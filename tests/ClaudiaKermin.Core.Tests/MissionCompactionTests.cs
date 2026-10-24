// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
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
    /// Server-side compaction across a Mission, replayed through the real client from a hand-written stream of a
    /// long transcript (180K tokens in) whose reply opens with a compaction block.
    /// </summary>
    public sealed class MissionCompactionTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Summary = "Mission Goal: \\\"Prove the kOS link from the launch pad.\\\"\\nPlan: 1 sequence (Link check, 0 m/s). "
            + "No Debriefs yet. Delta-v remaining: 3,400 m/s. Failed attempts: none. Player prefers short lines (café — “quoted”).";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json"))));
            return mission;
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

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.Clone();
        }

        [Fact]
        public void TheCompactionBlockIsStoredByteExactAndEchoedOnTheNextRequestAndTheCompactionIsPaid()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-compacted.sse", 64),
                ScriptedResponse.Fixture("mission-sequence-plan.sse", 64));
            var config = new PlayerConfig { CompactionTriggerTokens = 120000 };
            var mission = Control(transport, config);
            CraftCheckRouter.StartChecked(mission, _background, null, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            // The request that compacted: beta header, configured trigger and the Mission's own instructions.
            var first = Body(transport.Requests[0]);
            var edit = first.GetProperty("context_management").GetProperty("edits")[0];
            Assert.Equal("compact_20260112", edit.GetProperty("type").GetString());
            Assert.Equal(120000, edit.GetProperty("trigger").GetProperty("value").GetInt32());
            AssertKeepsWhatTheTicketSays(edit.GetProperty("instructions").GetString());
            Assert.Contains(transport.Requests[0].Headers, h => h.Key == "anthropic-beta" && h.Value.Contains("compact-2026-01-12"));

            // The compaction pass is billed with the message. Sonnet 5.5 is $2 in and $10 out per million tokens:
            // the message $0.0444 plus the pass (180000 in, 3500 out) $0.395.
            Assert.Equal(0.4394m, mission.View.Spent);

            // Stored byte for byte in the Mission folder's transcript.
            var block = "{\"type\":\"compaction\",\"content\":\"" + Summary + "\"}";
            var transcriptPath = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "transcript.json");
            Assert.Contains(block, File.ReadAllText(transcriptPath));

            // Echoed back unchanged, in the assistant turn it arrived in, with nothing trimmed before it.
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var next = transport.Requests[1];
            Assert.Contains(block, next.Body);
            var firstMessages = Body(transport.Requests[0]).GetProperty("messages");
            var nextMessages = Body(next).GetProperty("messages");
            Assert.True(nextMessages.GetArrayLength() > firstMessages.GetArrayLength());
            for (var i = 0; i < firstMessages.GetArrayLength(); i++)
                Assert.Equal(firstMessages[i].GetRawText(), nextMessages[i].GetRawText());
            var assistant = nextMessages.EnumerateArray().First(m => m.GetProperty("role").GetString() == "assistant");
            Assert.Equal(block, assistant.GetProperty("content")[0].GetRawText());

            // The compaction pass is billed: 180000 in and 3500 out on top of the message itself.
        }

        [Theory]
        [InlineData("compaction_trigger = 49999\n", 150000)]
        [InlineData("compaction_trigger = 50000\n", 50000)]
        public void TheConfiguredTriggerIsKeptOnlyAtOrAboveTheApisMinimum(string line, int expected)
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                File.WriteAllText(Path.Combine(dir.Sub,PlayerConfig.FileName), line);
                Assert.Equal(expected, PlayerConfig.LoadOrCreate(dir.Sub).CompactionTriggerTokens);
            }
        }

        private static void AssertKeepsWhatTheTicketSays(string text)
        {
            Assert.NotNull(text);
            foreach (var kept in new[] { "Mission Goal", "Mission Plan", "revision", "Debrief", "outcome", "figures", "delta-v remaining", "failed attempts", "lessons", "preferences" })
                Assert.Contains(kept, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
