// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Live supervision (spec stories 83 to 89 and 125), driven through the Mission seam: the waiting call at the
    /// Spending Cap, one request per Mission with Alerts ahead of questions, questions while Running, Telemetry
    /// Checks and their Alerts, and the Debrief that lists every crossing.
    /// </summary>
    public sealed class MissionLiveTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The waiting call ----

        [Fact]
        public void AQuestionThatWouldPassTheCapWhileRunningWaitsAndTheMissionStaysRunning()
        {
            // The priced plan and Sequence Plan cost $1.08, so a question's worst case no longer fits a $1.20 cap.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);

            mission.Send(new AskQuestion("How long will it take?"));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Contains(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Text.Contains("Spending Cap") && m.Always);
            // The run is not touched.
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.LatestLine == "About 5 seconds, and no engines fire.");
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.CapWait);
            Assert.DoesNotContain(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains("\"blocked\"", CostLog(mission));

            // The run ends as usual afterwards.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void ALateApprovalReplanThatWouldPassTheCapWaitsWithoutPausing()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), "\"window_closes_ut\":1300,\"late_window_dv\":null,"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 1.20m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            mission.Send(new UtAdvanced(1400));
            mission.Send(new Approve());

            Assert.Equal(MissionStage.Sequencing, mission.View.Stage);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Equal(new[] { QuickReply.RaiseCap }, mission.View.QuickReplies);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void ARunThatEndsWithAQuestionWaitingAtTheCapStillPausesForItsDebrief()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);
            mission.Send(new AskQuestion("How long will it take?"));
            Assert.Contains("Spending Cap", mission.View.CapWait);

            // Story 124: the Debrief goes first, and at the cap it Pauses the Mission with Raise cap or End Mission.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal("", mission.View.CapWait);

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded && !v.Busy && transport.Requests.Count == 4);
            Assert.Contains("kOS run ended: Finished.", LastUserText(transport.Requests[2]));
            Assert.Contains("How long will it take?", LastUserText(transport.Requests[3]));
        }

        [Fact]
        public void AnAlertWaitingAtTheCapWhenTheRunEndsIsDroppedNotSent()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);
            mission.Send(Snapshot(s => s.Altitude = 500));
            Assert.Contains("Spending Cap", mission.View.CapWait);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("altitude 500 m over 100 m: not judged: the run ended first", LastUserText(transport.Requests[2]));
        }

        [Fact]
        public void AnAlertClaudiaCantJudgePausesTheMissionButTheRunCanStillBeAborted()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("refusal.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(QuickReply.Abort, mission.View.QuickReplies.First());
            Assert.Contains(QuickReply.Retry, mission.View.QuickReplies);
            mission.TakeCommands();

            mission.Send(new AbortSequence());
            Assert.Equal(start.RunId, mission.TakeCommands().OfType<AbortScript>().Single().RunId);
        }

        // ---- Questions while Running ----

        [Fact]
        public void QuestionsWhileRunningGoOneAtATimeWithACompactCraftSnapshotAndNeverTouchTheRun()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            Assert.True(mission.View.CanType);
            Assert.Contains(QuickReply.AskQuestion, mission.View.QuickReplies);

            mission.Send(new AskQuestion("How long will it take?"));
            Assert.True(mission.View.Busy);
            // Asked while the first is out: it waits its turn.
            mission.Send(new AskQuestion("And the fuel?"));
            Assert.Equal(2, mission.View.Log.Count(e => e.Kind == LogKind.Player && e.Text.EndsWith("?")));

            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.DoesNotContain(mission.TakeCommands(), c => c is AbortScript || c is StartScript);

            var first = LastUserText(transport.Requests[2]);
            Assert.Contains("How long will it take?", first);
            Assert.Contains("Craft now (compact, JSON data):", first);
            Assert.Contains("\"situation\":\"PRELAUNCH\"", first);
            Assert.Contains("\"run_elapsed_s\"", first);
            // Compact: not the whole Game Snapshot.
            Assert.DoesNotContain("Game Snapshot (JSON data", first);
            Assert.DoesNotContain("\"parts\"", first);
            Assert.Contains("And the fuel?", LastUserText(transport.Requests[3]));
            Assert.Equal("low", Effort(transport.Requests[3]));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void AQuestionClaudiaCantAnswerWhileRunningLeavesTheMissionRunning()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("refusal.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new AskQuestion("Why?"));
            DrainUntil(mission, v => !v.Busy);

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Claudia && e.Text == ClaudiaKermin.Core.Chat.ChatSession.RefusalLine);

            // The run's Debrief is the next request, as usual.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(4, transport.Requests.Count);
        }

        // ---- Telemetry Checks at Approval ----

        [Fact]
        public void ApprovalShowsClaudiasTelemetryChecksWithTheConfigDefaultsFillingTheGaps()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(5000)));
            var mission = Control(transport, new PlayerConfig { CheckElectricChargePercent = 20 });
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            var card = mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).Text;
            // Claudia's own; no delta-v default (the plan expects 0 m/s); 150% of 5 s; the config's electric charge; the fuel margin.
            Assert.Contains("Telemetry Checks: altitude over 5,000 m; time elapsed over 8 s (default); ElectricCharge under 20% (default); "
                + "needed-fuel margin under -10% (default)", card);
        }

        [Fact]
        public void TheDeltaVDefaultIs120PercentOfThePlanAndAQuantityClaudiaSetKeepsHers()
        {
            var plan = new SequencePlan { ExpectedDeltaV = 1000, ExpectedDurationSeconds = 600 };
            var defaults = TelemetryDefaults.Fill(plan, new PlayerConfig());
            Assert.Equal(new[] { "delta-v used over 1,200 m/s", "time elapsed over 900 s", "ElectricCharge under 10%", "needed-fuel margin under -10%" },
                defaults.Select(c => c.Describe()));

            plan.TelemetryChecks.Add(new TelemetryCheck { Quantity = TelemetryQuantity.DeltaVUsed, Threshold = 1100 });
            var filled = TelemetryDefaults.Fill(plan, new PlayerConfig());
            Assert.Equal("delta-v used over 1,100 m/s", filled.Single(c => c.Quantity == TelemetryQuantity.DeltaVUsed).Describe());
            Assert.False(filled.First().Default);
        }

        [Fact]
        public void APlansTelemetryChecksSurviveASave()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(5000)));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            var saved = MissionSaveState.FromValues(mission.ExportStates().Single().ToValues());
            var check = saved.SequencePlan.TelemetryChecks.Single();
            Assert.Equal("altitude over 5,000 m", check.Describe());
            Assert.Equal("Too high for a pad test.", check.Why);
        }

        // ---- A normal run ----

        [Fact]
        public void ANormalRunMakesNoClaudeCallsAndTheStripShowsTheBlockProgressAndKeyNumbers()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new KosBlockStarted("exec_node", start.RunId));
            mission.Send(new UtAdvanced(1203.48 + 2));
            mission.Send(Snapshot(s => { s.Altitude = 80; s.Stages[0].DeltaVVac = 3190; }));

            var progress = mission.View.RunProgress;
            Assert.True(progress.Running);
            Assert.Equal("Link check", progress.Sequence);
            Assert.Equal("exec_node", progress.Block);
            Assert.Equal(2, progress.ElapsedSeconds, 1);
            Assert.Equal(5, progress.PlannedSeconds);
            Assert.Equal(0.4, progress.Fraction, 2);
            Assert.Equal(10, progress.DeltaVUsed);
            Assert.Equal(80, progress.Altitude);
            Assert.Equal(100, progress.ElectricChargePercent);

            // Each block result adds a log row; a block from another run is ignored.
            var result = new Dictionary<string, object> { { "success", true }, { "reason", "" }, { "dv_used", 9.6 }, { "duration", 2.2 } };
            mission.Send(new KosBlockEnded("exec_node", result, start.RunId));
            mission.Send(new KosBlockEnded("exec_node", result, start.RunId + 1));
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == "Building Block exec_node: done, 10 m/s used, 2 s.");
            Assert.Equal(1, mission.View.RunProgress.BlocksDone);
            Assert.Equal("", mission.View.RunProgress.Block);

            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.False(mission.View.Busy);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.False(mission.View.RunProgress.Running);
        }

        // ---- Alerts ----

        [Fact]
        public void ACrossedCheckSendsOneAlertAndAnAbortRecommendationDropsWarpAndThePlayerAbortsToAnOffNominalDebrief()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-alert-abort.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            // 100 m/s used against a 50 m/s check.
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Telemetry Check crossed: delta-v used 100 m/s over 50 m/s"));
            // It fires once: more snapshots past it send nothing more.
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3000));
            DrainUntil(mission, v => v.AbortRecommended.Length > 0);
            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 2900));
            _background.Drain();
            Assert.Equal(3, transport.Requests.Count);

            var alert = LastUserText(transport.Requests[2]);
            Assert.Contains("Alert: a Telemetry Check was crossed", alert);
            Assert.Contains("Craft now (compact, JSON data):", alert);
            Assert.Equal("low", Effort(transport.Requests[2]));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains("The burn is overrunning.", mission.View.AbortRecommended);
            Assert.Equal(QuickReply.Abort, mission.View.QuickReplies.First());
            var commands = mission.TakeCommands();
            Assert.Contains(commands, c => c is DropWarp);
            Assert.Contains(commands.OfType<PostScreenMessage>(), m => m.Always && m.Text.StartsWith("Claudia recommends Abort"));
            // Claudia never aborts herself.
            Assert.DoesNotContain(commands, c => c is AbortScript);

            mission.Send(new AbortSequence());
            var abort = mission.TakeCommands().OfType<AbortScript>().Single();
            Assert.Equal(start.RunId, abort.RunId);
            Assert.Equal("the player pressed Abort", abort.Reason);
            Assert.Equal("", mission.View.AbortRecommended);
            Assert.DoesNotContain(QuickReply.Abort, mission.View.QuickReplies);

            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Aborted, null, "Program aborted.", "the player pressed Abort", true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var report = LastUserText(transport.Requests[3]);
            Assert.Contains("kOS run ended: Aborted (aborted by the player).", report);
            Assert.Contains("Telemetry Checks crossed during the run, with your mid-run verdicts: delta-v used 100 m/s over 50 m/s: Abort recommended (The burn is overrunning.)", report);

            // Claudia rated it Nominal; a crossing rules that out.
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", debrief.Outcome);
            Assert.Equal("Debrief: Off-Nominal (aborted by the player)", debrief.Title);
            Assert.Contains("Telemetry Checks crossed: delta-v used 100 m/s over 50 m/s: Abort recommended", debrief.Text);
        }

        [Fact]
        public void ACarryOnLeavesALogLineAndItsFollowUpThresholdFiresOnceMore()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-alert-carry-on.sse"),
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            mission.TakeCommands();

            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => !v.Busy && v.LatestLine.StartsWith("Alert, altitude: carry on."));
            Assert.Equal("", mission.View.AbortRecommended);
            var commands = mission.TakeCommands();
            Assert.DoesNotContain(commands, c => c is DropWarp);
            Assert.DoesNotContain(commands, c => c is PostScreenMessage);

            // Below the follow-up threshold Claudia gave (1000 m): nothing.
            mission.Send(Snapshot(s => s.Altitude = 800));
            _background.Drain();
            Assert.Equal(3, transport.Requests.Count);

            mission.Send(Snapshot(s => s.Altitude = 1200));
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "Alert, altitude: carry on. Still fine.");
            Assert.Contains("altitude 1,200 m over 1,000 m (follow-up threshold)", LastUserText(transport.Requests[3]));

            // Each check fires at most once per run, and once at its follow-up.
            mission.Send(Snapshot(s => s.Altitude = 5000));
            _background.Drain();
            Assert.Equal(4, transport.Requests.Count);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", debrief.Outcome);
            Assert.Contains("altitude 500 m over 100 m: carry on (Within margins.); altitude 1,200 m over 1,000 m (follow-up threshold): carry on (Still within margins.)", debrief.Text);
        }

        [Fact]
        public void AnAlertGoesAheadOfAQuestionWaitingInTheQueue()
        {
            var held = ScriptedResponse.Fixture("mission-answer.sse");
            held.PauseAt = 10;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                held,
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            ToRunning(mission, 10m);

            mission.Send(new AskQuestion("First question?"));
            mission.Send(new AskQuestion("Second question?"));
            mission.Send(Snapshot(s => s.Altitude = 500));
            held.Gate.Set();

            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 5);
            Assert.Contains("First question?", LastUserText(transport.Requests[2]));
            Assert.Contains("Alert: a Telemetry Check was crossed", LastUserText(transport.Requests[3]));
            Assert.Contains("Second question?", LastUserText(transport.Requests[4]));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void AnAlertThatWouldPassTheCapWaitsAndTheRunCarriesOn()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), AltitudeCheck(100)),
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"));
            var mission = Control(transport);
            ToRunning(mission, 1.20m);
            mission.TakeCommands();

            mission.Send(Snapshot(s => s.Altitude = 500));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains("The sequence carries on without her", mission.View.CapWait);
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Always && m.Text.Contains("Spending Cap"));
            Assert.Equal(new[] { QuickReply.Abort, QuickReply.AskQuestion, QuickReply.RaiseCap }, mission.View.QuickReplies.Where(r => r != QuickReply.ManualControl));

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.LatestLine == "Alert, altitude: carry on. Still fine.");
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void AnAlertStillQueuedWhenTheRunEndsIsDroppedAndTheDebriefSaysItWasNeverJudged()
        {
            var held = ScriptedResponse.Fixture("mission-answer.sse");
            held.PauseAt = 10;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)),
                held,
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new AskQuestion("First question?"));
            mission.Send(Snapshot(s => s.Altitude = 500));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            held.Gate.Set();

            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Contains("altitude 500 m over 100 m: not judged: the run ended first", LastUserText(transport.Requests[3]));
        }

        // ---- Helpers ----

        private static string DeltaVCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"delta_v_used\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Nothing should burn.\"}],";
        }

        private static string AltitudeCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"altitude\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Too high for a pad test.\"}],";
        }

        /// <summary>The on-pad Game Snapshot, changed.</summary>
        private static GameSnapshot Snapshot(Action<GameSnapshot> change)
        {
            var snapshot = OnPad();
            change(snapshot);
            return snapshot;
        }

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var escaped = fields.Replace("\"", "\\\"");
            var replaced = text.Replace("\\\"script\\\":", escaped + "\\\"script\\\":");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>The effort in force for a request: the last effort-only system message in its conversation.</summary>
        private static string Effort(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                    .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).Last();
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private StartScript ToRunning(MissionControl mission, decimal cap)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        /// <summary>The text of the last user message in a request: the facts the turn gave Claudia.</summary>
        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }
    }
}
