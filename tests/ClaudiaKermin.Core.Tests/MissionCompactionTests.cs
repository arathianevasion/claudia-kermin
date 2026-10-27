// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Compaction on purpose (#268) through <see cref="MissionControl"/> on the streaming fake: no threshold edit on any request,
    /// on-demand compaction at the first long start of Running past the threshold, the hard ceiling at the next turn boundary,
    /// the block kept byte for byte and sent first with the effort resent after it, a load that keeps it, an Alert during
    /// compaction, one retry then skip-and-log, the Spending Cap deferring it, and a tool loop that never compacts mid-way.
    /// </summary>
    public sealed class MissionCompactionTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Summary = "Mission Goal: \\\"Prove the kOS link from the launch pad.\\\" Settings: defaults. Spending Cap: $10.00. "
            + "Plan: Link check (0 m/s). Player: short lines (café — “quoted”).";
        private const string Block = "{\"type\":\"compaction\",\"content\":\"" + Summary + "\",\"signature\":\"c2lnLWNvbXBhY3Q=\"}";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The threshold edit is gone; compaction at the first long start of Running ----

        [Fact]
        public void NoRequestCarriesTheThresholdEditAndBelowTheThresholdNothingCompacts()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600));
            var router = new CraftCheckRouter(transport);
            var mission = Control(router);
            ToRunning(mission);
            _background.Drain();

            Assert.Equal(2, transport.Requests.Count);
            foreach (var request in transport.Requests.Concat(router.CheckRequests))
            {
                Assert.False(Body(request).TryGetProperty("context_management", out _));
                Assert.False(Body(request).TryGetProperty("compaction", out _));
            }
            Assert.Contains(transport.Requests[0].Headers, h => h.Key == "anthropic-beta" && h.Value.Contains("compact-2026-09-04"));
        }

        [Fact]
        public void PastTheThresholdALongSequenceCompactsAsItsOwnRequestAndTheBlockIsSentFirstAfterwardsWithTheEffortResent()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 160000),
                Compaction(),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            DrainUntil(mission, v => Summarized(v));
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            // Its own request: the conversation's model, system and tools, every message so far, the mod's instructions, no reply schema.
            var sequence = Body(transport.Requests[1]);
            var compaction = Body(transport.Requests[2]);
            Assert.Equal("summarize", compaction.GetProperty("compaction").GetProperty("type").GetString());
            Assert.Equal(MissionPrompts.CompactionInstructions, compaction.GetProperty("compaction").GetProperty("instructions").GetString());
            Assert.False(compaction.TryGetProperty("context_management", out _));
            Assert.False(compaction.TryGetProperty("output_config", out _));
            Assert.Equal(MissionPrompts.CompactionMaxTokens, compaction.GetProperty("max_tokens").GetInt32());
            Assert.Equal(sequence.GetProperty("model").GetString(), compaction.GetProperty("model").GetString());
            Assert.Equal(sequence.GetProperty("system").GetRawText(), compaction.GetProperty("system").GetRawText());
            Assert.Equal(sequence.GetProperty("tools").GetRawText(), compaction.GetProperty("tools").GetRawText());
            var before = sequence.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            var summarized = compaction.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            Assert.Equal(before, summarized.Take(before.Count));
            Assert.Equal(before.Count + 1, summarized.Count);
            Assert.Equal("assistant", compaction.GetProperty("messages")[before.Count].GetProperty("role").GetString());

            // The player sees one Mission log line and nothing on the strip.
            Assert.Single(mission.View.Log, e => e.Text.Contains("summarized the conversation"));

            // Afterwards: the block first, byte for byte, then only the new turn, which sends its effort again (the
            // Debrief's high is what the conversation already ran at, but the block dropped per-message effort).
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var debrief = Body(transport.Requests[3]).GetProperty("messages");
            Assert.Equal("{\"role\":\"assistant\",\"content\":[" + Block + "]}", debrief[0].GetRawText());
            Assert.Equal("system", debrief[1].GetProperty("role").GetString());
            Assert.Equal("high", debrief[1].GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal("user", debrief[2].GetProperty("role").GetString());
            Assert.Equal(4, debrief.GetArrayLength());

            // Billed like any request: its own pending charge, then paid at the compaction iteration's cost.
            Assert.Contains("\"outcome\":\"compaction\"", CostLog(mission));
        }

        [Fact]
        public void ACompactionKeepsTheListedFactsWordForWordInPlainText()
        {
            var text = MissionPrompts.CompactionInstructions;
            foreach (var kept in new[]
            {
                "the Mission Goal", "the Mission Settings", "the Spending Cap", "the approved Mission Plan",
                "the flown Maneuver Sequences with their Debrief outcomes", "every player instruction", "the craft's state",
            })
                Assert.Contains(kept, text);
            Assert.Contains("word for word", text);
            Assert.Contains("plain text", text);
            Assert.Contains("Do not call any tool", text);
        }

        [Fact]
        public void PastTheThresholdAShortSequenceWaitsAndTheNextLongOneCompacts()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 60), 160000),
                Sized(ScriptedResponse.Fixture("mission-debrief-next.sse"), 165000),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 120), 170000),
                Compaction());
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Equal(4, transport.Requests.Count);
            Assert.False(Body(transport.Requests[3]).TryGetProperty("compaction", out _));

            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            DrainUntil(mission, v => Summarized(v));
            Assert.True(Body(transport.Requests[4]).TryGetProperty("compaction", out _));
        }

        // ---- The hard ceiling ----

        [Fact]
        public void PastTheCeilingTheNextTurnBoundaryCompactsFirstWhateverTheSequence()
        {
            var transport = new StreamingFakeTransport(
                Sized(ScriptedResponse.Fixture("mission-plan.sse"), 185000),
                Compaction(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(3, transport.Requests.Count);
            Assert.True(Body(transport.Requests[1]).TryGetProperty("compaction", out _));
            var sequence = Body(transport.Requests[2]).GetProperty("messages");
            Assert.Equal("{\"role\":\"assistant\",\"content\":[" + Block + "]}", sequence[0].GetRawText());
            Assert.Equal("high", sequence[1].GetProperty("output_config").GetProperty("effort").GetString());
        }

        // ---- A tool loop never compacts between steps ----

        [Fact]
        public void AToolLoopOverTheThresholdNeverCompactsBetweenStepsAndTheNextTurnBoundaryDoes()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ToolUseStreams.ToolUsePriced("a", 190000, ("stage_performance", "{\"situation\":\"vacuum\"}")),
                ToolUseStreams.ToolUsePriced("b", 195000, ("stage_performance", "{\"situation\":\"vacuum\"}")),
                Sized(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 200000),
                Compaction(),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 25m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);

            // Plan, three loop steps (two tool calls and the reply), and no compaction among them.
            Assert.Equal(4, transport.Requests.Count);
            foreach (var request in transport.Requests)
            {
                Assert.False(Body(request).TryGetProperty("compaction", out _));
                Assert.False(Body(request).TryGetProperty("context_management", out _));
            }

            // The loop's committed reply put the conversation past the ceiling: the next turn waits for a compaction.
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "About 5 seconds, and no engines fire.");
            Assert.True(Body(transport.Requests[4]).TryGetProperty("compaction", out _));
            Assert.Equal("{\"role\":\"assistant\",\"content\":[" + Block + "]}", Body(transport.Requests[5]).GetProperty("messages")[0].GetRawText());
        }

        // ---- An Alert during compaction ----

        [Fact]
        public void AnAlertRaisedDuringCompactionGoesOnTheConversationAsItStoodAndStaysAfterTheBlock()
        {
            var held = Compaction();
            held.PauseAt = 10;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)), 600), 160000),
                held,
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            DrainUntil(mission, v => transport.Requests.Count == 3);

            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "Alert, altitude: carry on. Still fine.");
            Assert.False(Summarized(mission.View));
            var sequence = Body(transport.Requests[1]).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            var alert = Body(transport.Requests[3]).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            // The conversation as it stood: everything the compaction is summarizing, no block, then the Alert.
            Assert.Equal(sequence.Take(sequence.Count), alert.Take(sequence.Count));
            Assert.DoesNotContain(alert, m => m.Contains("\"type\":\"compaction\""));

            held.Gate.Set();
            DrainUntil(mission, v => Summarized(v));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // After the block: the Alert's turn and reply (the block replaces only the messages it covers), then the Debrief.
            var debrief = Body(transport.Requests[4]).GetProperty("messages");
            Assert.Equal("{\"role\":\"assistant\",\"content\":[" + Block + "]}", debrief[0].GetRawText());
            Assert.Equal("low", debrief[1].GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Contains("Alert: a Telemetry Check was crossed", debrief[2].GetRawText());
            Assert.Equal("assistant", debrief[4].GetProperty("role").GetString());
            // The kept reply's thinking would no longer check (its turn opens with a system message), so it goes.
            Assert.DoesNotContain("\"type\":\"thinking\"", debrief[4].GetRawText());
            Assert.Equal("high", debrief[5].GetProperty("output_config").GetProperty("effort").GetString());
        }

        [Fact]
        public void AnAlertWhoseLoopIsMidWayWhenTheBlockLandsKeepsItsPrefixAndJoinsAfterTheBlockWithoutItsThinking()
        {
            var held = Compaction();
            held.PauseAt = 10;
            var step = ToolUseStreams.ToolUse("alert", ("stage_performance", "{\"situation\":\"vacuum\"}"));
            step.PauseAt = 10;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), AltitudeCheck(100)), 600), 160000),
                held,
                step,
                ScriptedResponse.Fixture("mission-alert-carry-on-plain.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            DrainUntil(mission, v => transport.Requests.Count == 3);
            mission.Send(Snapshot(s => s.Altitude = 500));
            DrainUntil(mission, v => transport.Requests.Count == 4);

            // The block lands while the Alert's first step is still out; the step then calls a tool.
            held.Gate.Set();
            DrainUntil(mission, v => Summarized(v));
            step.Gate.Set();
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "Alert, altitude: carry on. Still fine.");

            // Its second step is its first plus two messages, still behind no block: compaction never changes a loop mid-way.
            var first = Body(transport.Requests[3]).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            var second = Body(transport.Requests[4]).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            Assert.Equal(first, second.Take(first.Count));
            Assert.Equal(first.Count + 2, second.Count);
            Assert.DoesNotContain(second, m => m.Contains("\"type\":\"compaction\""));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var debrief = Body(transport.Requests[5]).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
            Assert.Equal("{\"role\":\"assistant\",\"content\":[" + Block + "]}", debrief[0]);
            Assert.Contains("Alert: a Telemetry Check was crossed", debrief[2]);
            Assert.Contains("\"type\":\"tool_use\"", debrief[4]);
            Assert.Contains("\"type\":\"tool_result\"", debrief[5]);
            // Its thinking came from a prefix no later request sends, so it goes; the tool call and the reply stay.
            Assert.DoesNotContain(debrief.Skip(1), m => m.Contains("\"type\":\"thinking\""));
            // Block; the Alert's effort, facts, notice, tool call, result and reply; the Debrief's effort, facts and notice.
            Assert.Equal(10, debrief.Count);
        }

        // ---- Failure and the Spending Cap ----

        [Fact]
        public void ACeilingCompactionTheSpendingCapDefersWaitsForTheNextStartOfRunning()
        {
            var transport = new StreamingFakeTransport(
                Priced(ScriptedResponse.Fixture("mission-plan.sse"), 190000),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 185000),
                Sized(ScriptedResponse.Fixture("mission-answer.sse"), 170000),
                Compaction());
            var mission = Control(new CraftCheckRouter(transport));
            // The plan's worst case (about $0.36) fits; after its $0.38, neither a compaction nor the Sequence Plan does.
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 0.50m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Paused, mission.View.Stage);

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            // Still past the ceiling, but the deferred compaction waits for a start of Running, not this turn boundary.
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "About 5 seconds, and no engines fire.");
            Assert.Equal(3, transport.Requests.Count);
            Assert.Single(Regex.Matches(Events(mission), "compaction_deferred"));

            mission.Send(new Approve());
            DrainUntil(mission, v => Summarized(v));
            Assert.True(Body(transport.Requests[3]).TryGetProperty("compaction", out _));
        }

        [Fact]
        public void AFailedCompactionIsTriedOnceMoreThenSkippedAndLoggedAndTheMissionCarriesOn()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 160000),
                NoSummary("max_tokens"),
                ScriptedResponse.Error(400, "invalid_request_error", "compaction_unavailable"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            var start = ToRunning(mission);
            DrainUntil(mission, v => v.Log.Any(e => e.Text.StartsWith("The conversation couldn't be summarized")));
            Assert.True(Body(transport.Requests[2]).TryGetProperty("compaction", out _));
            Assert.True(Body(transport.Requests[3]).TryGetProperty("compaction", out _));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(5, transport.Requests.Count);
            Assert.DoesNotContain("\"type\":\"compaction\"", transport.Requests[4].Body);
        }

        [Fact]
        public void AFailedCompactionThatSucceedsOnItsRetryIsKept()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 160000),
                NoSummary("end_turn"),
                Compaction());
            var mission = Control(new CraftCheckRouter(transport));
            ToRunning(mission);
            DrainUntil(mission, v => Summarized(v));
            Assert.Equal(4, transport.Requests.Count);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("The conversation couldn't be summarized"));
        }

        [Fact]
        public void WithNoRoomUnderTheSpendingCapCompactionWaitsForTheNextStartOfRunning()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Priced(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 170000),
                ScriptedResponse.Fixture("mission-debrief-next.sse"),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 170000),
                Compaction());
            var mission = Control(new CraftCheckRouter(transport));
            // The Sequence Plan's worst case (about $0.36) fits under the cap before it is paid; after its $0.34, a
            // compaction's worst case (about $0.20) doesn't.
            var start = ToRunning(mission, 0.45m);
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("compaction_deferred", Events(mission));
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("summarized"));

            mission.Send(new RaiseCap(10m));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            mission.Send(new Approve());
            DrainUntil(mission, v => Summarized(v));
            Assert.True(Body(transport.Requests[4]).TryGetProperty("compaction", out _));
        }

        // ---- Persistence ----

        [Fact]
        public void TheBlockIsPersistedByteExactAndALoadSendsItFirstStill()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                Sized(LongRun(ScriptedResponse.Fixture("mission-sequence-plan.sse"), 600), 160000),
                Compaction(),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(new CraftCheckRouter(transport));
            ToRunning(mission);
            DrainUntil(mission, v => Summarized(v));
            var transcript = File.ReadAllText(Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "transcript.json"));
            // Every message stays (the plan's turn opens with its effort: 4; the Sequence Plan's: 3); the block covers all 7.
            Assert.Equal(7, Body(transport.Requests[2]).GetProperty("messages").GetArrayLength());
            Assert.Contains("\"compaction\":{\"covers\":7,\"block\":" + Block + "}", transcript);

            // A quit and a load: the Mission comes back from its folder with the block in force.
            var saved = mission.ExportStates();
            var resumed = Control(new CraftCheckRouter(transport));
            resumed.Send(new MissionsRestored(_save.Path, saved));
            resumed.Send(new AskQuestion("How long will it take?"));
            DrainUntil(resumed, v => transport.Requests.Count == 4 && !v.Busy);
            var question = Body(transport.Requests[3]).GetProperty("messages");
            Assert.Equal("{\"role\":\"assistant\",\"content\":[" + Block + "]}", question[0].GetRawText());
            Assert.Equal("low", question[1].GetProperty("output_config").GetProperty("effort").GetString());
        }

        [Theory]
        [InlineData("compaction_trigger = 49999\n", 150000)]
        [InlineData("compaction_trigger = 50000\n", 50000)]
        public void TheConfiguredTriggerIsKeptOnlyAtOrAboveTheMinimum(string line, int expected)
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), line);
                Assert.Equal(expected, PlayerConfig.LoadOrCreate(dir.Sub).CompactionTriggerTokens);
            }
        }

        // ---- Streams ----

        /// <summary>An on-demand compaction response as the API streams it: the signed block whole in content_block_start, no deltas, stop reason compaction, usage in its iteration.</summary>
        private static ScriptedResponse Compaction()
        {
            var sse = new StringBuilder();
            Event(sse, "message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_compact\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\","
                + "\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}}");
            Event(sse, "content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":" + Block + "}");
            Event(sse, "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}");
            Event(sse, "message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"compaction\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":0,"
                + "\"iterations\":[{\"type\":\"compaction\",\"input_tokens\":2000,\"cache_read_input_tokens\":150000,\"output_tokens\":3000}]}}");
            Event(sse, "message_stop", "{\"type\":\"message_stop\"}");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        /// <summary>A compaction request that wrote no summary: a 200 with empty content and the summarizer's own stop reason.</summary>
        private static ScriptedResponse NoSummary(string stopReason)
        {
            var sse = new StringBuilder();
            Event(sse, "message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_nosummary\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\","
                + "\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}}");
            Event(sse, "message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + stopReason + "\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":0,"
                + "\"iterations\":[{\"type\":\"compaction\",\"input_tokens\":2000,\"output_tokens\":16000}]}}");
            Event(sse, "message_stop", "{\"type\":\"message_stop\"}");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        private static void Event(StringBuilder sse, string name, string data)
        {
            sse.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data).Append("\n\n");
        }

        /// <summary>The reply with its message_start reading <paramref name="cacheRead"/> tokens from the cache: a conversation that size.</summary>
        private static ScriptedResponse Sized(ScriptedResponse response, long cacheRead)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = new Regex("\"cache_read_input_tokens\":\\d+").Replace(text, "\"cache_read_input_tokens\":" + cacheRead, 1);
            if (replaced == text) replaced = new Regex("\"usage\":\\{\"input_tokens\":(\\d+)").Replace(text, "\"usage\":{\"input_tokens\":$1,\"cache_read_input_tokens\":" + cacheRead, 1);
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), PauseAt = response.PauseAt };
        }

        /// <summary>The reply with <paramref name="inputTokens"/> of uncached input (450000 costs $0.90 on Sonnet 5.5).</summary>
        private static ScriptedResponse Priced(ScriptedResponse response, long inputTokens)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = new Regex("\"input_tokens\":\\d+").Replace(text, "\"input_tokens\":" + inputTokens);
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced) };
        }

        /// <summary>A Sequence Plan expecting <paramref name="seconds"/> to run.</summary>
        private static ScriptedResponse LongRun(ScriptedResponse response, int seconds)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace("\\\"expected_duration_s\\\":5,", "\\\"expected_duration_s\\\":" + seconds + ",");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced) };
        }

        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace("\\\"script\\\":", fields.Replace("\"", "\\\"") + "\\\"script\\\":");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced) };
        }

        private static string AltitudeCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"altitude\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Too high for a pad test.\"}],";
        }

        // ---- Setup ----

        private MissionControl Control(IHttpTransport transport)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private StartScript ToRunning(MissionControl mission, decimal cap = 10m)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static bool Summarized(MissionView view)
        {
            return view.Log.Any(e => e.Text.Contains("summarized the conversation"));
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static GameSnapshot Snapshot(Action<GameSnapshot> change)
        {
            var snapshot = OnPad();
            change(snapshot);
            return snapshot;
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

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body)) return doc.RootElement.Clone();
        }

        private string Folder(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId);
        }

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(Folder(mission), "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private string Events(MissionControl mission)
        {
            var path = Path.Combine(Folder(mission), "events.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
    }
}
