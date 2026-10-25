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
            if (mission == null) return states;
            // A load that is waiting for a request to finish has already decided the state.
            if (mission.DeferredRestore != null && mission.DeferredRestore.State != null) return new List<MissionSaveState> { mission.DeferredRestore.State };
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
                MissionPlan = mission.Plan,
                SequencePlan = mission.PendingSequence,
            };
            if (mission.UnsafeProblems != null) state.UnsafeProblems.AddRange(mission.UnsafeProblems);
            foreach (var setting in mission.Settings) state.Settings[setting.Key] = setting.Value;
            states.Add(state);
            return states;
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

            var restore = Choose(restored.States.Where(s => !Dismissed(s)).ToList(), existing);
            if (restore == null)
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
            if (state == null) state = states.FirstOrDefault(s => s.Stage != MissionStage.Succeeded && s.Stage != MissionStage.Ended && s.Stage != MissionStage.Lost);
            if (state == null) state = states.FirstOrDefault();
            return state == null ? null : new Restore { State = state };
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
            };
            foreach (var setting in state.Settings) mission.Settings[setting.Key] = setting.Value;
            RestoreChildVessels(mission, state.ChildVessels);

            // The conversation is the transcript's, whole: turns past the save's marker are kept.
            var transcript = MissionStore.ReadTranscript(mission.Folder);
            if (transcript != null && transcript.System.Count > 0)
            {
                mission.System.AddRange(transcript.System);
                mission.Conversation.AddRange(transcript.Messages);
                foreach (var m in transcript.Messages)
                    if (m.Effort != null) mission.Effort = m.Effort;
            }
            else
            {
                mission.System.Add(MissionPrompts.BaseLayer);
                mission.System.Add(MissionPrompts.MissionLayer(state.Id));
                mission.System.Add(MissionPrompts.TimingLayer(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config))));
            }
            if (_config.DebugTools) mission.Dump = new FileStreamDump(Path.Combine(mission.Folder, "stream-dumps"));

            // Spend is never the save's: the cost log is never rewound, so a load cannot refund anything.
            var cost = MissionStore.ReadCostTotals(mission.Folder);
            mission.Spent = cost.Spent;
            mission.Held = cost.Held;
            mission.Requests = cost.Requests;
            mission.Warned = mission.Committed >= mission.SpendingCap * 0.8m;
            _mission = mission;

            if (restore.NoSnapshot) NoSnapshotMission(mission);
            else Reconcile(mission, state, resumeAfterQuit);
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
            if (resumeAfterQuit) unanswered = ValidUnanswered(mission);
            else ClearUnanswered(mission);

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

            mission.PauseReason = state.PauseReason;
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
                    mission.OwedOutcome = new RunOutcome(RunOutcomeKind.Interrupted, null, "", mission.InterruptReason ?? "the game was loaded, quit or crashed while it ran", false);
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
            if (stage == MissionStage.AwaitingPlanApproval && mission.Plan != null)
            {
                // A Mission Plan with no sequences has no card: Claudia's line is what the player needs to see again.
                if (mission.Plan.Sequences.Count == 0 && !string.IsNullOrEmpty(state.LatestLine)) Note(LogKind.Claudia, state.LatestLine);
                OfferMissionPlan(mission, mission.Plan);
            }
            else if (stage == MissionStage.AwaitingSequenceApproval && mission.PendingSequence != null)
            {
                mission.Stage = MissionStage.AwaitingSequenceApproval;
                OfferSequencePlan(mission, mission.PendingSequence);
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
