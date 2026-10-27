// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The full Debrief: what the mod gives Claudia besides the script's report (the events it logged during the run, the
    /// rest of the Mission Plan, and the margin recheck, a Craft Check of the craft as it is now), and what the Debrief card
    /// shows. Every Debrief gets the margin recheck, charged to the Mission like any other request and sent only if the
    /// Spending Cap has room for it and still leaves room for the Debrief itself.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>What the mod added to one Debrief's request, kept for its card.</summary>
        private sealed class DebriefGate
        {
            /// <summary>The recheck has run, or was skipped: it is never tried twice for one Debrief.</summary>
            public bool Done;
            public string RecheckSummary = "";
            public readonly List<string> RecheckFindings = new List<string>();
            public string RecheckSkipped = "";
            /// <summary>The recheck's request, which its tool loop appends to, and the Game Snapshot its tools calculate from.</summary>
            public MessagesRequest RecheckRequest;
            public GameSnapshot RecheckSnapshot;
            public int RecheckToolSteps;
            public bool RecheckLimitSent;
            /// <summary>The recheck has had its Second look (a Debrief's own is on its turn).</summary>
            public bool RecheckLooked;
        }

        private sealed partial class Mission
        {
            /// <summary>What the mod noticed during the current run (RunEvents.cs); cleared when the next run starts.</summary>
            public readonly RunEventLog RunEvents = new RunEventLog();
            /// <summary>The kOS terminal text of the last run that did not report, for the Debrief card; "" otherwise.</summary>
            public string ScriptError = "";
            /// <summary>The Debrief in hand: the recheck result and the rest, for its card.</summary>
            public DebriefGate Gate;
            /// <summary>A Failed Debrief's reduced Mission Goal, waiting for the player's Approval; "" otherwise.</summary>
            public string ReducedGoal = "";
        }

        // ---- The run's events ----

        private void RunStarting(Mission mission)
        {
            mission.RunEvents.Clear();
            mission.ScriptError = "";
        }

        private static string RunEventsJson(Mission mission)
        {
            var log = mission.RunEvents;
            if (log.Count == 0 && log.Dropped == 0) return "";
            var json = new JsonWriter().BeginObject().Field("dropped", log.Dropped).Name("events").BeginArray();
            foreach (var e in log.Events)
                json.BeginObject().Field("ut", e.Ut).Field("kind", e.Kind.ToString()).Field("text", e.Text).EndObject();
            return json.EndArray().EndObject().ToString();
        }

        private static void RestoreRunEvents(Mission mission, string text)
        {
            mission.RunEvents.Clear();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var root = JsonReader.Parse(text);
                var events = new List<RunEvent>();
                var items = root["events"];
                if (items != null)
                    foreach (var node in items.Items.Where(n => n.Kind == JsonKind.Object))
                    {
                        RunEventKind kind;
                        var body = JsonNode.Str(node["text"]);
                        if (!Enum.TryParse(JsonNode.Str(node["kind"]), out kind) || string.IsNullOrEmpty(body)) continue;
                        events.Add(new RunEvent { Ut = JsonNode.Double(node["ut"]) ?? 0, Kind = kind, Text = body });
                    }
                mission.RunEvents.Restore(events, (int)(JsonNode.Long(root["dropped"]) ?? 0));
            }
            catch (JsonException)
            {
                mission.RunEvents.Clear();
            }
        }

        private void EventSeen(RunEventSeen seen)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission)) return;
            mission.RunEvents.Add(seen.Ut > 0 ? seen.Ut : _ut, seen.Kind, seen.Text);
        }

        /// <summary>The run ended with a script that did not report: its terminal text goes to the Debrief card.</summary>
        private static void NoteRunOutcome(Mission mission, RunOutcome outcome)
        {
            mission.ScriptError = outcome != null && outcome.Kind != RunOutcomeKind.Finished ? outcome.ErrorText : "";
        }

        // ---- Before the Debrief request goes ----

        /// <summary>
        /// Called as a Debrief turn begins. Adds the mod's facts to it, once, and starts the margin recheck when the
        /// Spending Cap allows; true while the recheck is in flight (the Debrief request follows it).
        /// </summary>
        private bool PrepareDebrief(Mission mission, Turn turn)
        {
            if (turn.Gate == null)
            {
                turn.Gate = new DebriefGate();
                mission.Gate = turn.Gate;
                turn.UserText += RunFacts(mission);
            }
            return !turn.Gate.Done && StartRecheck(mission, turn);
        }

        /// <summary>The events the mod logged and the rest of the Mission Plan, for the Debrief request.</summary>
        private string RunFacts(Mission mission)
        {
            var text = mission.RunEvents.DebriefText(mission.RunStartUt);
            var rest = RestOfPlanText(mission);
            return text + "\nManeuver Sequences still to fly in the Mission Plan (quoted): " + MissionPrompts.Quoted(rest) + ".";
        }

        /// <summary>The Maneuver Sequences after the one just flown, with their delta-v budgets; the plan's own words.</summary>
        private static string RestOfPlanText(Mission mission)
        {
            var left = RestOfPlan(mission);
            if (left.Count == 0) return "none";
            return string.Join("; ", left.Select(s => s.Name + (s.Manual ? " [manual]" : "") + ": " + s.Intent + " (" + MetresPerSecond(s.DeltaV) + ")").ToArray());
        }

        private static IList<PlannedSequence> RestOfPlan(Mission mission)
        {
            if (mission.Plan == null) return new List<PlannedSequence>();
            return mission.Plan.Sequences.Skip(Math.Max(0, mission.SequencesProposed)).ToList();
        }

        private bool StartRecheck(Mission mission, Turn turn)
        {
            var gate = turn.Gate;
            gate.Done = true;
            var snapshot = _snapshot;
            if (snapshot == null) return Skip(gate, "there is no Game Snapshot of the craft");
            if (!snapshot.DeltaVSimulator) return Skip(gate, "the stock delta-v simulator is off");
            if (!_hasKey()) return Skip(gate, "there is no API key");

            var request = _craftCheck.RecheckRequest(mission.Goal, mission.Model, snapshot, mission.Remote, RestOfPlanText(mission));
            var worst = _config.Prices.WorstCase(mission.Model, (request.ToJson().Length + 2) / 3, request.MaxTokens);
            // The recheck must leave room for the Debrief itself, or a Mission at its cap would Pause for a side request.
            if (mission.Committed + worst + DebriefReserve(mission, turn) > mission.SpendingCap)
            {
                LogCost(mission, "blocked", worst, "over_cap");
                var skipped = "it could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + ")";
                Note(LogKind.Notice, "The margin recheck was skipped: " + skipped + ".");
                return Skip(gate, skipped);
            }

            gate.RecheckRequest = request;
            gate.RecheckSnapshot = snapshot;
            mission.Pending = turn;
            SendRecheck(mission, turn, worst);
            return true;
        }

        /// <summary>Sends the recheck's request (its first, or the next step of its tool loop), charged to the Mission at <paramref name="worst"/>.</summary>
        private void SendRecheck(Mission mission, Turn turn, decimal worst)
        {
            mission.PendingCharge = worst;
            mission.Requests++;
            LogCost(mission, "pending", worst, "margin_recheck");
            mission.InFlight = true;
            mission.Activity = turn.Gate.RecheckLooked ? SecondLook.Activity : "rechecking margins";
            turn.Cancel = new CancellationTokenSource();
            var probe = new Turn { Purpose = Purpose.Margin };
            _craftCheck.SendRecheck(turn.Gate.RecheckRequest, turn.Cancel.Token, result => Rechecked(mission, turn, probe, result));
        }

        private static bool Skip(DebriefGate gate, string why)
        {
            gate.RecheckSkipped = why;
            return false;
        }

        /// <summary>What the Debrief request itself could cost at worst, with slack for the recheck's text.</summary>
        private decimal DebriefReserve(Mission mission, Turn turn)
        {
            var probe = new MessagesRequest { Model = mission.Model, MaxTokens = MissionPrompts.MaxTokens, OutputSchemaJson = MissionReply.FlightSchemaJson };
            probe.Tools.AddRange(_tools.Definitions(ToolOffer.Mission));
            probe.System.AddRange(mission.System);
            probe.Messages.AddRange(Sent(mission));
            probe.Messages.Add(Message.UserText(turn.UserText));
            probe.Messages.Add(Message.SystemText(turn.Notice));
            return _config.Prices.WorstCase(mission.Model, (probe.ToJson().Length + ProbeSlack + 2) / 3, probe.MaxTokens);
        }

        /// <summary>Characters of slack a probed request carries for what can't be known yet (the recheck's text, the run's report).</summary>
        private const int ProbeSlack = 4000;

        /// <summary>The recheck came back (or failed): it is paid for, its findings join the Debrief request, and the request goes.</summary>
        private void Rechecked(Mission mission, Turn turn, Turn probe, StreamResult result)
        {
            if (mission.Pending != turn || !mission.InFlight) return;
            mission.InFlight = false;
            mission.Activity = "";
            turn.Cancel.Dispose();
            Settle(mission, probe, result);
            LogCost(mission, mission.Settlement, mission.Settled, mission.Outcome);
            if (mission != _mission) return;
            if (mission.Stage == MissionStage.Lost)
            {
                mission.Pending = null;
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
                return;
            }
            try
            {
                if (result != null && result.Status == StreamStatus.Completed && result.Message.StopReason == StopReason.ToolUse && mission.DeferredRestore == null)
                {
                    // The step is charged like any request: the 80% warning, and a Pause at the cap (which ends the recheck below).
                    AfterCharge(mission);
                    if (mission != _mission) return;
                    if (mission.Stage == MissionStage.Paused) RecheckAnswered(mission, turn, null, "the Mission was Paused", null);
                    else RecheckToolsCalled(mission, turn, result.Message);
                    return;
                }
                string problem;
                var reply = CraftCheckFlow.ParseRecheck(result, out problem);
                if (reply != null && mission.DeferredRestore == null && RecheckSecondLook(mission, turn, reply)) return;
                RecheckAnswered(mission, turn, reply, problem, result);
            }
            finally
            {
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
            }
        }

        /// <summary>
        /// The Second look (#272) for a margin recheck that says it is unsure: its reply is dropped and the recheck goes again at high
        /// effort with the reason, from the Game Snapshot of this moment, if the Spending Cap still leaves room for it and the Debrief;
        /// without room the reply is kept and the skip noted. True when the recheck was sent again. The second reply's doubt is only logged.
        /// </summary>
        private bool RecheckSecondLook(Mission mission, Turn turn, CraftCheckReply reply)
        {
            var gate = turn.Gate;
            if (reply.UnsureBecause.Length == 0) return false;
            if (gate.RecheckLooked)
            {
                Event(mission, "second_look_unsure", "margin_recheck: " + reply.UnsureBecause);
                return false;
            }
            if (!SecondLook.Eligible("margin_recheck", gate.RecheckRequest.Effort)) return false;
            var snapshot = _snapshot ?? gate.RecheckSnapshot;
            var request = _craftCheck.RecheckRequest(mission.Goal, mission.Model, snapshot, mission.Remote, RestOfPlanText(mission), reply.UnsureBecause);
            var worst = RequestCharge.WorstCase(_config.Prices, mission.Model, request);
            if (mission.Committed + worst + DebriefReserve(mission, turn) > mission.SpendingCap)
            {
                LogCost(mission, "blocked", worst, "over_cap");
                Event(mission, "second_look_skipped", "margin_recheck: " + reply.UnsureBecause);
                Note(LogKind.Notice, "A second look at the margin recheck was skipped: it could pass the Spending Cap ($" + Dollars(mission.SpendingCap)
                    + "). Claudia's first reply was unsure because: " + Shorten(reply.UnsureBecause));
                return false;
            }
            // The first reply is charged like any request (the 80% warning, a Pause at the cap), which ends the recheck as a tool step's Pause does.
            AfterCharge(mission);
            if (mission != _mission) return true;
            if (mission.Stage == MissionStage.Paused)
            {
                RecheckAnswered(mission, turn, null, "the Mission was Paused", null);
                return true;
            }
            gate.RecheckLooked = true;
            gate.RecheckRequest = request;
            gate.RecheckSnapshot = snapshot;
            gate.RecheckToolSteps = 0;
            gate.RecheckLimitSent = false;
            Event(mission, "second_look", "margin_recheck: " + reply.UnsureBecause);
            Note(LogKind.Notice, "Claudia was unsure of the margin recheck (" + Shorten(reply.UnsureBecause) + ") and is taking a second look.");
            SendRecheck(mission, turn, worst);
            return true;
        }

        /// <summary>
        /// The recheck's tool loop: Claudia may call the tools offered on the Craft Check (stage_performance) against the Game
        /// Snapshot the recheck was built from, with a Mission turn's limits. Each step is charged to the Mission and gated, like the
        /// recheck itself, on leaving room for the Debrief; with none, the recheck is cut short and the Debrief goes on without it.
        /// </summary>
        private void RecheckToolsCalled(Mission mission, Turn turn, AssistantMessage message)
        {
            var gate = turn.Gate;
            if (gate.RecheckLimitSent)
            {
                RecheckAnswered(mission, turn, null, "Claudia kept calling Planning Tools after the limit of " + ToolStep.MaxSteps + " steps", null);
                return;
            }
            var atLimit = gate.RecheckToolSteps >= ToolStep.MaxSteps;
            mission.InFlight = true;
            mission.Activity = _craftCheck.ActivityOf(message);
            turn.Cancel = new CancellationTokenSource();
            _craftCheck.RunTools(message, gate.RecheckSnapshot, atLimit, step => RecheckToolsAnswered(mission, turn, message, step, atLimit));
        }

        private void RecheckToolsAnswered(Mission mission, Turn turn, AssistantMessage message, ToolStepResult step, bool atLimit)
        {
            if (mission.Pending != turn || !mission.InFlight) return;
            mission.InFlight = false;
            mission.Activity = "";
            turn.Cancel.Dispose();
            if (mission != _mission) return;
            try
            {
                if (mission.Stage == MissionStage.Lost || mission.DeferredRestore != null)
                {
                    mission.Pending = null;
                    return;
                }
                if (step == null)
                {
                    RecheckAnswered(mission, turn, null, "a Planning Tool failed", null);
                    return;
                }
                var gate = turn.Gate;
                gate.RecheckRequest.Messages.Add(message.ToMessage());
                gate.RecheckRequest.Messages.Add(step.Results);
                gate.RecheckToolSteps++;
                if (atLimit) gate.RecheckLimitSent = true;
                foreach (var call in step.Calls)
                {
                    Note(LogKind.Notice, "Planning Tool " + call.Name + " for the margin recheck (" + Shorten(call.InputJson) + "): " + (call.IsError ? "error: " : "") + call.Summary);
                    Event(mission, "tool", "margin_recheck " + call.Name + " " + call.InputJson + " -> " + (call.IsError ? "error: " : "") + call.Summary);
                }
                // Paused while the tools ran: the recheck ends here, and the Debrief waits for Retry with what it has.
                if (mission.Stage == MissionStage.Paused)
                {
                    RecheckAnswered(mission, turn, null, "the Mission was Paused", null);
                    return;
                }
                var worst = RequestCharge.WorstCase(_config.Prices, mission.Model, gate.RecheckRequest);
                if (mission.Committed + worst + DebriefReserve(mission, turn) > mission.SpendingCap)
                {
                    LogCost(mission, "blocked", worst, "over_cap");
                    RecheckAnswered(mission, turn, null, "its next calculation step could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + ")", null);
                    return;
                }
                SendRecheck(mission, turn, worst);
            }
            finally
            {
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
            }
        }

        /// <summary>The recheck is over, with Claudia's findings or with <paramref name="problem"/> saying why there are none: they join the Debrief request, and it goes.</summary>
        private void RecheckAnswered(Mission mission, Turn turn, CraftCheckReply reply, string problem, StreamResult result)
        {
            var gate = turn.Gate;
            gate.RecheckRequest = null;
            gate.RecheckSnapshot = null;
            if (reply == null)
            {
                gate.RecheckSkipped = "the recheck failed: " + problem;
                Note(LogKind.Notice, "The margin recheck failed (" + problem + "); the Debrief goes on without it.");
                turn.UserText += "\nThe mod's margin recheck did not come back (" + problem + "); judge the margin from the Game Snapshot.";
            }
            else
            {
                gate.RecheckSummary = reply.Summary;
                gate.RecheckFindings.AddRange(reply.Findings.Select(f => (f.Severity == CraftFindingSeverity.Blocker ? "Blocker: " : "Warning: ") + f.Title
                    + (f.Explanation.Length > 0 ? ". " + f.Explanation : "")));
                turn.UserText += "\nThe mod's margin recheck (a Craft Check of the craft as it is now, against the Maneuver Sequences still to fly; quoted): "
                    + MissionPrompts.Quoted(reply.Summary + (gate.RecheckFindings.Count > 0 ? " Findings: " + string.Join(" ", gate.RecheckFindings.ToArray()) : " No findings.")) + ".";
            }
            if (mission.DeferredRestore != null)
            {
                mission.Pending = null;
                return;
            }
            // Recorded before AfterCharge, which may Pause for the cap: a quit then still owes the Debrief.
            RecordUnanswered(mission, turn);
            AfterCharge(mission);
            if (mission != _mission || mission.Stage == MissionStage.Paused) return;
            // A refusal or a gone model is the player's call (#45), here as for any request: the Debrief waits, Paused.
            if (result != null && result.Status == StreamStatus.Failed && result.Failure == StreamFailure.ModelNotFound)
            {
                ModelGone(mission, turn);
                return;
            }
            if (result != null && result.Status == StreamStatus.Completed && result.Message.StopReason == StopReason.Refusal)
            {
                Refused(mission, turn);
                return;
            }
            Send(mission, turn);
        }

        // ---- The card ----

        /// <summary>What the Debrief card shows beyond Claudia's account; takes the run's events and error text.</summary>
        private DebriefDetail DetailOf(Mission mission, Debrief debrief)
        {
            var gate = mission.Gate ?? new DebriefGate();
            mission.Gate = null;
            var snapshot = _snapshot;
            var detail = MissionCards.DebriefDetail(debrief);
            detail.RecheckSummary = gate.RecheckSummary;
            detail.RecheckSkipped = gate.RecheckSkipped;
            detail.ScriptError = mission.ScriptError;
            detail.RecheckFindings = gate.RecheckFindings.ToList();
            detail.Events = mission.RunEvents.EventLines(mission.RunStartUt);
            detail.ScienceSeen = mission.RunEvents.ScienceLines(mission.RunStartUt);
            if (snapshot != null)
            {
                double? left = snapshot.DeltaVSimulator && snapshot.Stages.Count > 0 && !snapshot.DeltaVUnreliable ? snapshot.Stages.Sum(s => s.DeltaVVac) : (double?)null;
                detail.Margin = MarginBar.Compute(left, RestOfPlan(mission).Sum(s => s.DeltaV), _config.CraftCheckMarginPercent);
                detail.ResourcesLeft = snapshot.Resources.Where(r => r.Max > 0)
                    .Select(r => r.Name + " " + (100 * r.Amount / r.Max).ToString("0", CultureInfo.InvariantCulture) + "%").ToList();
                if (snapshot.RcsOn.HasValue) detail.RcsState = snapshot.RcsOn.Value ? "RCS on" : "RCS off";
            }
            mission.RunEvents.Clear();
            mission.ScriptError = "";
            return detail;
        }

        // ---- What follows ----

        /// <summary>
        /// A Failed Debrief makes the craft safe (throttle 0, SAS on, steering let go), in a Blackout or not. With a reduced
        /// goal on offer the Mission waits for Approval of it; otherwise it waits on the player with End Mission and Retry with
        /// a note (asking Claudia for a reduced goal), and in a Blackout it waits for the link first (MissionControl.Blackout.cs).
        /// </summary>
        private void FailedDebrief(Mission mission, Debrief debrief)
        {
            _commands.Add(new MakeCraftSafe("a Failed Debrief", mission.VesselId, mission.ProcessorPartId));
            if (debrief.FollowUp == FollowUps.ReducedGoal && debrief.FollowUpText.Length > 0)
            {
                mission.ReducedGoal = debrief.FollowUpText;
                mission.Stage = MissionStage.AwaitingReducedGoal;
                // Under Auto Mode the reduced goal doesn't wait for the link: the player approved it in advance.
                Note(LogKind.Notice, "The Mission Goal can't be reached as stated. Claudia offers a reduced goal for your Approval: " + mission.ReducedGoal
                    + (mission.Blackout && !mission.AutoMode ? " It waits for the link." : ""));
                return;
            }
            mission.ReducedGoal = "";
            mission.Stage = MissionStage.AwaitingReducedGoal;
            Note(LogKind.Notice, "The Mission Goal can't be reached as stated, and Claudia offers no reduced goal. The craft is made safe: End Mission, or Retry with a note asking her for a reduced goal."
                + (mission.Blackout ? " The Mission waits for the link." : ""));
        }

        /// <summary>Retry with a note after a Failed Debrief with no reduced goal: Claudia is asked again, with the player's note, for a reduced goal.</summary>
        private bool ReducedGoalAsked(string note)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.AwaitingReducedGoal || mission.ReducedGoal.Length > 0) return false;
            note = (note ?? "").Trim();
            if (note.Length == 0 || mission.InFlight) return true;
            Note(LogKind.Player, "Retry with a note: " + note);
            mission.Stage = MissionStage.Debriefing;
            // The margin recheck and the run's facts went with the first Debrief: this one is not charged for them again.
            var turn = new Turn
            {
                Purpose = Purpose.Debrief,
                UserText = "The Failed Debrief offered no reduced Mission Goal. The player asks you to offer one, with this note (quoted data): " + MissionPrompts.Quoted(note)
                    + ".\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = DebriefPrompts.ReducedGoalRetryNotice,
                Gate = RetryGate(),
                ReducedGoalRetry = true,
            };
            mission.Gate = turn.Gate;
            Begin(turn);
            return true;
        }

        private static DebriefGate RetryGate()
        {
            return new DebriefGate { Done = true, RecheckSkipped = "not repeated for a retry" };
        }

        /// <summary>Approve reduced goal: the goal changes, and Claudia revises the Mission Plan for it, which needs Approval in turn.</summary>
        /// <param name="inAdvance">Auto Mode approved it (MissionControl.AutoMode.cs), not the player's button.</param>
        private void ReducedGoalAccepted(bool inAdvance = false)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.AwaitingReducedGoal || mission.InFlight || mission.ReducedGoal.Length == 0) return;
            var turn = ReducedGoalTurn(mission);
            mission.Goal = mission.ReducedGoal;
            mission.ReducedGoal = "";
            Note(inAdvance ? LogKind.Notice : LogKind.Player, "Reduced Mission Goal approved: " + mission.Goal);
            if (inAdvance) Note(LogKind.Notice, AutoModeApprovedNote);
            Event(mission, "goal_reduced", mission.Goal);
            mission.Stage = MissionStage.Planning;
            mission.SequenceCounted = false;
            Begin(turn);
        }

        /// <summary>The turn that asks for a Mission Plan for the reduced goal on offer; also the probe the Auto Mode room check prices.</summary>
        private Turn ReducedGoalTurn(Mission mission)
        {
            return new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = "The player approved a reduced Mission Goal (quoted data): " + MissionPrompts.Quoted(mission.ReducedGoal)
                    + " in place of the original (quoted data): " + MissionPrompts.Quoted(mission.Goal) + ".\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = DebriefPrompts.ReducedGoalNotice,
            };
        }

        /// <summary>The turn after a Debrief that did not end the Mission: a revised Mission Plan if Claudia proposed one, else the next Sequence Plan.</summary>
        private void BeginAfterDebrief(Mission mission, Debrief debrief)
        {
            if (debrief.Outcome == "Off-Nominal" && debrief.FollowUp == FollowUps.RevisedPlan)
            {
                mission.Stage = MissionStage.Planning;
                Begin(new Turn
                {
                    Purpose = Purpose.MissionPlan,
                    UserText = "Revise the Mission Plan after the Off-Nominal Debrief.\n" + MissionPrompts.Facts(_ut, _snapshot),
                    Notice = DebriefPrompts.RevisedPlanAfterOffNominal,
                });
                return;
            }
            Begin(NextSequenceTurn(debrief.Outcome == "Off-Nominal"));
        }

        /// <summary>The Sequence Plan request after a Debrief; also the probe the Auto Mode room check prices (MissionControl.AutoMode.cs).</summary>
        private Turn NextSequenceTurn(bool offNominal)
        {
            return new Turn
            {
                Purpose = Purpose.SequencePlan,
                UserText = (offNominal ? "Continue the Mission with a corrective sequence.\n" : "Continue the Mission.\n") + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.SequenceNotice,
            };
        }
    }

    internal static class DebriefPrompts
    {
        public const string ReducedGoalNotice =
            "The player has approved your reduced Mission Goal. Revise the Mission Plan for it now: reply with kind \"mission_plan\" and "
            + "mission_plan filled in, with the sequences already flown first, then those still to fly. The player must approve it before anything runs.";

        public const string ReducedGoalRetryNotice =
            "The Failed Debrief offered no reduced Mission Goal and the player asks for one. Reply with kind \"debrief\" and debrief filled in "
            + "again, outcome Failed: follow_up reduced_goal with follow_up_text the reduced Mission Goal (the player must approve it), or "
            + "end_mission if no reduced goal is within reach.";

        public const string RevisedPlanAfterOffNominal =
            "The Debrief was Off-Nominal and you proposed a revised Mission Plan. Write it now: reply with kind \"mission_plan\" and "
            + "mission_plan filled in, with the sequences already flown first, then those still to fly. The player must approve it before anything runs.";
    }
}
