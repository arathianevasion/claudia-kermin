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
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The per-plan log (#286) through <see cref="MissionControl"/> with the streaming fake: one line per Sequence Plan in
    /// <c>plans.jsonl</c> with the tools and blocks called, the Custom Script flag, lines and lints, the effort, thinking, time
    /// and cost, any Second look or cut-off retry, and the Debrief's outcome added to the same line when the Debrief commits.
    /// </summary>
    public sealed class MissionPlanLogTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Reason = "the fuel figure may be stale";
        private const string SiteSearch = "circularize().\nlocal best is 0.\nfrom {local i is 0.} until i > 20 step {set i to i + 1.} do {\n"
            + "  local p is latlng(i, 10).\n  if p:terrainheight > best { set best to p:terrainheight. }\n}\nland().\n";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Setup ----

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
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

        private StartScript ToRunning(MissionControl mission)
        {
            mission.Send(new StartMission(Goal, 25m, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static ScriptedResponse Fixture(string name)
        {
            return ScriptedResponse.Fixture(name);
        }

        private static ScriptedResponse Reply(string json, int outputTokens = 90)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = new StringBuilder();
            sse.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3900,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n");
            sse.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            sse.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n");
            sse.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            sse.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":" + outputTokens + ",\"iterations\":[{\"type\":\"message\",\"input_tokens\":3900,\"output_tokens\":" + outputTokens + "}]}}\n\n");
            sse.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        private static string Debrief(string unsure)
        {
            return "{\"kind\":\"debrief\",\"say\":\"Done.\",\"reply_intent\":\"none\",\"unsure_because\":\"" + unsure + "\","
                + "\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Nominal\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\"}}";
        }

        private static string Sequence(string script)
        {
            return "{\"kind\":\"sequence_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\","
                + "\"mission_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"name\":\"Hop\",\"intent\":\"Hop.\",\"why\":\"Because.\",\"target\":\"\",\"tolerances\":\"\",\"expected_delta_v\":0,\"expected_duration_s\":60,"
                + "\"abort_conditions\":[],\"risks\":[],\"telemetry_checks\":[],\"script\":" + JsonSerializer.Serialize(script) + ",\"custom_script_reason\":\"No block searches a site.\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1,\"manual\":false,\"vessel\":\"\"}}";
        }

        private static ScriptedResponse PlanReply()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Second leg\",\"intent\":\"Go on.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1}}");
        }

        /// <summary>The log's lines, parsed.</summary>
        private List<JsonElement> PlanLines(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "plans.jsonl");
            return File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        }

        // ---- The line ----

        [Fact]
        public void APlanWithToolsAndBlocksIsOneLineAndTheDebriefOutcomeIsAddedToItWhenTheDebriefCommits()
        {
            var transport = new StreamingFakeTransport(
                Fixture("mission-plan.sse"),
                ToolUseStreams.ToolUse("plan", ("stage_performance", "{\"situation\":\"vacuum\"}"), ("stage_performance", "{\"situation\":\"atmosphere\"}")),
                Fixture("mission-sequence-plan-blocks.sse"),
                Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);

            var line = Assert.Single(PlanLines(mission));
            Assert.Equal("plan", line.GetProperty("event").GetString());
            Assert.Equal(1, line.GetProperty("plan").GetInt32());
            Assert.Equal("SequencePlan", line.GetProperty("purpose").GetString());
            Assert.Equal(2, line.GetProperty("tools").GetProperty("stage_performance").GetInt32());
            Assert.Equal(2, line.GetProperty("tool_calls").GetInt32());
            Assert.True(line.GetProperty("blocks").GetProperty("circularize").GetInt32() >= 1);
            Assert.False(line.GetProperty("custom_script").GetBoolean());
            Assert.Equal(0, line.GetProperty("custom_lines").GetInt32());
            Assert.Equal(0, line.GetProperty("lints").GetArrayLength());
            Assert.Equal("high", line.GetProperty("effort").GetString());
            // The tool step and the plan's own reply: two requests, both charged to the plan.
            Assert.Equal(2, line.GetProperty("requests").GetInt32());
            Assert.True(line.GetProperty("cost").GetDecimal() > 0m);
            Assert.True(line.GetProperty("output_tokens").GetInt64() > 0);
            Assert.True(line.GetProperty("thinking_tokens").GetInt64() >= 0);
            Assert.True(line.GetProperty("reply_seconds").GetDouble() >= 0);
            Assert.Equal(JsonValueKind.Null, line.GetProperty("second_look").ValueKind);
            Assert.False(line.GetProperty("cut_off_retry").GetBoolean());
            Assert.False(line.TryGetProperty("debrief", out _));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // The same line, rewritten with the Debrief's outcome: still one line for the plan.
            var after = Assert.Single(PlanLines(mission));
            Assert.Equal("Nominal", after.GetProperty("debrief").GetProperty("outcome").GetString());
            Assert.Equal(1, after.GetProperty("plan").GetInt32());
            Assert.Equal(2, after.GetProperty("tools").GetProperty("stage_performance").GetInt32());
            Assert.True(after.GetProperty("debrief").GetProperty("cost").GetDecimal() > 0m);
            Assert.Equal(1, after.GetProperty("debrief").GetProperty("requests").GetInt32());
        }

        [Fact]
        public void ACustomScriptWithLintsRecordsTheFlagTheCodeLinesAndTheLints()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Reply(Sequence(SiteSearch)));
            var mission = Control(transport);
            ToRunning(mission);

            var line = Assert.Single(PlanLines(mission));
            Assert.True(line.GetProperty("custom_script").GetBoolean());
            Assert.Equal(8, line.GetProperty("custom_lines").GetInt32());
            var lints = line.GetProperty("lints").EnumerateArray().Select(l => l.GetString()).ToList();
            var lint = Assert.Single(lints);
            Assert.Contains("landing-site search", lint);
            Assert.Equal(1, line.GetProperty("blocks").GetProperty("circularize").GetInt32());
            Assert.Equal(1, line.GetProperty("blocks").GetProperty("land").GetInt32());
            Assert.Equal(0, line.GetProperty("tool_calls").GetInt32());
        }

        [Fact]
        public void ACutOffReplyThatIsRetriedIsRecordedOnTheLineAndItsRequestsAreCounted()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Fixture("max-tokens-tool.sse"), Fixture("mission-sequence-plan-blocks.sse"));
            var mission = Control(transport);
            ToRunning(mission);

            var line = Assert.Single(PlanLines(mission));
            Assert.True(line.GetProperty("cut_off_retry").GetBoolean());
            Assert.Equal("medium", line.GetProperty("effort").GetString());
            Assert.Equal(2, line.GetProperty("requests").GetInt32());
            Assert.Equal(JsonValueKind.Null, line.GetProperty("second_look").ValueKind);
        }

        [Fact]
        public void ASecondLookOnTheDebriefIsRecordedWithItsReasonAndTheNextPlanGetsALineOfItsOwn()
        {
            var config = new PlayerConfig();
            config.Efforts.Read(EffortTable.Prefix + "debrief", "medium");
            var transport = new StreamingFakeTransport(
                PlanReply(),
                Fixture("mission-sequence-plan.sse"),
                Reply(Debrief(Reason)),
                Reply(Debrief("")),
                Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, config);
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && transport.Requests.Count == 5);

            var lines = PlanLines(mission);
            Assert.Equal(2, lines.Count);
            var debrief = lines[0].GetProperty("debrief");
            Assert.Equal("Nominal", debrief.GetProperty("outcome").GetString());
            Assert.Equal(Reason, debrief.GetProperty("second_look").GetString());
            Assert.Equal("high", debrief.GetProperty("effort").GetString());
            Assert.Equal(2, debrief.GetProperty("requests").GetInt32());
            // The first plan's own figures stand apart from its Debrief's, and the next plan waits for its Debrief.
            Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("second_look").ValueKind);
            Assert.Equal(2, lines[1].GetProperty("plan").GetInt32());
            Assert.False(lines[1].TryGetProperty("debrief", out _));
        }

        [Fact]
        public void ARewriteAfterAFailedScriptCheckIsOneLineCountingBothRequests()
        {
            var transport = new StreamingFakeTransport(
                PlanReply(), Fixture("mission-sequence-plan-unsafe.sse"), Fixture("mission-sequence-plan-rewritten.sse"));
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new QuicksaveCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            ToRunning(mission);

            var line = Assert.Single(PlanLines(mission));
            Assert.True(line.GetProperty("script_rewrite").GetBoolean());
            Assert.Equal(2, line.GetProperty("requests").GetInt32());
        }

        private sealed class QuicksaveCheck : IScriptCheck
        {
            public ScriptCheckResult Check(string script)
            {
                return script.ToUpperInvariant().Contains("QUICKSAVE")
                    ? new ScriptCheckResult(null, new List<string> { "line 1: :QUICKSAVE (quicksave)" })
                    : ScriptCheckResult.Pass();
            }
        }
    }
}
