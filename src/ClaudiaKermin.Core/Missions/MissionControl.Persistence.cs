// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// A Mission surviving scene changes, quicksaves, reverts, quits and crashes (spec: persistence).
    ///
    /// The save file holds a <see cref="MissionSaveState"/> per Mission (<see cref="ExportStates"/>, restored by
    /// <see cref="MissionsRestored"/>); the Mission folder holds everything that is never rewound: the transcript,
    /// the event log, <c>mission.json</c> and the one turn sent and not yet answered. Loading a save puts the loop
    /// state back to the save's and keeps the whole conversation, with a notice to Claudia on her next turn. A
    /// sequence that was Running has no program after a load, quit or crash, so it is interrupted (never
    /// restarted) and Debriefed once the craft is in Flight. A turn left unanswered by a quit or crash is re-sent.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>A turn the loop owes Claudia once the craft is in Flight (it needs a fresh Game Snapshot).</summary>
        private enum Owed
        {
            None,
            /// <summary>The next Sequence Plan.</summary>
            SequencePlan,
            /// <summary>The Debrief of a sequence that was interrupted.</summary>
            Debrief,
        }

        /// <summary>What a load puts back: a snapshot from the save, or (NoSnapshot) just the Mission folder.</summary>
        private sealed class Restore
        {
            public MissionSaveState State;
            public bool NoSnapshot;
        }

        /// <summary>The Mission's state for the save file; nothing when there is no Mission. Called whenever the game saves.</summary>
        public IList<MissionSaveState> ExportStates()
        {
            var states = new List<MissionSaveState>();
            var mission = _mission;
            if (mission != null) states.Add(StateOf(mission));
            // The Missions paused at a sequence boundary to make room for the active one.
            states.AddRange(_parked);
            return states;
        }

        private static MissionSaveState StateOf(Mission mission)
        {
            // A load that is waiting for a request to finish has already decided the state.
            if (mission.DeferredRestore != null && mission.DeferredRestore.State != null) return mission.DeferredRestore.State;
            var state = new MissionSaveState
            {
                Id = mission.Id,
                Goal = mission.Goal,
                Model = mission.Model,
                Stage = mission.Stage,
                StageBeforePause = mission.StageBeforePause,
                SpendingCap = mission.SpendingCap,
                TranscriptMarker = mission.Conversation.Count,
                SequencesProposed = mission.SequencesProposed,
                SequenceCounted = mission.SequenceCounted,
                LatestLine = mission.LatestLine,
                PauseReason = mission.PauseReason,
                PauseCause = mission.Stage == MissionStage.Paused && mission.PauseCause != PauseCause.Other ? mission.PauseCause.ToString() : "",
                VesselId = mission.VesselId,
                VesselName = mission.VesselName,
                DueNotified = mission.DueNotified,
                Remote = mission.Remote,
                EvaScience = mission.EvaScience,
                ManualPlanned = mission.ManualPlanned,
                ManualOrigin = mission.ManualOrigin,
                HandBack = mission.HandBack,
                HandBackMessage = mission.HandBackMessage,
                StopReason = mission.StopReason,
                InterruptReason = mission.InterruptReason,
                ProcessorPartId = mission.ProcessorPartId,
                ReducedGoal = mission.ReducedGoal,
                ChildVessels = ChildVesselsJson(mission),
                LeftTheCraft = LeftTheCraftJson(mission),
                Crossings = CrossingsJson(mission),
                RunEvents = RunEventsJson(mission),
                RunStartUt = mission.RunStartUt,
                ScriptError = mission.ScriptError ?? "",
                Recheck = mission.Recheck,
                RecheckSince = mission.RecheckSince,
                PendingNotice = mission.LoadNotice ?? "",
                PausedCannotFly = mission.PausedCannotFly,
                MissionPlan = mission.Plan,
                SequencePlan = mission.PendingSequence,
                QueuedEntries = mission.Log.Count(e => e.Queued),
            };
            state.BlackoutRevisions.AddRange(mission.BlackoutRevisions);
            state.HeldWords.AddRange(mission.HeldWords);
            if (mission.UnsafeProblems != null) state.UnsafeProblems.AddRange(mission.UnsafeProblems);
            foreach (var setting in mission.Settings) state.Settings[setting.Key] = setting.Value;
            return state;
        }

        // ---- The Mission folder ----

        private void RecordStart(Mission mission)
        {
            try
            {
                MissionStore.WriteInfo(mission.Folder, new MissionInfo { Id = mission.Id, Goal = mission.Goal, Model = mission.Model, SpendingCap = mission.SpendingCap });
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't write the Mission's record: " + e.Message);
            }
            Event(mission, "started", mission.Goal);
        }

        private void RecordFinished(Mission mission)
        {
            try
            {
                MissionStore.WriteFinished(mission.Folder, mission.Id, mission.Stage.ToString());
                MissionStore.WriteVessel(mission.Folder, mission.VesselId, mission.VesselName);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // Still finished in memory; a later load may offer it as a Paused Mission.
            }
            Event(mission, "finished", mission.Stage.ToString());
            ReleaseChildVessels(mission);
        }

        /// <summary>The turn about to be sent goes to the Mission folder first, so a quit or crash before the reply can re-send it.</summary>
        private void RecordUnanswered(Mission mission, Turn turn)
        {
            ApplyLoadNotice(mission, turn);
            // An Alert judges a live run, and no run survives a quit or a load: there is nothing to re-send it for.
            if (turn.Purpose == Purpose.Alert) return;
            try
            {
                MissionStore.WriteUnanswered(mission.Folder, new UnansweredTurn
                {
                    Purpose = turn.Purpose.ToString(),
                    Effort = turn.Effort,
                    UserText = turn.UserText,
                    Notice = turn.Notice,
                    PlayerText = turn.PlayerText,
                    ScriptRewrite = turn.ScriptRewrite,
                    Waits = turn.Waits,
                    BaseMessages = mission.Conversation.Count,
                });
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't record the request before sending it: " + e.Message);
            }
        }

        private static void ClearUnanswered(Mission mission)
        {
            try
            {
                MissionStore.DeleteUnanswered(mission.Folder);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The turn's base count no longer matches the transcript, so a stale file is ignored.
            }
        }

        private void Event(Mission mission, string kind, string text)
        {
            try
            {
                MissionStore.AppendEvent(mission.Folder, _ut, kind, text);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The event log is best effort; the Mission goes on.
            }
        }

        /// <summary>A notice of the mod's own that a rebuilt Mission log must show again (an approval, a script's start or end): in the log now, and in the event log for a load.</summary>
        private void Notice(string text)
        {
            var entry = Note(LogKind.Notice, text);
            var mission = _mission;
            if (mission == null || mission.Folder == null) return;
            try
            {
                MissionStore.AppendNotice(mission.Folder, _ut, mission.Conversation.Count, entry.Text);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // Best effort, like the rest of the event log.
            }
        }

        /// <summary>What the next turn says to Claudia about a load goes inside that turn's notice, after the user message.</summary>
        private static void ApplyLoadNotice(Mission mission, Turn turn)
        {
            if (mission.LoadNotice == null) return;
            turn.Notice = mission.LoadNotice + "\n\n" + turn.Notice;
            mission.LoadNotice = null;
        }

        /// <summary>The fresh Craft Check on Resume (a later ticket) goes here, before the Mission carries on.</summary>
        private static void CraftCheckOnResume(Mission mission)
        {
        }

        private static bool IsRunning(Mission mission)
        {
            return mission.Stage == MissionStage.Running
                || (mission.Stage == MissionStage.Paused && mission.StageBeforePause == MissionStage.Running);
        }

        // ---- Scene changes ----

        /// <summary>
        /// Leaving Flight cuts a running script off (the adapter has asked the player first). The sequence is
        /// marked interrupted and Debriefed on return; the program's own end report arrives late and is ignored.
        /// </summary>
        private void SceneChanging(GameScene scene)
        {
            var leaving = _scene == GameScene.Flight && scene != GameScene.Flight;
            _scene = scene;
            if (!leaving) return;
            _snapshot = null;
            _active = null;
            if (_mission != null) _mission.SwitchingAway = false;
            if (_mission != null && IsRunning(_mission)) Interrupt(_mission, "the player left Flight");
        }

        private void Interrupt(Mission mission, string reason)
        {
            if (mission.Stage == MissionStage.Paused) mission.StageBeforePause = MissionStage.Debriefing;
            else mission.Stage = MissionStage.Debriefing;
            EndLive(mission);
            mission.Owed = Owed.Debrief;
            mission.OwedOutcome = new RunOutcome(RunOutcomeKind.Interrupted, null, "", reason, false);
            mission.InterruptReason = reason;
            Note(LogKind.Notice, "The sequence was interrupted (" + reason + "). Claudia will Debrief it when you are back in "
                + (_scene == GameScene.Flight && mission.VesselName.Length > 0 ? mission.VesselName : "Flight") + ".");
            Event(mission, "interrupted", reason);
        }

        /// <summary>Asks Claudia for a turn the loop owes her, once the craft is in Flight with a fresh Game Snapshot and no request is out.</summary>
        private void TryOwed()
        {
            var mission = _mission;
            if (mission == null || mission.Owed == Owed.None || mission.Pending != null || mission.InFlight) return;
            // It needs the craft's own facts: with the player in another vessel it waits until they are back in the craft.
            if (mission.Stage == MissionStage.Paused || !CraftActive || mission.SwitchingAway) return;

            Turn turn;
            if (mission.Owed == Owed.Debrief && mission.Stage == MissionStage.Debriefing)
            {
                var plan = mission.PendingSequence ?? new SequencePlan { Name = "(unknown)" };
                NoteRunOutcome(mission, mission.OwedOutcome);
                turn = mission.HandBack ? HandBackTurn(mission) : new Turn
                {
                    Purpose = Purpose.Debrief,
                    Effort = "high",
                    UserText = MissionPrompts.RunReport(plan, StoppedOutcome(mission, mission.OwedOutcome), CrossingsText(mission)) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                    Notice = MissionPrompts.DebriefNotice,
                };
            }
            else if (mission.Owed == Owed.SequencePlan && mission.Stage == MissionStage.Sequencing)
            {
                turn = new Turn
                {
                    Purpose = Purpose.SequencePlan,
                    Effort = "high",
                    UserText = "Continue the Mission.\n" + MissionPrompts.Facts(_ut, _snapshot),
                    Notice = MissionPrompts.SequenceNotice,
                };
            }
            else
            {
                mission.Owed = Owed.None;
                return;
            }
            mission.Owed = Owed.None;
            mission.OwedOutcome = null;
            mission.InterruptReason = null;
            Begin(turn);
        }

        // ---- Loading a save ----

        private void Restored(MissionsRestored restored)
        {
            Loaded(restored.SaveDirectory);
            if (_saveDirectory == null) return;
            var existing = _mission;
            // The craft was reloaded: the last Game Snapshot is the old craft's. Facts wait for a fresh one.
            _snapshot = null;
            _active = null;
            _craftLink = null;

            // A Mission deleted from the history may still have a state in an older save: without its folder it is gone, not restored.
            // A dismissed one keeps its folder but is history too.
            var states = restored.States.Where(s => Directory.Exists(MissionFolder.For(_saveDirectory, s.Id)) && !Dismissed(s)).ToList();
            var restore = Choose(states, existing);
            // The save's other unfinished Missions wait Paused at the boundary they were parked at; finished ones are history, kept in their folders.
            _parked.Clear();
            _switch = null;
            _historyVersion++;
            foreach (var state in states)
                if ((restore == null || state.Id != restore.State.Id) && !Finished(state.Stage) && _parked.All(p => p.Id != state.Id))
                {
                    state.Active = false;
                    _parked.Add(state);
                }
            if (restore == null && states.Count == 0)
            {
                // A Mission folder this save knows nothing of, if there is one the player hasn't finished with.
                var info = MissionStore.NewestResumable(_saveDirectory);
                if (info != null) restore = NoSnapshot(info);
            }
            var target = restore == null ? null : restore.State.Id;

            if (existing != null && existing.Id != target)
            {
                if (existing.InFlight) existing.Pending.Cancel.Cancel();
                _mission = null;
                existing = null;
            }
            if (restore == null) return;

            if (existing != null && existing.InFlight)
            {
                // Let it complete: Claudia keeps her reply, and the loop state is the save's once it has landed.
                existing.DeferredRestore = restore;
                return;
            }
            // A Mission already running in this game was reloaded; a Mission not in memory is resumed after a quit or crash.
            Reinstate(restore, existing == null);
        }

        private static Restore Choose(IList<MissionSaveState> states, Mission existing)
        {
            MissionSaveState state = null;
            if (existing != null) state = states.FirstOrDefault(s => s.Id == existing.Id);
            // The Mission in play when the save was made, finished or not; a save with only parked Missions has none.
            if (state == null) state = states.FirstOrDefault(s => s.Active);
            return state == null ? null : new Restore { State = state };
        }

        private static bool Finished(MissionStage stage)
        {
            return stage == MissionStage.Succeeded || stage == MissionStage.Ended || stage == MissionStage.Lost;
        }

        private static Restore NoSnapshot(MissionInfo info)
        {
            return new Restore
            {
                NoSnapshot = true,
                State = new MissionSaveState
                {
                    Id = info.Id,
                    Goal = info.Goal,
                    Model = info.Model,
                    SpendingCap = info.SpendingCap,
                    Stage = MissionStage.Paused,
                    StageBeforePause = MissionStage.WaitingForFlight,
                },
            };
        }

        private void CompleteDeferredRestore(Mission mission)
        {
            var restore = mission.DeferredRestore;
            mission.DeferredRestore = null;
            if (mission != _mission) return;
            Reinstate(restore, false);
        }

        /// <summary>Makes the Mission in <paramref name="restore"/> the Mission in play, from the save's snapshot and the Mission folder.</summary>
        /// <param name="resumeAfterQuit">The game has just started: a turn left unanswered is re-sent. Within a running game it is dropped; the save's state wins.</param>
        private void Reinstate(Restore restore, bool resumeAfterQuit)
        {
            var state = restore.State;
            var mission = new Mission
            {
                Id = state.Id,
                Goal = state.Goal,
                Model = state.Model,
                SpendingCap = state.SpendingCap,
                Folder = MissionFolder.For(_saveDirectory, state.Id),
                Plan = state.MissionPlan,
                PendingSequence = state.SequencePlan,
                SequencesProposed = state.SequencesProposed,
                SequenceCounted = state.SequenceCounted,
                UnsafeProblems = state.UnsafeProblems.ToList(),
                LatestLine = state.LatestLine,
                VesselId = state.VesselId,
                VesselName = state.VesselName,
                Remote = state.Remote,
                EvaScience = state.EvaScience ?? "",
                ManualPlanned = state.ManualPlanned,
                ManualOrigin = state.ManualOrigin,
                HandBack = state.HandBack,
                HandBackMessage = state.HandBackMessage,
                ProcessorPartId = state.ProcessorPartId,
                InterruptReason = state.InterruptReason,
                ReducedGoal = state.ReducedGoal ?? "",
                Recheck = state.Recheck ?? "",
                RecheckSince = state.RecheckSince,
            };
            foreach (var setting in state.Settings) mission.Settings[setting.Key] = setting.Value;
            RestoreChildVessels(mission, state.ChildVessels);
            RestoreLeftTheCraft(mission, state.LeftTheCraft);
            RestoreCrossings(mission, state.Crossings);
            RestoreRunEvents(mission, state.RunEvents);
            mission.RunStartUt = state.RunStartUt;
            mission.ScriptError = state.ScriptError ?? "";

            // The conversation is the transcript's, whole: turns past the save's marker are kept.
            var transcript = MissionStore.ReadTranscript(mission.Folder);
            if (transcript != null && transcript.System.Count > 0)
            {
                mission.System.AddRange(transcript.System);
                mission.Conversation.AddRange(transcript.Messages);
                foreach (var m in transcript.Messages)
                    if (m.Effort != null) mission.Effort = m.Effort;
                // The Mission log is the transcript's again: the player's words, Claudia's lines, the plan cards and the Debriefs.
                mission.Log.AddRange(MissionLogRebuild.From(transcript, state.Goal, mission.VesselName, _config, MissionStore.ReadNotices(mission.Folder)));
                mission.LogRebuilt = transcript.Messages.Count > 0;
            }
            else
            {
                mission.System.Add(MissionPrompts.BaseLayer);
                mission.System.Add(MissionPrompts.MissionLayer(state.Id, state.Goal));
                mission.System.Add(MissionPrompts.TimingLayer(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config))));
            }
            RestoreBlackout(mission, state);
            if (_config.DebugTools) mission.Dump = new FileStreamDump(Path.Combine(mission.Folder, "stream-dumps"));

            // Spend is never the save's: the cost log is never rewound, so a load cannot refund anything.
            var cost = MissionStore.ReadCostTotals(mission.Folder);
            // A Mission that is over sends nothing more, so its spend can't be refunded into anything.
            if (cost.Unreadable && !Finished(restore.State.Stage))
            {
                // Unreadable is not zero spend (that would be a refund): nothing is sent until the log reads.
                _mission = mission;
                PauseForUnreadableCostLog(mission, restore, resumeAfterQuit);
                return;
            }
            mission.Spent = cost.Spent;
            mission.Held = cost.Held;
            mission.Requests = cost.Requests;
            // Nor is the cap: it only ever rises, and a raise is logged, so a cap raised after the save wins.
            if (cost.Cap.HasValue && cost.Cap.Value > mission.SpendingCap) mission.SpendingCap = cost.Cap.Value;
            mission.Warned = mission.Committed >= mission.SpendingCap * 0.8m;
            _mission = mission;

            if (restore.NoSnapshot) NoSnapshotMission(mission);
            else Reconcile(mission, state, resumeAfterQuit);
        }

        /// <summary>
        /// A Blackout that was running at the save: the revisions, the player's held words and Claudia's queued messages come
        /// back with it, so the link-back summary and held words survive. The link is not known after a load, so the Blackout
        /// waits for the next link fact to end (or go on). A Mission that is over has no Blackout.
        /// </summary>
        private static void RestoreBlackout(Mission mission, MissionSaveState state)
        {
            if (Finished(state.Stage)) return;
            mission.BlackoutRevisions.AddRange(state.BlackoutRevisions);
            mission.HeldWords.AddRange(state.HeldWords);
            // Her entries in a Blackout are the log's latest ones of hers (the Mission log is rebuilt from the transcript).
            var left = state.QueuedEntries;
            for (var i = mission.Log.Count - 1; i >= 0 && left > 0; i--)
            {
                var kind = mission.Log[i].Kind;
                if (kind == LogKind.Notice || kind == LogKind.Player) continue;
                mission.Log[i].Queued = true;
                left--;
            }
            if (mission.BlackoutRevisions.Count > 0 || mission.HeldWords.Count > 0 || mission.Log.Any(e => e.Queued))
            {
                mission.Blackout = true;
                mission.BlackoutRestored = true;
            }
        }

        private const string CostLogUnreadableReason = "The Mission's cost log can't be read, so what it has spent isn't known. "
            + "Fix the file (its permissions, or whatever else holds it open), then Resume; or End Mission.";

        private void PauseForUnreadableCostLog(Mission mission, Restore restore, bool resumeAfterQuit)
        {
            var state = restore.State;
            var before = state.Stage == MissionStage.Paused ? state.StageBeforePause : state.Stage;
            mission.StageBeforePause = Settle(mission, before);
            mission.Stage = MissionStage.Paused;
            mission.PauseCause = PauseCause.Other;
            mission.PauseReason = CostLogUnreadableReason;
            mission.AwaitingResume = true;
            mission.UnreadableCostLogRestore = restore;
            mission.UnreadableCostLogResumeAfterQuit = resumeAfterQuit;
            Note(LogKind.Notice, mission.PauseReason);
            Event(mission, "cost_log_unreadable", "the cost log could not be read; the Mission is paused");
            NeedsPlayer("Claudia's Mission is Paused.");
        }

        private void NoSnapshotMission(Mission mission)
        {
            mission.Stage = MissionStage.Paused;
            mission.StageBeforePause = MissionStage.WaitingForFlight;
            mission.AwaitingResume = true;
            mission.PauseReason = "The save you loaded has no record of this Mission, so it can't say where the Mission stood. "
                + "Resume it with a fresh plan, or End Mission.";
            mission.LoadNotice = "The player loaded a save that has no record of this Mission, so the Mission was paused and "
                + "is being resumed from its folder: the conversation above still happened and stays as it is. Plans, scripts and "
                + "runs it describes may not have happened in this timeline. Treat the Game Snapshot as the truth about the craft "
                + "and plan afresh from it.";
            Note(LogKind.Notice, mission.PauseReason);
            Event(mission, "no_snapshot", "loaded a save with no snapshot of this Mission");
        }

        /// <summary>Brings the loop state back in line with the game.</summary>
        private void Reconcile(Mission mission, MissionSaveState state, bool resumeAfterQuit)
        {
            var count = mission.Conversation.Count;
            UnansweredTurn unanswered = null;
            // A Paused save keeps its unanswered turn through a quickload too: Retry, Retry with a note and Choose model resend it.
            if (resumeAfterQuit || state.Stage == MissionStage.Paused) unanswered = ValidUnanswered(mission);
            else ClearUnanswered(mission);
            // The file is the latest turn sent, not the save's: a Paused save only owns it when it is the request it paused from.
            if (unanswered != null && !resumeAfterQuit && state.Stage == MissionStage.Paused)
            {
                var kept = StageOf((Purpose)Enum.Parse(typeof(Purpose), unanswered.Purpose));
                if (kept != MissionStage.None && kept != state.StageBeforePause) unanswered = null;
            }

            if (count > state.TranscriptMarker)
            {
                mission.LoadNotice = "The game was loaded from an earlier save, so the Mission's state went back to: "
                    + Describe(state.Stage == MissionStage.Paused ? state.StageBeforePause : state.Stage)
                    + ". The conversation above still happened and stays as it is. "
                    + "Plans, scripts and runs after that save did not happen in this timeline; treat the Game Snapshot as the truth about the craft.";
                Note(LogKind.Notice, "Loaded an earlier save: the Mission is back at " + Describe(state.Stage == MissionStage.Paused ? state.StageBeforePause : state.Stage)
                    + ". Claudia's conversation was kept and she will be told.");
                Event(mission, "restored", "an earlier save was loaded; the conversation (" + count + " messages) was kept past the save's marker (" + state.TranscriptMarker + ")");
            }

            // What Claudia was still to be told when the save was made (a docking, say) is still owed, ahead of what the load adds.
            if (!string.IsNullOrEmpty(state.PendingNotice))
                mission.LoadNotice = state.PendingNotice + (mission.LoadNotice != null ? "\n\n" + mission.LoadNotice : "");

            mission.PauseReason = state.PauseReason;
            // An unknown or missing cause (an older save) is a plain Pause.
            PauseCause cause;
            mission.PauseCause = state.Stage == MissionStage.Paused && Enum.TryParse(state.PauseCause, out cause) && Enum.IsDefined(typeof(PauseCause), cause)
                ? cause : PauseCause.Other;
            mission.PausedCannotFly = state.PausedCannotFly && state.Stage == MissionStage.Paused;
            // A run the mod stopped itself is Debriefed with its reason even after a load (no timer: no run survives one).
            mission.StopReason = state.StopReason;
            mission.RunDeadlineUt = null;
            if (unanswered != null)
            {
                var purpose = (Purpose)Enum.Parse(typeof(Purpose), unanswered.Purpose);
                mission.Pending = new Turn
                {
                    Purpose = purpose,
                    Effort = unanswered.Effort,
                    UserText = unanswered.UserText,
                    Notice = unanswered.Notice,
                    PlayerText = unanswered.PlayerText,
                    ScriptRewrite = unanswered.ScriptRewrite,
                    Waits = unanswered.Waits,
                };
                var requestStage = StageOf(purpose);
                var settled = requestStage != MissionStage.None ? requestStage : Settle(mission, state.Stage == MissionStage.Paused ? state.StageBeforePause : state.Stage);
                // A plan or Debrief request in hand is what the loop was waiting for; a question changes nothing.
                if (requestStage != MissionStage.None) mission.Owed = Owed.None;
                OfferCards(mission, settled, state);
                if (state.Stage == MissionStage.Paused)
                {
                    mission.StageBeforePause = settled;
                    mission.Stage = MissionStage.Paused;
                }
                else
                {
                    mission.Stage = settled;
                    Event(mission, "resent", "re-sending the unanswered " + purpose + " request");
                    Note(LogKind.Notice, "Claudia's last request was never answered; sending it again.");
                    ApplyLoadNotice(mission, mission.Pending);
                    Send(mission, mission.Pending);
                }
                return;
            }

            if (unanswered == null && AdvanceFromTranscript(mission, state)) return;

            if (state.Stage == MissionStage.Paused)
            {
                mission.StageBeforePause = Settle(mission, state.StageBeforePause);
                mission.Stage = MissionStage.Paused;
                return;
            }
            var stage = Settle(mission, state.Stage);
            OfferCards(mission, stage, state);
            mission.Stage = stage;
        }

        /// <summary>
        /// The save was made while a Mission Plan, Sequence Plan or Debrief request was out, and the request has since completed:
        /// its reply is the transcript's last message. Offer that plan for Approval (or apply that Debrief) instead of asking
        /// again. A reply that doesn't fit the request, or a script that no longer passes the Script Check, leaves the usual re-ask.
        /// A Paused save counts by the stage it paused from; the reply is applied and the Mission stays Paused, with its Pause cause.
        /// </summary>
        private bool AdvanceFromTranscript(Mission mission, MissionSaveState state)
        {
            Purpose purpose;
            var paused = state.Stage == MissionStage.Paused;
            var inFlight = paused ? state.StageBeforePause : state.Stage;
            if (inFlight == MissionStage.Planning) purpose = Purpose.MissionPlan;
            else if (inFlight == MissionStage.Sequencing) purpose = Purpose.SequencePlan;
            // A Debrief would move the Mission on past its Pause, so a Paused one is asked again as before (applying it while Paused is still open in #213).
            else if (inFlight == MissionStage.Debriefing && !paused) purpose = Purpose.Debrief;
            else return false;

            // The in-flight turn joined the conversation after the save, with its reply: the first assistant message past the marker.
            var messages = mission.Conversation;
            var first = messages.Skip(state.TranscriptMarker).FirstOrDefault(m => m.Role == Role.Assistant);
            if (first == null) return false;
            var reply = MissionReply.Parse(string.Concat(first.Content.OfType<TextBlock>().Select(b => b.Text)));
            if (reply == null || Mismatch(purpose, reply) != null) return false;
            if (purpose == Purpose.SequencePlan && !reply.SequencePlan.Manual && !CheckScript(reply.SequencePlan).Passed) return false;

            mission.Owed = Owed.None;
            mission.LatestLine = SayWithWhy(purpose, reply);
            // A log rebuilt from the transcript already holds Claudia's line and the plan's card.
            mission.SuppressCards = mission.LogRebuilt;
            if (purpose == Purpose.Debrief)
            {
                // The run is over and its Debrief is in: the loop goes on from it as it did when the reply first landed.
                mission.SequencesProposed = state.SequencesProposed;
                mission.SequenceCounted = state.SequenceCounted;
                mission.OwedOutcome = null;
                mission.InterruptReason = null;
                Debriefed(mission, reply);
                mission.SuppressCards = false;
                Event(mission, "advanced", "the reply to the in-flight Debrief request was already in the transcript");
                Note(LogKind.Notice, "Claudia's last request had already been answered; her Debrief was applied.");
                return true;
            }
            if (purpose == Purpose.MissionPlan)
            {
                OfferMissionPlan(mission, reply.MissionPlan);
                mission.SequencesProposed = state.SequencesProposed;
            }
            else
            {
                mission.SequenceCounted = state.SequenceCounted;
                mission.SequencesProposed = state.SequencesProposed;
                OfferSequencePlan(mission, reply.SequencePlan);
                var rebuilt = mission.LogRebuilt ? mission.Log.LastOrDefault(e => e.Kind == LogKind.SequencePlan) : null;
                if (rebuilt != null) rebuilt.Text = MissionCards.SequencePlanBody(reply.SequencePlan, mission.VesselName, _ut, _config);
            }
            mission.SuppressCards = false;
            Event(mission, "advanced", "the reply to the in-flight " + purpose + " request was already in the transcript");
            if (!paused) Note(LogKind.Notice, "Claudia's last request had already been answered; her reply is offered for your Approval.");
            else
            {
                Note(LogKind.Notice, "Claudia's last request had already been answered; her reply is kept for when you Resume, and the Mission stays Paused"
                    + (string.IsNullOrEmpty(state.PauseReason) ? "." : ": " + state.PauseReason));
                // The offer moved the stage on; the Pause and its cause stand, and Resume lands on the offer.
                mission.StageBeforePause = mission.Stage;
                mission.Stage = MissionStage.Paused;
            }
            return true;
        }

        /// <summary>
        /// The stage a saved stage becomes with nothing in flight. A sequence that was Running (or being
        /// Debriefed) has no program now: it is interrupted, never restarted, and owed a Debrief. A plan that
        /// was being written is asked for again. A Sequence Plan from the save is Script-Checked again before it
        /// can be approved (ADR 0003): the save file is not trusted, and the check or the Core Library may have
        /// changed since; one that fails is "Couldn't produce a safe script".
        /// </summary>
        private MissionStage Settle(Mission mission, MissionStage stage)
        {
            switch (stage)
            {
                case MissionStage.Running:
                case MissionStage.Debriefing:
                    mission.Owed = Owed.Debrief;
                    mission.OwedOutcome = new RunOutcome(RunOutcomeKind.Interrupted, null, mission.ScriptError, mission.InterruptReason ?? "the game was loaded, quit or crashed while it ran", false);
                    return MissionStage.Debriefing;
                case MissionStage.Sequencing:
                    mission.Owed = Owed.SequencePlan;
                    return MissionStage.Sequencing;
                case MissionStage.AwaitingSequenceApproval:
                    // A manual stretch has no script to check.
                    if (mission.PendingSequence != null && mission.PendingSequence.Manual) return stage;
                    if (mission.PendingSequence != null)
                    {
                        var check = CheckScript(mission.PendingSequence);
                        if (check.Passed) return stage;
                        mission.UnsafeProblems = check.Problems;
                        mission.PendingSequence = null;
                        return MissionStage.UnsafeScript;
                    }
                    mission.Owed = Owed.SequencePlan;
                    return MissionStage.Sequencing;
                case MissionStage.AwaitingPlanApproval:
                    return mission.Plan != null ? stage : MissionStage.WaitingForFlight;
                case MissionStage.Planning:
                case MissionStage.None:
                case MissionStage.Paused:
                    return MissionStage.WaitingForFlight;
                default:
                    return stage;
            }
        }

        private static MissionStage StageOf(Purpose purpose)
        {
            switch (purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings: return MissionStage.Planning;
                case Purpose.SequencePlan: return MissionStage.Sequencing;
                case Purpose.Debrief: return MissionStage.Debriefing;
                default: return MissionStage.None;
            }
        }

        /// <summary>The plan awaiting Approval is shown again as a card, so the player sees what they approve.</summary>
        private void OfferCards(Mission mission, MissionStage stage, MissionSaveState state)
        {
            // A log rebuilt from the transcript already holds the plan's card (and Claudia's line when it had none): offering the plan again adds no second one.
            if (stage == MissionStage.AwaitingPlanApproval && mission.Plan != null)
            {
                // A Mission Plan with no sequences has no card: Claudia's line is what the player needs to see again.
                if (mission.Plan.Sequences.Count == 0 && !string.IsNullOrEmpty(state.LatestLine) && !mission.LogRebuilt) Note(LogKind.Claudia, state.LatestLine);
                mission.SuppressCards = mission.LogRebuilt;
                OfferMissionPlan(mission, mission.Plan);
                mission.SuppressCards = false;
            }
            else if (stage == MissionStage.AwaitingSequenceApproval && mission.PendingSequence != null)
            {
                mission.Stage = MissionStage.AwaitingSequenceApproval;
                mission.SuppressCards = mission.LogRebuilt;
                OfferSequencePlan(mission, mission.PendingSequence);
                mission.SuppressCards = false;
                // The rebuilt card had no "now" to say its Due Time is "in" so long against; the one awaiting Approval has one again.
                var rebuilt = mission.LogRebuilt ? mission.Log.LastOrDefault(e => e.Kind == LogKind.SequencePlan) : null;
                if (rebuilt != null) rebuilt.Text = MissionCards.SequencePlanBody(mission.PendingSequence, mission.VesselName, _ut, _config);
                mission.DueNotified = state.DueNotified;
            }
            else if (stage == MissionStage.UnsafeScript) UnsafeScriptCard(mission, null);
            else if (stage == MissionStage.Lost) LostDebrief(mission, "");
            else if (stage == MissionStage.AwaitingReducedGoal) Note(LogKind.Notice, "Claudia offered a reduced goal for your Approval: " + mission.ReducedGoal);
            mission.SequencesProposed = state.SequencesProposed;
            mission.LatestLine = state.LatestLine;
        }

        /// <summary>The turn sent and never answered, if the transcript has not moved past it.</summary>
        private static UnansweredTurn ValidUnanswered(Mission mission)
        {
            UnansweredTurn turn;
            try
            {
                turn = MissionStore.ReadUnanswered(mission.Folder);
            }
            catch (Exception e) when (IsFileError(e))
            {
                return null;
            }
            Purpose purpose;
            if (turn == null || turn.BaseMessages != mission.Conversation.Count || !Enum.TryParse(turn.Purpose, out purpose)) return null;
            // Never recorded, and no run is left to judge: an Alert in the folder is not re-sent.
            return purpose == Purpose.Alert ? null : turn;
        }

        private static string Describe(MissionStage stage)
        {
            switch (stage)
            {
                case MissionStage.AwaitingPlanApproval: return "waiting for your Approval of the Mission Plan";
                case MissionStage.AwaitingSequenceApproval: return "waiting for your Approval of a Sequence Plan";
                case MissionStage.Running:
                case MissionStage.Debriefing: return "a Maneuver Sequence that was Running (now interrupted)";
                case MissionStage.UnsafeScript: return "Claudia couldn't produce a safe script";
                case MissionStage.ManualControl: return "Manual Control: the player flying the craft";
                case MissionStage.Succeeded: return "the Mission Goal met";
                case MissionStage.Ended: return "ended";
                case MissionStage.AwaitingReducedGoal: return "waiting for your Approval of a reduced goal";
                case MissionStage.Lost: return "Lost (Claudia was killed aboard)";
                default: return "planning";
            }
        }
    }
}
