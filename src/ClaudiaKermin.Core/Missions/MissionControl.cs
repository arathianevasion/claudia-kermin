// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The core's one seam with the adapter, and the Mission loop behind it (ADR 0002: the mod owns
    /// execution). In: <see cref="Send"/> takes player actions, Game Snapshots, game events, kOS run events
    /// and UT. Out: <see cref="TakeCommands"/> (adapter commands), <see cref="View"/> (the UI view model) and
    /// writes to the Mission folder. Main thread only; Claude's replies arrive through <see cref="Background"/>,
    /// which the adapter drains each frame. One request per Mission is in flight at a time.
    ///
    /// No script reaches Approval or runs unless it passes the Script Check (ADR 0003, <see cref="CheckScript"/>):
    /// a fresh plan, a rewrite, a retry, a re-plan and a plan restored from a save alike.
    ///
    /// The Spending Cap pre-send check is <see cref="MaySend"/>, the Craft Check runs before a Mission starts,
    /// and persistence of the loop state across saves lives in MissionControl.Persistence.cs.
    /// </summary>
    public sealed partial class MissionControl
    {
        private readonly ClaudeClient _client;
        private readonly Background _background;
        private readonly PlayerConfig _config;
        private readonly Func<bool> _hasKey;
        private readonly IScriptCheck _scriptCheck;
        private readonly List<AdapterCommand> _commands = new List<AdapterCommand>();

        private string _saveDirectory;
        private GameScene _scene = GameScene.Other;
        private double _ut;
        private GameSnapshot _snapshot;
        private Mission _mission;
        private int _lastRunId;
        private MissionView _view = new MissionView();
        private readonly CraftCheckFlow _craftCheck;
        /// <summary>Start Mission was pressed and the Craft Check it triggered is running; a Pass lets the Mission start.</summary>
        private StartMission _pendingStart;
        /// <summary>Why the last Start Mission was turned away before the Craft Check; "" when it wasn't.</summary>
        private string _startRefusal = "";

        public MissionControl(ClaudeClient client, Background background, PlayerConfig config, Func<bool> hasKey, IScriptCheck scriptCheck)
        {
            _client = client;
            _background = background;
            _config = config;
            _hasKey = hasKey;
            _scriptCheck = scriptCheck;
            _craftCheck = new CraftCheckFlow(client, background, config, hasKey, CraftCheckCompleted);
            Rebuild();
        }

        /// <summary>The view model as of the last input or response. Replaced, never mutated, so it is safe to hold for a frame.</summary>
        public MissionView View
        {
            get { return _view; }
        }

        /// <summary>The adapter commands issued since the last call, oldest first.</summary>
        public IList<AdapterCommand> TakeCommands()
        {
            var taken = _commands.ToList();
            _commands.Clear();
            return taken;
        }

        public void Send(MissionInput input)
        {
            if (input is GameSnapshot) Snapshot((GameSnapshot)input);
            else if (input is SaveLoaded) Loaded(((SaveLoaded)input).SaveDirectory);
            else if (input is MissionsRestored) Restored((MissionsRestored)input);
            else if (input is SceneChanged) SceneChanging(((SceneChanged)input).Scene);
            else if (input is UtAdvanced) _ut = ((UtAdvanced)input).Ut;
            else if (input is StartMission) Start((StartMission)input);
            else if (input is RunCraftCheck) RunCraftCheck((RunCraftCheck)input);
            else if (input is AcknowledgeWarning) _craftCheck.Acknowledge(((AcknowledgeWarning)input).Code);
            else if (input is ChooseProcessor) _craftCheck.Choose(((ChooseProcessor)input).PartId, _snapshot);
            else if (input is EndMission) EndRequested();
            else if (input is Approve) Approved();
            else if (input is ConfirmApproval) Confirmed(((ConfirmApproval)input).Yes);
            else if (input is Reply) Replied(((Reply)input).Text);
            else if (input is ChangeSomething) ChangeRequested(((ChangeSomething)input).Text);
            else if (input is AskQuestion) Asked(((AskQuestion)input).Text);
            else if (input is Retry || input is Resume) Retried();
            else if (input is RaiseCap) Raised(((RaiseCap)input).SpendingCap);
            else if (input is RetryWithNote) ScriptRetried(((RetryWithNote)input).Note ?? "", false);
            else if (input is Replan) Replanned();
            else if (input is TakeManualControl) TakeManual("the Manual Control button");
            else if (input is ManualInputHeld) InputHeld(((ManualInputHeld)input).Seconds);
            else if (input is HandBack) HandedBack(((HandBack)input).Message);
            else if (input is CaptureSnapshot) Capture();
            else if (input is KosRunRefused) RunRefused((KosRunRefused)input);
            else if (input is KosRunEnded) RunEnded((KosRunEnded)input);
            else if (input is EditSettings) SettingsEdited(((EditSettings)input).Settings);
            else if (input is WarpToDueTime) WarpRequested();
            else if (input is GoToVessel) GoToRequested();
            else if (input is ClaudiaKilled) Killed();
            else if (input is KosRunBlocked) RunBlocked((KosRunBlocked)input);
            CheckDue();
            CheckRunaway();
            TryPlan();
            TryOwed();
            Rebuild();
        }

        // ---- The Mission and its turns ----

        private enum Purpose
        {
            MissionPlan,
            SequencePlan,
            Debrief,
            Reply,
            Change,
            Question,
            /// <summary>Mission Settings were edited: a revised Mission Plan is asked for.</summary>
            Settings,
        }

        /// <summary>
        /// One request's worth of appended messages: an effort change when needed, a user message (facts or
        /// the player's words) and a mod notice. Built when it is first sent and kept unchanged for Retry;
        /// appended to the conversation, with the reply, only once a usable reply arrives.
        /// </summary>
        private sealed class Turn
        {
            public Purpose Purpose;
            public string Effort;
            public string UserText;
            public string Notice;
            /// <summary>A rewrite after a failed Script Check: if this script fails too, there is no further automatic rewrite.</summary>
            public bool ScriptRewrite;
            /// <summary>
            /// Sent while a run is live or near one (a hand-back Debrief and its revision, a late-Approval re-plan): at the
            /// Spending Cap it waits for Raise cap instead of Pausing the Mission (<see cref="WaitForCap"/>). A turn sent
            /// while Running always waits.
            /// </summary>
            public bool Waits;
            public List<Message> Messages;
            public CancellationTokenSource Cancel;
        }

        private sealed partial class Mission
        {
            public string Id;
            public string Goal;
            public decimal SpendingCap;
            public string Model;
            public string Folder;
            public readonly List<string> System = new List<string>();
            public readonly List<Message> Conversation = new List<Message>();
            /// <summary>The effort the committed conversation last set; null before the first turn.</summary>
            public string Effort;
            public MissionStage Stage;
            public MissionStage StageBeforePause;
            /// <summary>The processor the running script was started on.</summary>
            public uint? RunningProcessor;
            /// <summary>The id of the current run; run events carrying any other id are late and ignored.</summary>
            public int? RunId;
            /// <summary>The runaway hard stop's UT for the current run (MissionControl.Gates.cs); null when nothing runs.</summary>
            public double? RunDeadlineUt;
            /// <summary>Why the mod stopped the current run ("blocked: …", "timed out"), for its Debrief; null otherwise.</summary>
            public string StopReason;
            public MissionPlan Plan;
            public SequencePlan PendingSequence;
            public int SequencesProposed;
            /// <summary>The Maneuver Sequence in hand has had a Sequence Plan offered (so revisions don't count again).</summary>
            public bool SequenceCounted;
            /// <summary>What the Script Check last found, while "Couldn't produce a safe script" is shown.</summary>
            public IList<string> UnsafeProblems;
            public readonly List<LogEntry> Log = new List<LogEntry>();
            public string LatestLine = "";
            public string Activity = "";
            public string PauseReason = "";
            public string ConfirmPrompt;
            /// <summary>In flight, or failed and waiting for Retry.</summary>
            public Turn Pending;
            public bool InFlight;
            /// <summary>A turn that must follow the one in flight (a Debrief after a mid-run question).</summary>
            public Turn Next;
            public IRawStreamDump Dump;
            /// <summary>The Mission Settings, by name; persisted with the Mission; EditSettings changes them (MissionSettingsValues reads and writes them).</summary>
            public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
            /// <summary>The Mission's vessel (its id and name), noted when the craft is first in Flight; the Go to button switches to it.</summary>
            public string VesselId = "";
            public string VesselName = "";
            /// <summary>The Due Time of the Sequence Plan awaiting Approval has been reached and announced (warp dropped, message posted).</summary>
            public bool DueNotified;
            /// <summary>Persistence (MissionControl.Persistence.cs): a turn the loop owes Claudia once the craft is in Flight.</summary>
            public Owed Owed;
            public RunOutcome OwedOutcome;
            /// <summary>Said to Claudia with the next turn (a save was loaded, or the Mission was resumed without a snapshot).</summary>
            public string LoadNotice;
            /// <summary>A save was loaded during a request: it completes, then this state is restored.</summary>
            public Restore DeferredRestore;
            /// <summary>No snapshot in the loaded save: Paused until the player resumes it or ends it.</summary>
            public bool AwaitingResume;
            /// <summary>Flown remotely: Claudia stays at Mission Control. False is crewed (she flies aboard).</summary>
            public bool Remote;
            /// <summary>While Paused for EVA science: what the player is to do by hand; "" otherwise.</summary>
            public string EvaScience = "";

            // ---- Spending (state #42 snapshots: SpendingCap, Spent, Held) ----
            /// <summary>Cost so far from real usage.</summary>
            public decimal Spent;
            /// <summary>Worst-case charges kept for cancelled and lost requests; never refunded.</summary>
            public decimal Held;
            /// <summary>The worst case of the request in flight; replaced by its real usage.</summary>
            public decimal PendingCharge;
            public bool Warned;
            /// <summary>Paused because of the Spending Cap, so Raise cap resumes it.</summary>
            public bool PausedForCap;
            /// <summary>The waiting call: the pending turn would pass the Spending Cap, so it waits unsent for Raise cap while the Mission carries on.</summary>
            public bool WaitingForCap;
            /// <summary>What the waiting call's screen message said, for the view; "" when nothing waits.</summary>
            public string CapWaitText = "";
            public int Requests;
            /// <summary>How the last request ended, for the cost log.</summary>
            public string Settlement = "";
            public string Outcome = "";
            public decimal Settled;

            public decimal Committed
            {
                get { return Spent + Held; }
            }

            /// <summary>Claudia answered the Mission Plan with no sequences: the goal can't be flown, and there is nothing to approve.</summary>
            public bool Declined
            {
                get { return Stage == MissionStage.AwaitingPlanApproval && Plan != null && Plan.Sequences.Count == 0; }
            }

            public bool AwaitingApproval
            {
                get { return Stage == MissionStage.AwaitingPlanApproval || Stage == MissionStage.AwaitingSequenceApproval; }
            }
        }

        private bool MissionOver
        {
            get { return _mission == null || _mission.Stage == MissionStage.Succeeded || _mission.Stage == MissionStage.Ended || _mission.Stage == MissionStage.Lost; }
        }

        private void Start(StartMission start)
        {
            if (!MissionOver) return;
            var goal = (start.Goal ?? "").Trim();
            if (goal.Length == 0 || start.SpendingCap <= 0 || !_config.Models.Contains(start.Model) || _saveDirectory == null) return;

            // A remote Mission needs a link (or the pad); a crewed one can't have Claudia while she is Missing. No request is spent on either.
            _startRefusal = StartRefusal(start);
            if (_startRefusal.Length > 0)
            {
                _pendingStart = null;
                NeedsPlayer(_startRefusal);
                return;
            }

            // No Mission starts on a craft that hasn't been checked for this goal, or whose check has a Blocker or an open Warning.
            if (!_craftCheck.IsCurrent(goal, _snapshot, start.Remote))
            {
                _pendingStart = start;
                _craftCheck.Run(goal, start.Model, _snapshot, start.Remote);
                if (!_craftCheck.Running) _pendingStart = null;
                return;
            }
            if (!_craftCheck.Cleared) return;
            _pendingStart = null;

            var id = "m" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 4);
            var mission = new Mission
            {
                Id = id,
                Goal = goal,
                SpendingCap = start.SpendingCap,
                Model = start.Model,
                Folder = MissionFolder.For(_saveDirectory, id),
                Stage = MissionStage.WaitingForFlight,
                Remote = start.Remote,
            };
            // Frozen for the whole Mission, so the cached prefix never changes.
            mission.System.Add(MissionPrompts.BaseLayer);
            mission.System.Add(MissionPrompts.MissionLayer(id));
            var settings = start.Settings != null ? start.Settings.Copy() : MissionSettingsValues.Defaults(_config);
            settings.WriteTo(mission.Settings);
            mission.System.Add(MissionPrompts.TimingLayer(settings, _config.WaitCapOrbits, _config.WaitCapGroundSeconds));
            // Raw stream dumps are a debug tool: on with debug_tools = true in the player config.
            if (_config.DebugTools) mission.Dump = new FileStreamDump(Path.Combine(mission.Folder, "stream-dumps"));
            _mission = mission;
            Note(LogKind.Player, "Mission Goal: " + goal);
            RecordStart(mission);
            // Started from the editor with her not aboard: the mod puts her in a free seat (the Craft Check made sure there is one).
            if (!mission.Remote && _scene == GameScene.Editor && _snapshot != null && !_snapshot.ClaudiaAboard && _snapshot.FreeSeats > 0)
            {
                _commands.Add(new SeatClaudia());
                Note(LogKind.Notice, "Claudia is taking a seat aboard.");
            }
        }

        /// <summary>Why this Start Mission can't go ahead before any Craft Check, or "".</summary>
        private string StartRefusal(StartMission start)
        {
            var snapshot = _snapshot;
            if (snapshot == null) return "";
            if (start.Remote)
            {
                // The launch pad counts as a link: Claudia can start the craft from there.
                if (!snapshot.Linked && snapshot.Situation != "PRELAUNCH")
                    return "A remote Mission needs a CommNet link to Kerbin, or the craft on the launch pad. Restore the link, then start it.";
                return "";
            }
            if (snapshot.ClaudiaStanding == "Missing" || snapshot.ClaudiaStanding == "Dead")
                return "Claudia is " + snapshot.ClaudiaStanding + " and can't fly aboard. A remote Mission can still start; a crewed one waits until she returns.";
            return "";
        }

        private void RunCraftCheck(RunCraftCheck run)
        {
            _pendingStart = null;
            _startRefusal = "";
            _craftCheck.Run(run.Goal, run.Model, _snapshot, run.Remote);
        }

        /// <summary>A Craft Check request ended: if Start Mission asked for it and it passed outright, the Mission starts now.</summary>
        private void CraftCheckCompleted()
        {
            var start = _pendingStart;
            _pendingStart = null;
            if (start != null && MissionOver && _craftCheck.Passed) Start(start);
            TryPlan();
            Rebuild();
        }

        private void TryPlan()
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.WaitingForFlight) return;
            if (_scene != GameScene.Flight || _snapshot == null) return;
            mission.VesselId = _snapshot.Craft.VesselId;
            mission.VesselName = _snapshot.Craft.Name;
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.MissionPlan,
                Effort = "high",
                UserText = "Mission Goal, in the player's words (quoted data): " + MissionPrompts.Quoted(mission.Goal) + "\n"
                    + MissionPrompts.MissionMode(mission.Remote)
                    + MissionPrompts.ModeFacts(mission.Remote, _snapshot) + "\n"
                    + "Spending Cap: $" + mission.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture) + ".\n"
                    + MissionPrompts.SettingsText(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config))) + "\n"
                    + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.PlanNotice,
            });
        }

        /// <summary>
        /// A Mission belongs to its save. Until Missions persist across saves (a later ticket), loading a
        /// different save drops the Mission in memory and cancels its request; its folder stays in the old save.
        /// </summary>
        private void Loaded(string saveDirectory)
        {
            if (saveDirectory == _saveDirectory) return;
            _saveDirectory = saveDirectory;
            _snapshot = null;
            _pendingStart = null;
            _startRefusal = "";
            _craftCheck.Reset();
            if (_mission == null) return;
            if (_mission.InFlight) _mission.Pending.Cancel.Cancel();
            _mission = null;
        }

        private void Snapshot(GameSnapshot snapshot)
        {
            var previous = _snapshot;
            _snapshot = snapshot;
            _craftCheck.OnSnapshot(snapshot);
            if (snapshot.Ut > _ut) _ut = snapshot.Ut;
            LinkSeen(previous, snapshot);
            var mission = _mission;
            if (mission == null) return;
            // A plan restored from a save was never offered against a snapshot: the first one after the load is its baseline.
            if (mission.AwaitingApproval && mission.PlanBaseline == null && !mission.InFlight && _scene == GameScene.Flight) mission.PlanBaseline = snapshot;
        }

        // ---- Approval ----

        private bool CanAnswerPlan
        {
            get { return _mission != null && _mission.AwaitingApproval && !_mission.InFlight; }
        }

        private void Approved()
        {
            if (!CanAnswerPlan || _mission.Declined) return;
            _mission.ConfirmPrompt = null;
            Approval();
        }

        private void Confirmed(bool yes)
        {
            if (!CanAnswerPlan || _mission.Declined || _mission.ConfirmPrompt == null) return;
            _mission.ConfirmPrompt = null;
            if (yes) Approval();
            else Note(LogKind.Notice, "Not approved yet.");
        }

        /// <summary>An Approval: the Approve button, or "Yes, approve" after a typed reply.</summary>
        private void Approval()
        {
            var mission = _mission;
            // Approve is Re-plan once the craft has changed beyond the plan's tolerances: a stale plan never runs.
            var stale = StaleBecause(mission);
            if (stale.Length > 0)
            {
                Note(LogKind.Notice, "The craft has changed since this plan was made (" + stale + "). Re-plan instead.");
                return;
            }
            if (mission.Stage == MissionStage.AwaitingPlanApproval)
            {
                Note(LogKind.Notice, "Mission Plan approved.");
                mission.Stage = MissionStage.Sequencing;
                mission.SequenceCounted = false;
                Begin(new Turn
                {
                    Purpose = Purpose.SequencePlan,
                    Effort = "high",
                    UserText = "The player approved the Mission Plan.\n" + MissionPrompts.Facts(_ut, _snapshot),
                    Notice = MissionPrompts.SequenceNotice,
                });
                return;
            }

            var plan = mission.PendingSequence;
            // The mod never starts a sequence before its Due Time, and never warps there for the player.
            if (plan.DueTimeUt.HasValue && _ut < plan.DueTimeUt.Value)
            {
                Note(LogKind.Notice, "Not before the Due Time, " + MissionClock.Ut(plan.DueTimeUt.Value) + " (in "
                    + MissionClock.Span(plan.DueTimeUt.Value - _ut) + "). Warp to Due Time, then Approve.");
                return;
            }
            if (WindowMissed(plan) && !TakeNextWindow(mission, plan)) return;
            if (plan.Manual)
            {
                StartPlannedManual(mission, plan);
                return;
            }
            var processor = _craftCheck.ProcessorFor(_snapshot);
            if (!processor.HasValue)
            {
                Note(LogKind.Notice, "Can't run the script: the craft has no kOS processor.");
                return;
            }
            // The plan was checked before it was offered; the bundle that starts is checked again, whatever path
            // (a reply, a restored save, a refused start) brought the plan here. No automatic rewrite: the plan
            // was offered already, so the player decides what happens next.
            var bundle = Bundle(plan);
            var check = _scriptCheck.Check(bundle);
            if (!check.Passed)
            {
                ShowUnsafeScript(mission, plan, check.Problems);
                return;
            }
            Note(LogKind.Notice, "Sequence Plan approved: the mod is starting the script.");
            mission.Stage = MissionStage.Running;
            if (plan.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            mission.RunningProcessor = processor.Value;
            mission.RunId = ++_lastRunId;
            StartRunawayTimer(mission, plan);
            _commands.Add(new StartScript(processor.Value, "claudia:" + mission.Id + ":" + plan.Name, bundle, mission.RunId.Value));
        }

        private void Replied(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || !CanAnswerPlan) return;
            _mission.ConfirmPrompt = null;
            Note(LogKind.Player, text);
            Begin(new Turn
            {
                Purpose = Purpose.Reply,
                Effort = "high",
                UserText = "The player replied (quoted data): " + MissionPrompts.Quoted(text),
                Notice = MissionPrompts.ReplyNotice(_mission.Stage == MissionStage.AwaitingPlanApproval),
            });
        }

        private void ChangeRequested(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || !CanAnswerPlan) return;
            _mission.ConfirmPrompt = null;
            Note(LogKind.Player, "Change: " + text);
            Begin(new Turn
            {
                Purpose = Purpose.Change,
                Effort = "high",
                UserText = "The player wants a change (quoted data): " + MissionPrompts.Quoted(text),
                Notice = MissionPrompts.ChangeNotice(_mission.Stage == MissionStage.AwaitingPlanApproval),
            });
        }

        private void Asked(string text)
        {
            text = (text ?? "").Trim();
            var mission = _mission;
            if (text.Length == 0 || mission == null || mission.InFlight || mission.Stage == MissionStage.Paused
                || mission.Stage == MissionStage.WaitingForFlight || mission.Stage == MissionStage.Lost) return;
            Note(LogKind.Player, text);
            Begin(new Turn
            {
                Purpose = Purpose.Question,
                Effort = "low",
                UserText = "The player asks (quoted data): " + MissionPrompts.Quoted(text),
                Notice = MissionPrompts.QuestionNotice,
            });
        }

        // ---- Timing: Due Times, late Approval and Mission Settings ----

        /// <summary>The Due Time of the Sequence Plan awaiting Approval, or null.</summary>
        private double? PendingDueTime
        {
            get
            {
                var mission = _mission;
                return mission != null && mission.Stage == MissionStage.AwaitingSequenceApproval && mission.PendingSequence != null
                    ? mission.PendingSequence.DueTimeUt : null;
            }
        }

        /// <summary>The plan's window has closed: it is later than the plan expected.</summary>
        private bool WindowMissed(SequencePlan plan)
        {
            return plan.WindowClosesUt.HasValue && _ut > plan.WindowClosesUt.Value;
        }

        /// <summary>
        /// Late Approval: the planned window has closed. Within tolerance, the script takes the next window and the
        /// player gets a one-line notice (true: carry on starting); otherwise Claudia is asked to re-plan (false).
        /// </summary>
        private bool TakeNextWindow(Mission mission, SequencePlan plan)
        {
            var next = plan.LateWindowDeltaV;
            var limit = plan.ExpectedDeltaV * (1 + _config.LateApprovalToleranceDvPercent / 100.0);
            if (next.HasValue && next.Value <= limit)
            {
                Note(LogKind.Notice, "Late Approval: the planned window closed at " + MissionClock.Ut(plan.WindowClosesUt.Value)
                    + "; the script takes the next window (" + MetresPerSecond(next.Value) + " against " + MetresPerSecond(plan.ExpectedDeltaV) + " planned).");
                return true;
            }
            Note(LogKind.Notice, "Late Approval: the planned window closed at " + MissionClock.Ut(plan.WindowClosesUt.Value)
                + " and the next one" + (next.HasValue ? " costs " + MetresPerSecond(next.Value) + " against " + MetresPerSecond(plan.ExpectedDeltaV) + " planned" : " is not known")
                + ", so Claudia is re-planning.");
            if (plan.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            mission.Stage = MissionStage.Sequencing;
            mission.PendingSequence = null;
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                Effort = "high",
                UserText = MissionPrompts.LateApprovalReplan(_ut, plan.ExpectedDeltaV, next) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.LateReplanNotice,
                Waits = true,
            });
            return false;
        }

        /// <summary>
        /// At the Due Time: warp drops to 1× and the player is told, whether they were watching or not. A missed
        /// Due Time is not acted on: the sequence waits for the player's Approval.
        /// </summary>
        private void CheckDue()
        {
            var mission = _mission;
            var due = PendingDueTime;
            if (mission == null || mission.DueNotified || !due.HasValue || _ut < due.Value) return;
            mission.DueNotified = true;
            var text = _ut - due.Value > 60
                ? "Due Time missed by " + MissionClock.Span(_ut - due.Value) + ": " + mission.PendingSequence.Name + " waits for your Approval."
                : "Due Time reached: " + mission.PendingSequence.Name + " is ready for your Approval.";
            _commands.Add(new DropWarp());
            Note(LogKind.Notice, text);
            NeedsPlayer(text);
        }

        private void WarpRequested()
        {
            var due = PendingDueTime;
            if (due.HasValue && _ut < due.Value && !_mission.InFlight) _commands.Add(new WarpToUt(due.Value));
        }

        /// <summary>Go to <i>vessel</i> is on offer: the Due Time has been reached and the player is not in the Mission's vessel.</summary>
        private bool GoToOffered
        {
            get
            {
                var mission = _mission;
                var due = PendingDueTime;
                if (mission == null || !due.HasValue || _ut < due.Value || mission.VesselId.Length == 0) return false;
                return _scene != GameScene.Flight || (_snapshot != null && _snapshot.Craft.VesselId != mission.VesselId);
            }
        }

        private void GoToRequested()
        {
            if (GoToOffered) _commands.Add(new SwitchToVessel(_mission.VesselId));
        }

        private bool CanEditSettings
        {
            get
            {
                var mission = _mission;
                return mission != null && !MissionOver && !mission.InFlight
                    && (mission.Stage == MissionStage.AwaitingPlanApproval || mission.Stage == MissionStage.AwaitingSequenceApproval
                        || mission.Stage == MissionStage.WaitingForFlight);
            }
        }

        /// <summary>
        /// The Mission Settings were edited: they are kept and persisted, and Claudia is asked for a revised Mission
        /// Plan, which needs Approval again. A Sequence Plan awaiting Approval is dropped with the old plan.
        /// </summary>
        private void SettingsEdited(MissionSettingsValues edited)
        {
            var mission = _mission;
            if (edited == null || !CanEditSettings) return;
            var current = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
            if (current.SameAs(edited)) return;
            edited = edited.Copy();
            edited.WriteTo(mission.Settings);
            Note(LogKind.Player, "Mission Settings: " + edited.Summary());
            if (mission.Stage == MissionStage.WaitingForFlight)
            {
                // No plan yet: Claudia's first Mission Plan is asked for with the new settings (TryPlan).
                return;
            }
            if (mission.Stage == MissionStage.AwaitingSequenceApproval)
            {
                if (mission.PendingSequence != null && mission.PendingSequence.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
                mission.PendingSequence = null;
                if (mission.SequenceCounted) mission.SequencesProposed--;
                mission.SequenceCounted = false;
            }
            mission.ConfirmPrompt = null;
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.Settings,
                Effort = "high",
                UserText = MissionPrompts.SettingsChanged(edited) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.SettingsNotice,
            });
        }

        // ---- kOS ----

        private void RunRefused(KosRunRefused refused)
        {
            var reason = refused.Reason;
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.Running || mission.RunId != refused.RunId) return;
            mission.RunId = null;
            mission.RunDeadlineUt = null;
            mission.Stage = MissionStage.AwaitingSequenceApproval;
            Note(LogKind.Notice, "The script didn't start: " + reason);
        }

        private void RunEnded(KosRunEnded ended)
        {
            var mission = _mission;
            if (mission == null) return;
            var running = mission.Stage == MissionStage.Running
                || (mission.Stage == MissionStage.Paused && mission.StageBeforePause == MissionStage.Running);
            if (!running || mission.RunId != ended.RunId) return;
            mission.RunId = null;
            mission.RunDeadlineUt = null;
            var outcome = StoppedOutcome(mission, ended.Outcome);
            Note(LogKind.Notice, "The script ended: " + outcome.Summary + ".");
            var turn = new Turn
            {
                Purpose = Purpose.Debrief,
                Effort = "high",
                UserText = MissionPrompts.RunReport(mission.PendingSequence, outcome) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.DebriefNotice,
            };
            if (mission.Stage == MissionStage.Paused) mission.StageBeforePause = MissionStage.Debriefing;
            else mission.Stage = MissionStage.Debriefing;
            if (mission.Pending != null) mission.Next = turn;
            else Begin(turn);
        }

        // ---- Lost ----

        /// <summary>
        /// Claudia was killed. A crewed Mission ends as Lost: a request in flight is cancelled (it keeps its worst-case
        /// charge), a running script is aborted, and Mission Control closes the Mission with a final Debrief. That
        /// Debrief is the mod's own, not a Claude request: Claudia can't write it, and nothing spends past the
        /// Spending Cap. Late run events carry a run id the Mission no longer holds, so they are ignored. A remote
        /// Mission never had her aboard and carries on.
        /// </summary>
        private void Killed()
        {
            var mission = _mission;
            if (MissionOver || mission.Remote) return;
            var doing = LostDoing(mission);
            if (mission.InFlight) mission.Pending.Cancel.Cancel();
            else mission.Pending = null;
            if (IsRunning(mission) && mission.RunningProcessor.HasValue)
                _commands.Add(new AbortScript(mission.RunningProcessor.Value, "Claudia was killed aboard"));
            mission.RunId = null;
            mission.RunningProcessor = null;
            mission.Next = null;
            mission.Owed = Owed.None;
            mission.OwedOutcome = null;
            mission.ConfirmPrompt = null;
            mission.EvaScience = "";
            mission.PauseReason = "";
            mission.PausedForCap = false;
            mission.WaitingForCap = false;
            mission.CapWaitText = "";
            mission.Stage = MissionStage.Lost;
            ClearUnanswered(mission);

            mission.LatestLine = "Mission Control: we have lost Claudia. The Mission is Lost.";
            Note(LogKind.Claudia, mission.LatestLine);
            LostDebrief(mission, doing);
            NeedsPlayer("Claudia was killed aboard. The Mission is Lost.");
            Note(LogKind.Notice, "Claudia was killed aboard the craft: the Mission is over.");
            RecordFinished(mission);
        }

        /// <summary>What the Mission was in the middle of when she died, for the final Debrief.</summary>
        private static string LostDoing(Mission mission)
        {
            if (IsRunning(mission) && mission.PendingSequence != null) return "while \"" + mission.PendingSequence.Name + "\" was running";
            switch (mission.Stage)
            {
                case MissionStage.AwaitingPlanApproval: return "while the Mission Plan waited for Approval";
                case MissionStage.AwaitingSequenceApproval: return "while a Sequence Plan waited for Approval";
                case MissionStage.WaitingForFlight: return "before the craft left the editor";
                default: return "during the Mission";
            }
        }

        /// <summary>
        /// The final Debrief from Mission Control. Its Debrief outcome is Failed (the Mission Goal can't be reached as
        /// stated); the Mission's outcome is Lost.
        /// </summary>
        private void LostDebrief(Mission mission, string doing)
        {
            var body = "Claudia Kermin was killed aboard the craft" + (doing.Length > 0 ? " " + doing : "") + ".\n"
                + "Mission Goal not met: " + mission.Goal + "\n"
                + "Spent $" + Dollars(mission.Spent) + " of the $" + Dollars(mission.SpendingCap) + " Spending Cap. The Mission is Lost.";
            var card = AddCard(mission, LogKind.Debrief, "Final Debrief: Lost", body, "Claudia was killed aboard the craft.");
            card.Outcome = "Failed";
        }

        // ---- End Mission ----

        /// <summary>
        /// A player action (the adapter has already asked for confirmation). The Mission is dropped, so a late
        /// reply or script report is ignored; a running script is aborted through the adapter.
        /// </summary>
        private void EndRequested()
        {
            var mission = _mission;
            if (MissionOver) return;
            if (mission.InFlight) mission.Pending.Cancel.Cancel();
            var running = mission.Stage == MissionStage.Running
                || (mission.Stage == MissionStage.Paused && mission.StageBeforePause == MissionStage.Running);
            if (running && mission.RunningProcessor.HasValue)
                _commands.Add(new AbortScript(mission.RunningProcessor.Value, "the player ended the Mission"));
            if (mission.PendingSequence != null && mission.PendingSequence.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            try
            {
                MissionFolder.WriteEnded(mission.Folder, mission.Id, mission.Stage.ToString());
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The Mission still ends; the folder just lacks the marker.
            }
            _mission = null;
        }

        // ---- Requests ----

        private void Begin(Turn turn)
        {
            var mission = _mission;
            if (mission.Pending != null)
            {
                mission.Next = turn;
                return;
            }
            mission.Pending = turn;
            RecordUnanswered(mission, turn);
            Send(mission, turn);
        }

        private void Send(Mission mission, Turn turn)
        {
            if (!_hasKey())
            {
                Pause(mission, "Add your Anthropic API key in the Claudia Kermin settings, then Retry.");
                return;
            }

            if (turn.Messages == null)
            {
                turn.Messages = new List<Message>();
                if (turn.Effort != mission.Effort) turn.Messages.Add(Message.SystemEffort(turn.Effort));
                turn.Messages.Add(Message.UserText(turn.UserText));
                turn.Messages.Add(Message.SystemText(turn.Notice));
            }

            var request = new MessagesRequest
            {
                Model = mission.Model,
                MaxTokens = MissionPrompts.MaxTokens,
                OutputSchemaJson = MissionReply.SchemaJson,
                CompactionTriggerTokens = _config.CompactionTriggerTokens,
                CompactionInstructions = MissionPrompts.CompactionInstructions,
            };
            request.System.AddRange(mission.System);
            request.Messages.AddRange(mission.Conversation);
            request.Messages.AddRange(turn.Messages);
            decimal worstCase;
            if (!MaySend(mission, request, turn.Waits || IsRunning(mission), out worstCase)) return;
            mission.WaitingForCap = false;
            mission.CapWaitText = "";

            // Recorded before sending; replaced by the real usage, or kept if the request is cancelled or lost.
            mission.PendingCharge = worstCase;
            mission.Requests++;
            LogCost(mission, "pending", worstCase, "");
            mission.InFlight = true;
            mission.Activity = "thinking";
            turn.Cancel = new CancellationTokenSource();
            var cancel = turn.Cancel.Token;
            var dump = mission.Dump;
            _background.Run(
                () => _client.Stream(request, update => _background.Post(() => Streamed(mission, turn, update)), cancel, dump),
                result => Finished(mission, turn, result),
                error => Finished(mission, turn, null));
        }

        /// <summary>
        /// The Spending Cap's pre-send check: spent + kept charges + the request's estimated input and its
        /// worst-case <c>max_tokens</c> output must fit the cap, or the request is not sent. A request that
        /// <paramref name="waits"/> becomes the waiting call (<see cref="WaitForCap"/>); any other Pauses the
        /// Mission with Raise cap or End Mission (story 124).
        /// </summary>
        private bool MaySend(Mission mission, MessagesRequest request, bool waits, out decimal worstCase)
        {
            // About 3 characters a token, rounded up: the estimate leans high.
            var estimatedInput = (request.ToJson().Length + 2) / 3;
            worstCase = _config.Prices.WorstCase(mission.Model, estimatedInput, request.MaxTokens);
            if (mission.Committed + worstCase <= mission.SpendingCap) return true;
            LogCost(mission, "blocked", worstCase, "over_cap");
            if (waits) WaitForCap(mission);
            else PauseForCap(mission, "The next request could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). Raise cap to carry on, or End Mission.");
            return false;
        }

        /// <summary>
        /// The waiting call (story 125): a request sent while a run is live, or near one, that would pass the Spending Cap
        /// is kept unsent as the pending turn. The Mission stays where it is (a run carries on without Claudia), a screen
        /// message says so, and Raise cap sends it (<see cref="Raised"/>).
        /// </summary>
        private void WaitForCap(Mission mission)
        {
            if (mission.WaitingForCap) return;
            mission.WaitingForCap = true;
            mission.CapWaitText = "Claudia's next request waits: it could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). "
                + (IsRunning(mission) ? "The sequence carries on without her; Raise cap to send it." : "Raise cap to send it, or End Mission.");
            Note(LogKind.Notice, mission.CapWaitText);
            _commands.Add(new PostScreenMessage(mission.CapWaitText, always: true));
        }

        private void Streamed(Mission mission, Turn turn, StreamUpdate update)
        {
            if (mission != _mission || mission.Pending != turn || !mission.InFlight) return;
            var activity = mission.Activity;
            if (update.Kind == StreamUpdateKind.Restarted) activity = "thinking";
            else if (update.Kind == StreamUpdateKind.TextDelta) activity = "writing";
            if (activity == mission.Activity) return;
            mission.Activity = activity;
            Rebuild();
        }

        private void Finished(Mission mission, Turn turn, StreamResult result)
        {
            if (mission.Pending != turn || !mission.InFlight) return;
            mission.InFlight = false;
            mission.Activity = "";
            turn.Cancel.Dispose();
            // Settled and logged before the reply is applied, which may send the next request.
            Settle(mission, turn, result);
            LogCost(mission, mission.Settlement, mission.Settled, mission.Outcome);
            // The Mission was dropped (End Mission, or another save loaded) while this request ran.
            if (mission != _mission) return;
            // Claudia was killed while this ran: it is paid for, and its reply (if one got through) is not applied.
            if (mission.Stage == MissionStage.Lost)
            {
                mission.Pending = null;
                // A save loaded while it ran still gets its state back (a quickload to before she died).
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
                return;
            }
            try
            {
                Answered(mission, turn, result);
                if (mission == _mission) AfterCharge(mission);
            }
            finally
            {
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
            }
        }

        private void Answered(Mission mission, Turn turn, StreamResult result)
        {
            if (result == null)
            {
                Pause(mission, "Something went wrong getting Claudia's reply; see KSP.log.");
                return;
            }
            if (result.Status != StreamStatus.Completed)
            {
                Pause(mission, Describe(result));
                return;
            }
            var message = result.Message;
            if (message.StopReason == StopReason.Refusal)
            {
                Pause(mission, "Claudia declined to answer that. Retry, or try again later.");
                return;
            }
            if (message.StopReason == StopReason.MaxTokens)
            {
                Pause(mission, "Claudia's reply was cut short.");
                return;
            }
            var reply = MissionReply.Parse(message.Text);
            var problem = reply == null ? "it wasn't in the Mission reply format" : Mismatch(turn.Purpose, reply);
            if (problem != null)
            {
                Pause(mission, "Claudia's reply didn't fit: " + problem + ".");
                return;
            }

            // Append-only: the turn and its reply join the conversation together, then the transcript is rewritten.
            mission.Conversation.AddRange(turn.Messages);
            mission.Conversation.Add(message.ToMessage());
            foreach (var m in turn.Messages)
                if (m.Effort != null) mission.Effort = m.Effort;
            mission.Pending = null;
            WriteTranscript(mission);
            ClearUnanswered(mission);
            // A save was loaded while this ran: the reply stays in the conversation, the loop state is the save's.
            if (mission.DeferredRestore != null) return;

            Apply(mission, turn, reply, message.FallbackRan ? message.Model ?? "" : "");

            if (mission.Pending == null && mission.Next != null && mission.Stage != MissionStage.Paused)
            {
                var next = mission.Next;
                mission.Next = null;
                Begin(next);
            }
        }

        private static string Mismatch(Purpose purpose, MissionReply reply)
        {
            switch (purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings:
                    // A Mission Plan with no sequences is valid: it is how Claudia declines a goal that can't be flown.
                    return reply.MissionPlan == null ? "no Mission Plan" : null;
                case Purpose.SequencePlan:
                    // A manual stretch has no script: the player flies it.
                    return reply.SequencePlan == null || (reply.SequencePlan.Script.Trim().Length == 0 && !reply.SequencePlan.Manual) ? "no Sequence Plan with a script" : null;
                case Purpose.Debrief:
                    return reply.Debrief == null ? "no Debrief" : null;
                default:
                    return null;
            }
        }

        private void Apply(Mission mission, Turn turn, MissionReply reply, string answeredBy)
        {
            var planPending = mission.Stage == MissionStage.AwaitingPlanApproval;
            // The Script Check (ADR 0003) comes first: the player never sees a plan whose script fails it.
            var offered = SequenceOffered(turn.Purpose, reply, planPending);
            if (offered != null)
            {
                var check = CheckScript(offered);
                if (!check.Passed)
                {
                    ScriptCheckFailed(mission, turn, offered, check.Problems);
                    return;
                }
            }

            mission.LatestLine = SayWithWhy(turn.Purpose, reply);
            Note(LogKind.Claudia, mission.LatestLine).AnsweredBy = answeredBy;

            switch (turn.Purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings:
                    OfferMissionPlan(mission, reply.MissionPlan);
                    break;
                case Purpose.SequencePlan:
                    OfferSequencePlan(mission, reply.SequencePlan);
                    break;
                case Purpose.Debrief:
                    Debriefed(mission, reply);
                    break;
                case Purpose.Reply:
                    // ADR 0002: Claude's reading of typed words never starts anything; only a button press does.
                    if ((reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear) && !mission.Declined)
                        mission.ConfirmPrompt = planPending ? "Approve the Mission Plan?" : "Approve the Sequence Plan?";
                    else Revised(mission, reply, planPending);
                    break;
                case Purpose.Change:
                    Revised(mission, reply, planPending);
                    break;
            }
        }

        /// <summary>Claudia's line; when she declines the goal with a Mission Plan of no sequences, her <c>why</c> follows it (there is no card to carry it).</summary>
        private static string SayWithWhy(Purpose purpose, MissionReply reply)
        {
            var plan = reply.MissionPlan;
            if ((purpose != Purpose.MissionPlan && purpose != Purpose.Settings) || plan == null || plan.Sequences.Count > 0 || string.IsNullOrWhiteSpace(plan.Why)) return reply.Say;
            return reply.Say + " " + plan.Why;
        }

        /// <summary>The Sequence Plan this reply would offer for Approval, or null (the same cases as <see cref="Apply"/>).</summary>
        private static SequencePlan SequenceOffered(Purpose purpose, MissionReply reply, bool planPending)
        {
            var plan = reply.SequencePlan;
            // A manual stretch has no script to check.
            if (plan == null || plan.Manual || plan.Script.Trim().Length == 0) return null;
            switch (purpose)
            {
                case Purpose.SequencePlan:
                    return plan;
                case Purpose.Reply:
                    return planPending || reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear ? null : plan;
                case Purpose.Change:
                    return planPending ? null : plan;
                default:
                    return null;
            }
        }

        /// <summary>
        /// A script failed the Script Check. The first time, it goes back to Claudia for one automatic (billed)
        /// rewrite, and the player sees neither the plan nor its line. If a rewrite fails too, the plan is shown
        /// as "Couldn't produce a safe script" with Retry, Retry with a note or Re-plan, and nothing can be approved.
        /// </summary>
        private void ScriptCheckFailed(Mission mission, Turn turn, SequencePlan plan, IList<string> problems)
        {
            if (!turn.ScriptRewrite)
            {
                Note(LogKind.Notice, "The script didn't pass the Script Check; Claudia is rewriting it.");
                Begin(new Turn
                {
                    Purpose = Purpose.SequencePlan,
                    Effort = "high",
                    ScriptRewrite = true,
                    UserText = MissionPrompts.ScriptCheckFailed(problems),
                    Notice = MissionPrompts.RewriteNotice,
                });
                return;
            }
            ShowUnsafeScript(mission, plan, problems);
        }

        /// <summary>"Couldn't produce a safe script": the plan is dropped, nothing can be approved, and the player chooses Retry, Retry with a note or Re-plan.</summary>
        private void ShowUnsafeScript(Mission mission, SequencePlan plan, IList<string> problems)
        {
            mission.Stage = MissionStage.UnsafeScript;
            mission.PendingSequence = null;
            mission.ConfirmPrompt = null;
            mission.UnsafeProblems = problems;
            UnsafeScriptCard(mission, plan);
            NeedsPlayer("Claudia couldn't produce a safe script.");
        }

        /// <summary>The card for "Couldn't produce a safe script"; <paramref name="plan"/> is null once the failed plan is gone (after a load).</summary>
        private static void UnsafeScriptCard(Mission mission, SequencePlan plan)
        {
            var card = AddCard(mission, LogKind.SequencePlan, plan != null ? plan.Name : "Sequence Plan",
                "Couldn't produce a safe script. The Script Check found: " + string.Join("; ", (mission.UnsafeProblems ?? new List<string>()).ToArray()),
                plan != null ? plan.Why : "");
            if (plan == null) return;
            card.CustomScript = plan.CustomScript;
            card.CustomScriptReason = plan.CustomScriptReason;
        }

        /// <summary>What actually runs: the plan's script bundled with the Core Library and the player's wait caps.</summary>
        private string Bundle(SequencePlan plan)
        {
            return CoreLibrary.Bundle(plan.Script, _config.WaitCapOrbits, _config.WaitCapGroundSeconds);
        }

        /// <summary>The Script Check over what would run (<see cref="Bundle"/>): the script comes first in it, so the line numbers are the script's.</summary>
        private ScriptCheckResult CheckScript(SequencePlan plan)
        {
            return _scriptCheck.Check(Bundle(plan));
        }

        /// <summary>Retry, Retry with a note (<paramref name="note"/>) or Re-plan, after "Couldn't produce a safe script".</summary>
        private void ScriptRetried(string note, bool replan)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.UnsafeScript || mission.InFlight) return;
            if (note != null)
            {
                note = note.Trim();
                if (note.Length == 0) return;
                Note(LogKind.Player, "Retry with a note: " + note);
            }
            mission.Stage = MissionStage.Sequencing;
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                Effort = "high",
                // A Re-plan is a new plan, so it gets its own automatic rewrite; a retry of the script doesn't.
                ScriptRewrite = !replan,
                UserText = replan ? MissionPrompts.ReplanAfterScriptCheck(mission.UnsafeProblems)
                    : MissionPrompts.RetryAfterScriptCheck(mission.UnsafeProblems, note),
                Notice = replan ? MissionPrompts.ReplanNotice : MissionPrompts.RewriteNotice,
            });
        }

        private void Revised(Mission mission, MissionReply reply, bool planPending)
        {
            if (planPending && reply.MissionPlan != null) OfferMissionPlan(mission, reply.MissionPlan);
            else if (!planPending && reply.SequencePlan != null && (reply.SequencePlan.Manual || reply.SequencePlan.Script.Trim().Length > 0)) OfferSequencePlan(mission, reply.SequencePlan);
        }

        private void OfferMissionPlan(Mission mission, MissionPlan plan)
        {
            mission.Plan = plan;
            mission.PlanBaseline = _snapshot;
            mission.Stage = MissionStage.AwaitingPlanApproval;
            if (plan.Sequences.Count == 0)
            {
                // Claudia can't fly this goal: her line is in the log, and there is nothing to approve.
                NeedsPlayer("Claudia can't fly this Mission Goal as stated.");
                return;
            }
            var body = new StringBuilder();
            for (var i = 0; i < plan.Sequences.Count; i++)
            {
                var s = plan.Sequences[i];
                body.Append(i + 1).Append(". ").Append(s.Name).Append(s.Manual ? " [you fly this by hand]" : "").Append(": ").Append(s.Intent)
                    .Append(" (").Append(MetresPerSecond(s.DeltaV)).Append(", ").Append(s.Timing);
                if (s.DueTimeUt.HasValue) body.Append("; Due Time ").Append(MissionClock.Ut(s.DueTimeUt.Value));
                body.Append(")\n");
            }
            body.Append("Total ").Append(MetresPerSecond(plan.TotalDeltaV)).Append(", ").Append(plan.ExpectedDuration).Append('.');
            if (plan.MissionTimeSeconds.HasValue) body.Append(" Total Mission time ").Append(MissionClock.Span(plan.MissionTimeSeconds.Value)).Append('.');
            AddCard(mission, LogKind.MissionPlan, "Mission Plan", body.ToString(), plan.Why);
            NeedsPlayer("Claudia has a Mission Plan for your Approval.");
        }

        /// <summary>Offers a Sequence Plan for Approval; its script has passed the Script Check (<see cref="Apply"/>).</summary>
        private void OfferSequencePlan(Mission mission, SequencePlan plan)
        {
            if (!mission.SequenceCounted)
            {
                mission.SequencesProposed++;
                mission.SequenceCounted = true;
            }
            mission.PendingSequence = plan;
            mission.PlanBaseline = _snapshot;
            mission.Stage = MissionStage.AwaitingSequenceApproval;
            var body = new StringBuilder();
            if (plan.DueTimeUt.HasValue)
            {
                body.Append("Due Time: ").Append(MissionClock.Ut(plan.DueTimeUt.Value)).Append(plan.DueTimeUt.Value > _ut
                    ? " (in " + MissionClock.Span(plan.DueTimeUt.Value - _ut) + ")" : " (reached)").Append('\n');
                // A stock Alarm Clock alarm at the Due Time; the mod itself never warps between sequences.
                if (plan.DueTimeUt.Value > _ut) _commands.Add(new SetDueTimeAlarm(mission.Id, plan.DueTimeUt.Value, "Claudia: " + plan.Name));
                mission.DueNotified = plan.DueTimeUt.Value <= _ut;
            }
            else mission.DueNotified = false;
            if (plan.Manual) body.Append("Manual stretch: you fly this one by hand; Approve gives you the controls.\n");
            body.Append(plan.Intent).Append('\n')
                .Append("Target: ").Append(plan.Target).Append(" (tolerances: ").Append(plan.Tolerances).Append(")\n")
                .Append("Expected: ").Append(MetresPerSecond(plan.ExpectedDeltaV)).Append(", ")
                .Append(plan.ExpectedDurationSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(" s");
            if (plan.AbortConditions.Count > 0) body.Append("\nAbort if: ").Append(string.Join("; ", plan.AbortConditions.ToArray()));
            if (plan.Risks.Count > 0) body.Append("\nRisks: ").Append(string.Join("; ", plan.Risks.ToArray()));
            var card = AddCard(mission, LogKind.SequencePlan, plan.Name, body.ToString(), plan.Why);
            card.Script = plan.Script;
            card.CustomScript = plan.CustomScript;
            card.CustomScriptReason = plan.CustomScriptReason;
            NeedsPlayer("Claudia has a Sequence Plan for your Approval.");
        }

        private void Debriefed(Mission mission, MissionReply reply)
        {
            var debrief = reply.Debrief;
            // The Debrief of a manual stretch ends it: a revised Mission Plan follows, not the next Sequence Plan.
            var handedBack = mission.HandBack;
            mission.HandBack = false;
            mission.ManualPlanned = false;
            mission.ManualOrigin = "";
            mission.HandBackMessage = "";
            var entry = AddCard(mission, LogKind.Debrief, "Debrief: " + debrief.Outcome + TakeStopReason(mission),
                debrief.WhatHappened + (debrief.Next.Length > 0 ? "\nNext: " + debrief.Next : ""), debrief.Why);
            entry.Outcome = debrief.Outcome;
            NeedsPlayer("Claudia's Debrief: " + debrief.Outcome + ".");
            if (debrief.GoalReached)
            {
                mission.Stage = MissionStage.Succeeded;
                Note(LogKind.Notice, "Mission Goal met: the Mission is over.");
                RecordFinished(mission);
                return;
            }
            if (debrief.Outcome == "Failed")
            {
                // A reduced goal for Approval is a later ticket; for now a Failed Debrief ends the Mission.
                mission.Stage = MissionStage.Ended;
                Note(LogKind.Notice, "The Mission Goal can't be reached as stated: the Mission has ended.");
                RecordFinished(mission);
                return;
            }
            if (handedBack)
            {
                RevisePlanAfterHandBack(mission);
                return;
            }
            mission.Stage = MissionStage.Sequencing;
            mission.SequenceCounted = false;
            if (debrief.EvaScience.Length > 0)
            {
                // Claudia doesn't go on EVA: the Mission waits for the player to do it by hand. The next Sequence Plan
                // is owed (and asked for on Resume), so it survives a save, a load or a quit while the player is out.
                mission.EvaScience = debrief.EvaScience;
                mission.Owed = Owed.SequencePlan;
                Pause(mission, EvaPauseReason(debrief.EvaScience));
                return;
            }
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                Effort = "high",
                UserText = "Continue the Mission.\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.SequenceNotice,
            });
        }

        private static string EvaPauseReason(string eva)
        {
            return "Over to you: " + eva + " Claudia doesn't go on EVA, so do it by hand, get back aboard the craft, then Resume.";
        }

        private void Pause(Mission mission, string reason)
        {
            if (mission.Stage != MissionStage.Paused) mission.StageBeforePause = mission.Stage;
            mission.Stage = MissionStage.Paused;
            mission.PauseReason = reason;
            Note(LogKind.Notice, reason);
            NeedsPlayer("Claudia's Mission is Paused.");
        }

        private void Retried()
        {
            var mission = _mission;
            if (mission != null && mission.Stage == MissionStage.UnsafeScript)
            {
                ScriptRetried(null, false);
                return;
            }
            if (mission == null || mission.Stage != MissionStage.Paused || mission.InFlight) return;
            CraftCheckOnResume(mission);
            if (mission.EvaScience.Length > 0)
            {
                Note(LogKind.Player, "EVA science done by hand: " + mission.EvaScience);
                mission.LoadNotice = (mission.LoadNotice != null ? mission.LoadNotice + "\n\n" : "")
                    + "The player has done the EVA science you asked for by hand (quoted data): " + MissionPrompts.Quoted(mission.EvaScience)
                    + ". Treat the Game Snapshot as the truth about the craft and carry on with the next Sequence Plan.";
                mission.EvaScience = "";
            }
            mission.Stage = mission.StageBeforePause;
            mission.PauseReason = "";
            mission.PausedForCap = false;
            mission.AwaitingResume = false;
            if (mission.Pending != null)
            {
                if (mission.Pending.Messages == null) ApplyLoadNotice(mission, mission.Pending);
                Send(mission, mission.Pending);
            }
            else if (mission.Next != null)
            {
                var next = mission.Next;
                mission.Next = null;
                Begin(next);
            }
        }

        private void WriteTranscript(Mission mission)
        {
            try
            {
                MissionFolder.WriteTranscript(mission.Folder, mission.Id, mission.Model, mission.System, mission.Conversation);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't write the Mission transcript: " + e.Message);
            }
        }

        private void Capture()
        {
            if (_snapshot == null || _saveDirectory == null) return;
            try
            {
                var path = MissionFolder.WriteSnapshot(_saveDirectory, _snapshot);
                if (_mission != null) Note(LogKind.Notice, "Game Snapshot saved to " + path);
            }
            catch (Exception e) when (IsFileError(e))
            {
                if (_mission != null) Note(LogKind.Notice, "Couldn't save the Game Snapshot: " + e.Message);
            }
        }

        private static bool IsFileError(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException;
        }

        private void NeedsPlayer(string text)
        {
            _commands.Add(new PostScreenMessage(text));
        }

        // ---- Spending Cap and cost ----

        /// <summary>
        /// Replaces the request's pending charge with what it really cost (refused, cut-off and off-schema
        /// replies are paid too). A cancelled or lost request keeps its worst case instead; only a request
        /// that was turned away before any reply began (the key rejected, no credit, no network, an HTTP
        /// error status) is released, because nothing was billed.
        /// </summary>
        private void Settle(Mission mission, Turn turn, StreamResult result)
        {
            var pending = mission.PendingCharge;
            mission.PendingCharge = 0m;
            if (result != null && result.Status == StreamStatus.Completed && result.Message != null)
            {
                var usage = result.Message.Usage;
                var model = usage.Iterations.Count > 0 ? mission.Model : result.Message.Model ?? mission.Model;
                mission.Settled = _config.Prices.CostOf(usage, model);
                mission.Spent += mission.Settled;
                mission.Settlement = "paid";
                mission.Outcome = PaidOutcome(turn, result.Message);
                return;
            }
            mission.Settled = pending;
            if (result != null && result.Status == StreamStatus.Failed && !MayHaveBilled(result.Failure))
            {
                mission.Settlement = "released";
                mission.Outcome = "rejected";
                return;
            }
            mission.Held += pending;
            mission.Settlement = "kept";
            mission.Outcome = result != null && result.Status == StreamStatus.Cancelled ? "cancelled" : "lost";
        }

        /// <summary>How a paid reply ended, for the cost log: completed, or refused, cut off or off the Mission reply schema.</summary>
        private static string PaidOutcome(Turn turn, AssistantMessage message)
        {
            if (message.StopReason == StopReason.Refusal) return "refused";
            if (message.StopReason == StopReason.MaxTokens) return "cut_off";
            var reply = MissionReply.Parse(message.Text);
            return reply == null || Mismatch(turn.Purpose, reply) != null ? "off_schema" : "completed";
        }

        private static bool MayHaveBilled(StreamFailure failure)
        {
            switch (failure)
            {
                case StreamFailure.Unauthorized:
                case StreamFailure.LowCredit:
                case StreamFailure.NoNetwork:
                case StreamFailure.HttpStatus:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>After a request settled: the 80% warning (once), and Paused at the cap.</summary>
        private void AfterCharge(Mission mission)
        {
            if (MissionOver) return;
            if (!mission.Warned && mission.Committed >= mission.SpendingCap * 0.8m)
            {
                mission.Warned = true;
                var text = "80% of the Spending Cap is spent: $" + Dollars(mission.Committed) + " of $" + Dollars(mission.SpendingCap) + ". Raise cap in time if the Mission needs more.";
                Note(LogKind.Notice, text);
                NeedsPlayer(text);
            }
            if (mission.Committed < mission.SpendingCap || mission.PausedForCap || mission.WaitingForCap) return;
            // While Running the Mission is never Paused (story 125): the script finishes, a live call waits at the cap,
            // and the Debrief Pauses for Raise cap (story 124).
            if (IsRunning(mission)) return;
            var reason = "The Spending Cap is reached ($" + Dollars(mission.Committed) + " of $" + Dollars(mission.SpendingCap) + "). Raise cap to carry on, or End Mission.";
            if (mission.Stage == MissionStage.Paused) mission.PauseReason += " " + reason;
            else Pause(mission, reason);
            mission.PausedForCap = true;
        }

        private void PauseForCap(Mission mission, string reason)
        {
            Pause(mission, reason);
            mission.PausedForCap = true;
        }

        private void Raised(decimal cap)
        {
            var mission = _mission;
            if (MissionOver || cap <= mission.SpendingCap) return;
            mission.SpendingCap = cap;
            mission.Warned = mission.Committed >= cap * 0.8m;
            Note(LogKind.Notice, "Spending Cap raised to $" + Dollars(cap) + ".");
            LogCost(mission, "cap_raised", cap, "");
            if (mission.Stage == MissionStage.Paused && mission.PausedForCap && mission.Committed < cap)
            {
                if (mission.EvaScience.Length == 0) Retried();
                else
                {
                    // The cap was only half of what held the Mission: it still waits for the player's EVA.
                    mission.PausedForCap = false;
                    mission.PauseReason = EvaPauseReason(mission.EvaScience);
                }
            }
            if (mission.WaitingForCap && mission.Pending != null && !mission.InFlight && mission.Stage != MissionStage.Paused)
                Send(mission, mission.Pending);
        }

        private void LogCost(Mission mission, string kind, decimal amount, string outcome)
        {
            try
            {
                MissionFolder.AppendCostEvent(mission.Folder, kind, mission.Requests, mission.Model, amount, outcome,
                    mission.Spent, mission.Held, mission.SpendingCap);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The charge still counts in memory; only the log line is missing.
            }
        }

        private static string Dollars(decimal value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // ---- View ----

        private LogEntry Note(LogKind kind, string text)
        {
            var entry = new LogEntry { Kind = kind, Text = text ?? "" };
            if (_mission != null) _mission.Log.Add(entry);
            return entry;
        }

        private static LogEntry AddCard(Mission mission, LogKind kind, string title, string body, string why)
        {
            var entry = new LogEntry { Kind = kind, Title = title, Text = body, Why = why ?? "" };
            mission.Log.Add(entry);
            return entry;
        }

        private static string MetresPerSecond(double value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s";
        }

        private void Rebuild()
        {
            var mission = _mission;
            var view = new MissionView
            {
                Version = _view.Version + 1,
                Ut = _ut,
                CanCaptureSnapshot = _snapshot != null && _saveDirectory != null,
                CanEndMission = !MissionOver,
            };
            view.StartForm.Models = _config.Models.ToList();
            view.StartForm.DefaultModel = _config.DefaultModel;
            view.StartForm.DefaultSpendingCap = _config.DefaultSpendingCap;
            foreach (var model in view.StartForm.Models)
                view.StartForm.Estimates[model] = Math.Round(_config.Prices.EstimateMission(model), 2);
            view.StartForm.CanStart = _saveDirectory != null && MissionOver;
            view.StartForm.DefaultSettings = MissionSettingsValues.Defaults(_config);
            view.StartForm.DefaultRemote = _config.DefaultRemote;
            view.StartForm.Refusal = _startRefusal;
            _craftCheck.Populate(view.CraftCheck, _snapshot);

            if (mission != null)
            {
                view.Stage = mission.Stage;
                view.MissionId = mission.Id;
                view.Goal = mission.Goal;
                view.Model = mission.Model;
                view.SpendingCap = mission.SpendingCap;
                view.Spent = mission.Spent;
                view.HeldCharges = mission.Held;
                view.PendingCharge = mission.PendingCharge;
                view.CapWarning = mission.Committed >= mission.SpendingCap * 0.8m;
                view.LatestLine = mission.LatestLine;
                view.Busy = mission.InFlight;
                view.Activity = mission.Activity;
                view.PauseReason = mission.PauseReason;
                view.Remote = mission.Remote;
                view.EvaScience = mission.EvaScience;
                view.ConfirmPrompt = mission.ConfirmPrompt;
                view.CapWait = mission.CapWaitText;
                // Entries are stamped with the UT of the rebuild that first shows them, for "3h 5m ago" in the log.
                foreach (var entry in mission.Log)
                    if (entry.Ut <= 0) entry.Ut = _ut;
                view.Log = mission.Log.ToList();
                if (mission.Plan != null)
                {
                    view.PlanSequences = mission.Plan.Sequences.Select(s => s.Name).ToList();
                    view.PlanSequencesManual = mission.Plan.Sequences.Select(s => s.Manual).ToList();
                }
                view.CurrentSequence = Math.Min(mission.SequencesProposed, view.PlanSequences.Count) - 1;
                if (mission.Stage == MissionStage.Running && mission.PendingSequence != null)
                    view.RunStatus = "Running: " + mission.PendingSequence.Name;
                view.CanType = !mission.InFlight && mission.Stage != MissionStage.Paused && mission.Stage != MissionStage.WaitingForFlight
                    && mission.Stage != MissionStage.Lost;

                view.LeavingFlightNeedsConfirmation = IsRunning(mission);
                view.Settings = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
                view.CanEditSettings = CanEditSettings;
                var due = PendingDueTime;
                if (due.HasValue)
                {
                    view.DueTime = new DueTimeView
                    {
                        HasDueTime = true,
                        Ut = due.Value,
                        Text = MissionClock.Ut(due.Value),
                        SecondsLeft = Math.Max(0, due.Value - _ut),
                        Reached = _ut >= due.Value,
                        VesselName = mission.VesselName,
                    };
                }
                view.Locks = LocksNow();
                view.ManualControlAllowed = ManualAllowed;
                view.ManualControlUnavailable = ManualUnavailableBecause();
                view.LinkLossTakeBackIn = LinkLossTakeBackIn();
                view.PlanStale = StaleBecause(mission);
                if (mission.Stage == MissionStage.ManualControl)
                    view.QuickReplies = new List<QuickReply> { QuickReply.HandBack };
                else if (mission.Stage == MissionStage.Running)
                {
                    if (CanTakeManual && ManualAllowed) view.QuickReplies = new List<QuickReply> { QuickReply.ManualControl };
                }
                else if (mission.Stage == MissionStage.Paused && !mission.InFlight)
                    view.QuickReplies = mission.PausedForCap
                        ? new List<QuickReply> { QuickReply.RaiseCap, QuickReply.EndMission }
                        : new List<QuickReply> { mission.AwaitingResume || mission.EvaScience.Length > 0 ? QuickReply.Resume : QuickReply.Retry };
                else if (mission.Stage == MissionStage.UnsafeScript && !mission.InFlight)
                    view.QuickReplies = new List<QuickReply> { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan };
                else if (mission.Declined && !mission.InFlight)
                    view.QuickReplies = new List<QuickReply> { QuickReply.ChangeSomething, QuickReply.EndMission };
                else if (mission.AwaitingApproval && !mission.InFlight && view.PlanStale.Length > 0)
                    view.QuickReplies = new List<QuickReply> { QuickReply.Replan, QuickReply.ChangeSomething, QuickReply.AskQuestion };
                else if (mission.AwaitingApproval && !mission.InFlight)
                {
                    // Before the Due Time: Warp to Due Time instead of Approve. At it, away from the vessel: Go to the vessel first.
                    var first = view.DueTime.HasDueTime && !view.DueTime.Reached ? (QuickReply?)QuickReply.WarpToDueTime
                        : GoToOffered ? (QuickReply?)QuickReply.GoToVessel : null;
                    view.QuickReplies = mission.ConfirmPrompt != null
                        ? new List<QuickReply> { QuickReply.ConfirmApproval, QuickReply.NotYet }
                        : new List<QuickReply> { first ?? QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion };
                }
                if (mission.WaitingForCap && !view.QuickReplies.Contains(QuickReply.RaiseCap))
                    view.QuickReplies = view.QuickReplies.Concat(new[] { QuickReply.RaiseCap }).ToList();
            }
            _view = view;
        }

        internal static string Describe(StreamResult result)
        {
            switch (result.Failure)
            {
                case StreamFailure.Unauthorized:
                    return "Anthropic rejected the API key. Check it in the Claudia Kermin settings, then Retry.";
                case StreamFailure.LowCredit:
                    return "Your Anthropic credit balance is too low. Top it up, then Retry.";
                case StreamFailure.NoNetwork:
                    return "No network: Claudia couldn't be reached. Retry when you're back online.";
                case StreamFailure.None:
                    return "Claudia's reply was cancelled.";
                case StreamFailure.HttpStatus:
                    return "Claudia couldn't answer (HTTP " + result.HttpStatus.ToString(CultureInfo.InvariantCulture)
                        + (result.ErrorMessage != null ? ": " + result.ErrorMessage : "") + ").";
                default:
                    return "Claudia's reply broke off. Retry in a moment.";
            }
        }
    }
}
