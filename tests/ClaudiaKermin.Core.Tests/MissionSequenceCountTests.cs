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
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The plan bar's step count (#263): a pending Sequence Plan that a reply turns into a revised Mission Plan is
    /// dropped unflown, so it must not stay counted in <c>SequencesProposed</c>. Driven through the Mission seam.
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

        private static ScriptedResponse SequencePlan(string name)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: " + name + ".\",\"reply_intent\":\"none\",\"mission_plan\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"name\":\"" + name + "\",\"intent\":\"Fly it.\",\"why\":\"Because.\",\"target\":\"t\",\"tolerances\":\"t\","
                + "\"expected_delta_v\":100,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],\"custom_script\":false,"
                + "\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\",\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse NominalDebrief()
        {
            return Reply("{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Nominal\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\","
                + "\"outcome_vs_target\":\"\",\"plan_margin\":\"\",\"science\":\"\",\"goal_checklist\":[],\"follow_up\":\"none\",\"follow_up_text\":\"\"}}");
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
    }
}
