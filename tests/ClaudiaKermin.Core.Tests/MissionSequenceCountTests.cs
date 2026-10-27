// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The plan bar's step count (#263): a pending Sequence Plan that a reply turns into a revised Mission Plan is
    /// dropped unflown, so it must not stay counted in <c>SequencesProposed</c>. And the step a Sequence Plan names
    /// (#413), which a retry names again, is the step the bar shows. Driven through the Mission seam.
    /// </summary>
    public sealed class MissionSequenceCountTests : IDisposable
    {
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        /// <summary>A three-sequence Mission Plan: launch, Mun transfer, Mun landing.</summary>
        private static ScriptedResponse ThreeSequenceMissionPlan(string intent = "none", string newGoal = null)
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan.\",\"reply_intent\":\"" + intent + "\",\"mission_plan\":{"
                + (newGoal == null ? "" : "\"new_goal\":\"" + newGoal + "\",") + "\"why\":\"Three legs.\",\"sequences\":["
                + "{\"name\":\"Launch\",\"intent\":\"Reach orbit.\",\"delta_v\":3400,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Mun transfer\",\"intent\":\"Burn for the Mun.\",\"delta_v\":900,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Mun landing\",\"intent\":\"Land.\",\"delta_v\":1500,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":5800,\"expected_duration\":\"short\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null,\"alert\":null}");
        }

        /// <summary>A Sequence Plan; <paramref name="step"/> is the Mission Plan step it names (1-based), or null for a plan that names none (as before #413).</summary>
        private static ScriptedResponse SequencePlan(string name, int? step = null)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: " + name + ".\",\"reply_intent\":\"none\",\"mission_plan\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"name\":\"" + name + "\",\"intent\":\"Fly it.\",\"why\":\"Because.\",\"target\":\"t\",\"tolerances\":\"t\","
                + (step.HasValue ? "\"plan_step\":" + step.Value + "," : "")
                + "\"expected_delta_v\":100,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],\"custom_script\":false,"
                + "\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\",\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse NominalDebrief()
        {
            return Debrief("Nominal", "none");
        }

        private static ScriptedResponse Debrief(string outcome, string followUp)
        {
            return Reply("{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"" + outcome + "\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\","
                + "\"outcome_vs_target\":\"\",\"plan_margin\":\"\",\"science\":\"\",\"goal_checklist\":[],\"follow_up\":\"" + followUp + "\",\"follow_up_text\":\"\"}}");
        }

        private MissionControl Started(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Land on the Mun.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            return mission;
        }

        /// <summary>Approves the pending Sequence Plan, ends its run, and waits for what follows the Debrief to await Approval.</summary>
        private void FlyAndDebrief(MissionControl mission)
        {
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, null, "", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
        }

        private static string StillToFly(HttpRequestSpec debriefRequest)
        {
            var text = LastUserText(debriefRequest);
            var at = text.IndexOf("still to fly in the Mission Plan", StringComparison.Ordinal);
            Assert.True(at >= 0, "the Debrief request doesn't list the rest of the plan");
            return text.Substring(at);
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
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

        [Fact]
        public void ARevisedMissionPlanReplacingAPendingSequencePlanDoesNotLeaveItCounted()
        {
            var transport = new StreamingFakeTransport(
                ThreeSequenceMissionPlan(), SequencePlan("Launch"),
                ThreeSequenceMissionPlan("revise", "Land on the Mun, precisely."), SequencePlan("Launch"),
                NominalDebrief(), SequencePlan("Mun transfer"));
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Land on the Mun.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(0, mission.View.CurrentSequence);

            // The player asks for a change; Claudia answers with a revised Mission Plan, dropping the pending Sequence Plan.
            mission.Send(new Reply("make the landing stricter"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Equal(0, mission.View.CurrentSequence);

            // Fly it, Debrief, and the next offer is step 2.
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, null, "", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Equal(1, mission.View.CurrentSequence);

            // The Debrief asked about the rest of the plan with the transfer and the landing still to fly.
            var debriefRequest = LastUserText(transport.Requests[4]);
            var at = debriefRequest.IndexOf("still to fly in the Mission Plan", StringComparison.Ordinal);
            Assert.True(at >= 0, "the Debrief request doesn't list the rest of the plan");
            var rest = debriefRequest.Substring(at);
            Assert.DoesNotContain("Reach orbit.", rest);
            Assert.Contains("Mun transfer", rest);
            Assert.Contains("Mun landing", rest);
        }

        // ---- Each Sequence Plan names the Mission Plan step it flies (#413) ----

        [Fact]
        public void ARetryOfTheLaunchNamesStepOneAgainSoTheTransferIsStepTwoAndTheLandingIsStillToFly()
        {
            var transport = new StreamingFakeTransport(
                ThreeSequenceMissionPlan(), SequencePlan("Launch", 1),
                Debrief("Off-Nominal", "corrective_sequence"), SequencePlan("Launch (retry)", 1),
                NominalDebrief(), SequencePlan("Mun transfer", 2),
                NominalDebrief(), SequencePlan("Mun landing", 3));
            var mission = Started(transport);
            Assert.Equal(0, mission.View.CurrentSequence);

            // The launch flies, Off-Nominal; the retry names step 1 again, so the bar stays on the launch.
            FlyAndDebrief(mission);
            Assert.Equal(0, mission.View.CurrentSequence);
            Assert.Equal("step 1 of 3", MissionWindowRules.Step(mission.View));

            // The retry flies, Nominal; the transfer is step 2, with step 1 done.
            FlyAndDebrief(mission);
            Assert.Equal(1, mission.View.CurrentSequence);
            Assert.Equal("step 2 of 3", MissionWindowRules.Step(mission.View));
            var segments = MissionWindowRules.Segments(mission.View);
            Assert.Equal(new[] { SegmentState.Done, SegmentState.Current, SegmentState.Upcoming }, segments.Select(s => s.State).ToArray());

            // The transfer flies: its Debrief has the landing still to fly, and its margin counts the landing's delta-v.
            FlyAndDebrief(mission);
            var rest = StillToFly(transport.Requests[6]);
            Assert.DoesNotContain("Mun transfer", rest);
            Assert.Contains("Mun landing", rest);
            var detail = mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief;
            Assert.Equal(1500, detail.Margin.RestOfPlan);
            Assert.Equal(2, mission.View.CurrentSequence);
        }

        [Fact]
        public void OneStepSplitOverTwoSequencesKeepsTheBarOnThatStepForBoth()
        {
            var transport = new StreamingFakeTransport(
                ThreeSequenceMissionPlan(), SequencePlan("Launch", 1),
                NominalDebrief(), SequencePlan("Mun transfer: plane change", 2),
                NominalDebrief(), SequencePlan("Mun transfer: burn", 2),
                NominalDebrief(), SequencePlan("Mun landing", 3));
            var mission = Started(transport);
            FlyAndDebrief(mission);
            Assert.Equal(1, mission.View.CurrentSequence);
            FlyAndDebrief(mission);
            Assert.Equal(1, mission.View.CurrentSequence);
            // Both halves of the transfer had the landing still to fly.
            Assert.Contains("Mun landing", StillToFly(transport.Requests[4]));
            FlyAndDebrief(mission);
            Assert.Contains("Mun landing", StillToFly(transport.Requests[6]));
            Assert.Equal(2, mission.View.CurrentSequence);
        }

        [Fact]
        public void APlanThatNamesNoStepIsCountedAsBefore()
        {
            var transport = new StreamingFakeTransport(
                ThreeSequenceMissionPlan(), SequencePlan("Launch"),
                Debrief("Off-Nominal", "corrective_sequence"), SequencePlan("Launch (retry)"));
            var mission = Started(transport);
            Assert.Equal(0, mission.View.CurrentSequence);
            FlyAndDebrief(mission);
            // The count moves on with every plan offered after a Debrief, retry or not.
            Assert.Equal(1, mission.View.CurrentSequence);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public void AStepOutsideTheMissionPlanIsRefusedAtTheScriptCheckWithTheValidRange(int step)
        {
            var transport = new StreamingFakeTransport(
                ThreeSequenceMissionPlan(), SequencePlan("Launch", step), SequencePlan("Launch", 1));
            var mission = Started(transport);

            // The player never saw the refused plan: the rewrite, naming step 1, is what awaits Approval.
            Assert.Equal(3, transport.Requests.Count);
            var asked = LastUserText(transport.Requests[2]);
            Assert.Contains("plan_step " + step, asked);
            Assert.Contains("1 to 3", asked);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.SequencePlan);
            Assert.Equal(0, mission.View.CurrentSequence);
        }

        [Fact]
        public void TheNamedStepSurvivesASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(
                ThreeSequenceMissionPlan(), SequencePlan("Launch", 1),
                Debrief("Off-Nominal", "corrective_sequence"), SequencePlan("Launch (retry)", 1));
            var mission = Started(transport);
            FlyAndDebrief(mission);
            var state = Assert.Single(mission.ExportStates());
            Assert.Equal(1, state.PlanStep);
            Assert.Equal(1, state.SequencePlan.PlanStep);

            var again = MissionSaveState.FromValues(state.ToValues());
            Assert.Equal(1, again.PlanStep);
            Assert.Equal(1, again.PlanStepBefore);
            Assert.Equal(1, again.SequencePlan.PlanStep);
            // The count ran on to 2; the named step is what the bar shows after the load.
            Assert.Equal(2, again.SequencesProposed);

            var client = new ClaudeClient(new CraftCheckRouter(new StreamingFakeTransport()), () => "sk-ant-test", l => { });
            var loaded = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            loaded.Send(new SaveLoaded(_save.Path));
            loaded.Send(new MissionsRestored(_save.Path, new[] { again }));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, loaded.View.Stage);
            Assert.Equal(0, loaded.View.CurrentSequence);
        }

        [Fact]
        public void ASaveFromBeforeTheNamedStepReadsAsNone()
        {
            var state = new MissionSaveState { Id = "m1", SequencesProposed = 2 };
            var values = state.ToValues();
            Assert.False(values.ContainsKey("planStep"));
            Assert.False(values.ContainsKey("planStepBefore"));
            var again = MissionSaveState.FromValues(values);
            Assert.Null(again.PlanStep);
            Assert.Null(again.PlanStepBefore);
        }
    }
}
