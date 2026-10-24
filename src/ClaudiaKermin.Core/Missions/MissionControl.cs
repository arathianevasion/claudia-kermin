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
    /// No script reaches Approval unless it passes the Script Check (ADR 0003, <see cref="ScriptCheckFailed"/>).
    ///
    /// Later tickets plug in here: the Spending Cap pre-send check (<see cref="MaySend"/>), the Craft Check
    /// before planning, and persistence of the loop state across saves (the transcript is already written).
    /// </summary>
    public sealed class MissionControl
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
        private MissionView _view = new MissionView();

        public MissionControl(ClaudeClient client, Background background, PlayerConfig config, Func<bool> hasKey, IScriptCheck scriptCheck)
        {
            _client = client;
            _background = background;
            _config = config;
            _hasKey = hasKey;
            _scriptCheck = scriptCheck;
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
            else if (input is SceneChanged) _scene = ((SceneChanged)input).Scene;
            else if (input is UtAdvanced) _ut = ((UtAdvanced)input).Ut;
            else if (input is StartMission) Start((StartMission)input);
            else if (input is EndMission) EndRequested();
            else if (input is Approve) Approved();
            else if (input is ConfirmApproval) Confirmed(((ConfirmApproval)input).Yes);
            else if (input is Reply) Replied(((Reply)input).Text);
            else if (input is ChangeSomething) ChangeRequested(((ChangeSomething)input).Text);
            else if (input is AskQuestion) Asked(((AskQuestion)input).Text);
            else if (input is Retry) Retried();
            else if (input is RetryWithNote) ScriptRetried(((RetryWithNote)input).Note ?? "", false);
            else if (input is Replan) ScriptRetried(null, true);
            else if (input is CaptureSnapshot) Capture();
            else if (input is KosRunRefused) RunRefused(((KosRunRefused)input).Reason);
            else if (input is KosRunEnded) RunEnded((KosRunEnded)input);
            TryPlan();
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
            public List<Message> Messages;
            public CancellationTokenSource Cancel;
        }

        private sealed class Mission
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

            public bool AwaitingApproval
            {
                get { return Stage == MissionStage.AwaitingPlanApproval || Stage == MissionStage.AwaitingSequenceApproval; }
            }
        }

        private bool MissionOver
        {
            get { return _mission == null || _mission.Stage == MissionStage.Succeeded || _mission.Stage == MissionStage.Ended; }
        }

        private void Start(StartMission start)
        {
            if (!MissionOver) return;
            var goal = (start.Goal ?? "").Trim();
            if (goal.Length == 0 || start.SpendingCap <= 0 || !_config.Models.Contains(start.Model) || _saveDirectory == null) return;

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
            };
            // Frozen for the whole Mission, so the cached prefix never changes.
            mission.System.Add(MissionPrompts.BaseLayer);
            mission.System.Add(MissionPrompts.MissionLayer(id));
            // Raw stream dumps are a debug tool: on with debug_tools = true in the player config.
            if (_config.DebugTools) mission.Dump = new FileStreamDump(Path.Combine(mission.Folder, "stream-dumps"));
            _mission = mission;
            Note(LogKind.Player, "Mission Goal: " + goal);
        }

        private void TryPlan()
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.WaitingForFlight) return;
            if (_scene != GameScene.Flight || _snapshot == null) return;
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.MissionPlan,
                Effort = "high",
                UserText = "Mission Goal, in the player's words (quoted data): " + MissionPrompts.Quoted(mission.Goal) + "\n"
                    + "Spending Cap: $" + mission.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture) + ".\n"
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
            if (_mission == null) return;
            if (_mission.InFlight) _mission.Pending.Cancel.Cancel();
            _mission = null;
        }

        private void Snapshot(GameSnapshot snapshot)
        {
            _snapshot = snapshot;
            if (snapshot.Ut > _ut) _ut = snapshot.Ut;
        }

        // ---- Approval ----

        private bool CanAnswerPlan
        {
            get { return _mission != null && _mission.AwaitingApproval && !_mission.InFlight; }
        }

        private void Approved()
        {
            if (!CanAnswerPlan) return;
            _mission.ConfirmPrompt = null;
            Approval();
        }

        private void Confirmed(bool yes)
        {
            if (!CanAnswerPlan || _mission.ConfirmPrompt == null) return;
            _mission.ConfirmPrompt = null;
            if (yes) Approval();
            else Note(LogKind.Notice, "Not approved yet.");
        }

        /// <summary>An Approval: the Approve button, or "Yes, approve" after a typed reply.</summary>
        private void Approval()
        {
            var mission = _mission;
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
            var processor = _snapshot != null ? _snapshot.ProcessorPartId : null;
            if (!processor.HasValue)
            {
                Note(LogKind.Notice, "Can't run the script: the craft has no kOS processor.");
                return;
            }
            Note(LogKind.Notice, "Sequence Plan approved: the mod is starting the script.");
            mission.Stage = MissionStage.Running;
            mission.RunningProcessor = processor.Value;
            _commands.Add(new StartScript(processor.Value, "claudia:" + mission.Id + ":" + plan.Name, plan.Script));
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
                || mission.Stage == MissionStage.WaitingForFlight) return;
            Note(LogKind.Player, text);
            Begin(new Turn
            {
                Purpose = Purpose.Question,
                Effort = "low",
                UserText = "The player asks (quoted data): " + MissionPrompts.Quoted(text),
                Notice = MissionPrompts.QuestionNotice,
            });
        }

        // ---- kOS ----

        private void RunRefused(string reason)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.Running) return;
            mission.Stage = MissionStage.AwaitingSequenceApproval;
            Note(LogKind.Notice, "The script didn't start: " + reason);
        }

        private void RunEnded(KosRunEnded ended)
        {
            var mission = _mission;
            if (mission == null) return;
            var running = mission.Stage == MissionStage.Running
                || (mission.Stage == MissionStage.Paused && mission.StageBeforePause == MissionStage.Running);
            if (!running) return;
            Note(LogKind.Notice, "The script ended: " + ended.Outcome.Summary + ".");
            var turn = new Turn
            {
                Purpose = Purpose.Debrief,
                Effort = "high",
                UserText = MissionPrompts.RunReport(mission.PendingSequence, ended.Outcome) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.DebriefNotice,
            };
            if (mission.Stage == MissionStage.Paused) mission.StageBeforePause = MissionStage.Debriefing;
            else mission.Stage = MissionStage.Debriefing;
            if (mission.Pending != null) mission.Next = turn;
            else Begin(turn);
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
            };
            request.System.AddRange(mission.System);
            request.Messages.AddRange(mission.Conversation);
            request.Messages.AddRange(turn.Messages);
            if (!MaySend(mission, request)) return;

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

        /// <summary>The Spending Cap's pre-send check goes here (a later ticket); for now every request may go.</summary>
        private static bool MaySend(Mission mission, MessagesRequest request)
        {
            return true;
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
            // The Mission was dropped (another save loaded) while this request ran.
            if (mission != _mission) return;
            try
            {
                Answered(mission, turn, result);
            }
            finally
            {
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
                    return reply.MissionPlan == null || reply.MissionPlan.Sequences.Count == 0 ? "no Mission Plan" : null;
                case Purpose.SequencePlan:
                    return reply.SequencePlan == null || reply.SequencePlan.Script.Trim().Length == 0 ? "no Sequence Plan with a script" : null;
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
                var check = _scriptCheck.Check(offered.Script);
                if (!check.Passed)
                {
                    ScriptCheckFailed(mission, turn, offered, check.Problems);
                    return;
                }
            }

            mission.LatestLine = reply.Say;
            Note(LogKind.Claudia, reply.Say).AnsweredBy = answeredBy;

            switch (turn.Purpose)
            {
                case Purpose.MissionPlan:
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
                    if (reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear)
                        mission.ConfirmPrompt = planPending ? "Approve the Mission Plan?" : "Approve the Sequence Plan?";
                    else Revised(mission, reply, planPending);
                    break;
                case Purpose.Change:
                    Revised(mission, reply, planPending);
                    break;
            }
        }

        /// <summary>The Sequence Plan this reply would offer for Approval, or null (the same cases as <see cref="Apply"/>).</summary>
        private static SequencePlan SequenceOffered(Purpose purpose, MissionReply reply, bool planPending)
        {
            var plan = reply.SequencePlan;
            if (plan == null || plan.Script.Trim().Length == 0) return null;
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

            mission.Stage = MissionStage.UnsafeScript;
            mission.PendingSequence = null;
            mission.ConfirmPrompt = null;
            mission.UnsafeProblems = problems;
            var card = AddCard(mission, LogKind.SequencePlan, plan.Name,
                "Couldn't produce a safe script. The Script Check found: " + string.Join("; ", problems.ToArray()), plan.Why);
            card.CustomScript = plan.CustomScript;
            card.CustomScriptReason = plan.CustomScriptReason;
            NeedsPlayer("Claudia couldn't produce a safe script.");
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
            if (planPending && reply.MissionPlan != null && reply.MissionPlan.Sequences.Count > 0) OfferMissionPlan(mission, reply.MissionPlan);
            else if (!planPending && reply.SequencePlan != null && reply.SequencePlan.Script.Trim().Length > 0) OfferSequencePlan(mission, reply.SequencePlan);
        }

        private void OfferMissionPlan(Mission mission, MissionPlan plan)
        {
            mission.Plan = plan;
            mission.Stage = MissionStage.AwaitingPlanApproval;
            var body = new StringBuilder();
            for (var i = 0; i < plan.Sequences.Count; i++)
            {
                var s = plan.Sequences[i];
                body.Append(i + 1).Append(". ").Append(s.Name).Append(": ").Append(s.Intent)
                    .Append(" (").Append(MetresPerSecond(s.DeltaV)).Append(", ").Append(s.Timing).Append(")\n");
            }
            body.Append("Total ").Append(MetresPerSecond(plan.TotalDeltaV)).Append(", ").Append(plan.ExpectedDuration).Append('.');
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
            mission.Stage = MissionStage.AwaitingSequenceApproval;
            var body = new StringBuilder();
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
            var entry = AddCard(mission, LogKind.Debrief, "Debrief: " + debrief.Outcome,
                debrief.WhatHappened + (debrief.Next.Length > 0 ? "\nNext: " + debrief.Next : ""), debrief.Why);
            entry.Outcome = debrief.Outcome;
            NeedsPlayer("Claudia's Debrief: " + debrief.Outcome + ".");
            if (debrief.GoalReached)
            {
                mission.Stage = MissionStage.Succeeded;
                Note(LogKind.Notice, "Mission Goal met: the Mission is over.");
                return;
            }
            if (debrief.Outcome == "Failed")
            {
                // A reduced goal for Approval is a later ticket; for now a Failed Debrief ends the Mission.
                mission.Stage = MissionStage.Ended;
                Note(LogKind.Notice, "The Mission Goal can't be reached as stated: the Mission has ended.");
                return;
            }
            mission.Stage = MissionStage.Sequencing;
            mission.SequenceCounted = false;
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                Effort = "high",
                UserText = "Continue the Mission.\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.SequenceNotice,
            });
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
            mission.Stage = mission.StageBeforePause;
            mission.PauseReason = "";
            if (mission.Pending != null) Send(mission, mission.Pending);
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
                CanCaptureSnapshot = _snapshot != null && _saveDirectory != null,
                CanEndMission = !MissionOver,
            };
            view.StartForm.Models = _config.Models.ToList();
            view.StartForm.DefaultModel = _config.DefaultModel;
            view.StartForm.DefaultSpendingCap = _config.DefaultSpendingCap;
            view.StartForm.CanStart = _saveDirectory != null && MissionOver;

            if (mission != null)
            {
                view.Stage = mission.Stage;
                view.MissionId = mission.Id;
                view.Goal = mission.Goal;
                view.Model = mission.Model;
                view.SpendingCap = mission.SpendingCap;
                view.LatestLine = mission.LatestLine;
                view.Busy = mission.InFlight;
                view.Activity = mission.Activity;
                view.PauseReason = mission.PauseReason;
                view.ConfirmPrompt = mission.ConfirmPrompt;
                view.Log = mission.Log.ToList();
                if (mission.Plan != null) view.PlanSequences = mission.Plan.Sequences.Select(s => s.Name).ToList();
                view.CurrentSequence = Math.Min(mission.SequencesProposed, view.PlanSequences.Count) - 1;
                if (mission.Stage == MissionStage.Running && mission.PendingSequence != null)
                    view.RunStatus = "Running: " + mission.PendingSequence.Name;
                view.CanType = !mission.InFlight && mission.Stage != MissionStage.Paused && mission.Stage != MissionStage.WaitingForFlight;

                if (mission.Stage == MissionStage.Paused && !mission.InFlight)
                    view.QuickReplies = new List<QuickReply> { QuickReply.Retry };
                else if (mission.Stage == MissionStage.UnsafeScript && !mission.InFlight)
                    view.QuickReplies = new List<QuickReply> { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan };
                else if (mission.AwaitingApproval && !mission.InFlight)
                {
                    view.QuickReplies = mission.ConfirmPrompt != null
                        ? new List<QuickReply> { QuickReply.ConfirmApproval, QuickReply.NotYet }
                        : new List<QuickReply> { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion };
                }
            }
            _view = view;
        }

        private static string Describe(StreamResult result)
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
