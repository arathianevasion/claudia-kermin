// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The streaming client, driven through its public API with recorded byte-exact streams replayed
    /// by a fake transport through the real parser.
    /// </summary>
    public class ClaudeStreamTests
    {
        private const string Key = "sk-ant-api03-STREAMSECRET-wxyz";

        private readonly List<string> _log = new List<string>();
        private readonly List<TimeSpan> _waits = new List<TimeSpan>();

        private ClaudeClient Client(StreamingFakeTransport transport, int stallMs = 2000)
        {
            var client = new ClaudeClient(transport, () => Key, line => { lock (_log) _log.Add(line); });
            client.Timing.StallTimeout = TimeSpan.FromMilliseconds(stallMs);
            client.Timing.Wait = (delay, cancel) => _waits.Add(delay);
            return client;
        }

        private static MessagesRequest Ask(string text = "How far is the Mun?")
        {
            var request = new MessagesRequest { Model = "claude-sonnet-5-5", MaxTokens = 16000, Effort = "low" };
            request.System.Add("You are Claudia.");
            request.Messages.Add(Message.UserText(text));
            return request;
        }

        private static StreamResult Run(ClaudeClient client, MessagesRequest request = null, List<StreamUpdate> updates = null,
            CancellationToken cancel = default(CancellationToken), IRawStreamDump dump = null)
        {
            return client.Stream(request ?? Ask(), u => { if (updates != null) lock (updates) updates.Add(u); }, cancel, dump);
        }

        private string RequestLines()
        {
            return string.Join("\n", _log.Where(l => l.Contains("POST /v1/messages")));
        }

        [Theory]
        [InlineData(4096)]
        [InlineData(7)]
        [InlineData(1)]
        public void TextStreamAssemblesTheReplyWhateverTheChunking(int chunk)
        {
            var updates = new List<StreamUpdate>();
            var result = Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse", chunk))), updates: updates);

            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(1, result.Attempts);
            var message = result.Message;
            const string expected = "Hello from Kerbin — the Mün is 11.4 Mm out, \"apoapsis\" first. \U0001F680";
            Assert.Equal(expected, message.Text);
            Assert.Equal(expected, string.Concat(updates.Where(u => u.Kind == StreamUpdateKind.TextDelta).Select(u => u.Text)));
            Assert.Equal("msg_01TextFixture", message.Id);
            Assert.Equal("claude-sonnet-5-5", message.Model);
            Assert.Equal(StopReason.EndTurn, message.StopReason);
            Assert.Equal(2048, message.Usage.InputTokens);
            Assert.Equal(21, message.Usage.OutputTokens);
            Assert.Equal(1500, message.Usage.CacheCreationInputTokens);
            var iteration = Assert.Single(message.Usage.Iterations);
            Assert.Equal("message", iteration.Type);
            Assert.Equal(21, iteration.OutputTokens);
        }

        [Fact]
        public void PingsAreIgnoredWithoutComment()
        {
            Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"))));
            var line = Assert.Single(_log);
            Assert.Contains("status=completed", line);
        }

        [Fact]
        public void CrLfLineEndsParseTheSame()
        {
            var crlf = Encoding.UTF8.GetBytes(Fixtures.Text("text.sse").Replace("\n", "\r\n"));
            var result = Run(Client(new StreamingFakeTransport(new ScriptedResponse { Body = crlf, ChunkSize = 3 })));
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.StartsWith("Hello from Kerbin", result.Message.Text);
        }

        [Fact]
        public void ThinkingAndToolUseAreTyped()
        {
            var result = Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("thinking-tool-use.sse", 11))));

            var content = result.Message.Content;
            Assert.Equal(3, content.Count);
            var thinking = Assert.IsType<ThinkingBlock>(content[0]);
            Assert.Equal("Checking the apoapsis against the plan.", thinking.Thinking);
            Assert.Equal("EqQBCgIYAhIMsig+part/one==Zm9vYmFy", thinking.Signature);
            Assert.Equal("Let me read the orbit.", Assert.IsType<TextBlock>(content[1]).Text);
            var tool = Assert.IsType<ToolUseBlock>(content[2]);
            Assert.Equal("toolu_01OrbitRead", tool.Id);
            Assert.Equal("read_orbit", tool.Name);
            Assert.Equal("{\"vessel\": \"Mun Lander\", \"detail\": 2}", tool.InputJson);
            Assert.True(tool.InputIsValid);
            Assert.Equal(StopReason.ToolUse, result.Message.StopReason);
        }

        [Fact]
        public void ThinkingAndToolUseGoBackUnchangedWithAToolResult()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("thinking-tool-use.sse"), ScriptedResponse.Fixture("text.sse"));
            var client = Client(transport);
            var request = Ask();
            var first = Run(client, request);

            request.Messages.Add(first.Message.ToMessage());
            var results = new Message { Role = Role.User };
            results.Content.Add(new ToolResultBlock { ToolUseId = "toolu_01OrbitRead", Content = "Ap 81 km" });
            request.Messages.Add(results);
            Run(client, request);

            using (var body = JsonDocument.Parse(transport.Requests[1].Body))
            {
                var messages = body.RootElement.GetProperty("messages");
                var assistant = messages[1].GetProperty("content");
                Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
                Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
                Assert.Equal("EqQBCgIYAhIMsig+part/one==Zm9vYmFy", assistant[0].GetProperty("signature").GetString());
                Assert.Equal("Checking the apoapsis against the plan.", assistant[0].GetProperty("thinking").GetString());
                Assert.Equal("Mun Lander", assistant[2].GetProperty("input").GetProperty("vessel").GetString());
                var result = messages[2].GetProperty("content")[0];
                Assert.Equal("tool_result", result.GetProperty("type").GetString());
                Assert.Equal("toolu_01OrbitRead", result.GetProperty("tool_use_id").GetString());
                Assert.Equal("Ap 81 km", result.GetProperty("content").GetString());
            }
        }

        [Fact]
        public void UnknownBlockIsKeptAndResentByteExact()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("unknown-block.sse", 5), ScriptedResponse.Fixture("text.sse"));
            var client = Client(transport);
            var request = Ask();
            var first = Run(client, request);

            Assert.Equal(StreamStatus.Completed, first.Status);
            var unknown = Assert.IsType<UnknownBlock>(first.Message.Content[0]);
            Assert.Equal("star_chart", unknown.Type);
            Assert.Equal("Charted.", first.Message.Text);

            request.Messages.Add(first.Message.ToMessage());
            request.Messages.Add(Message.UserText("Thanks"));
            Run(client, request);

            // Exactly the bytes between "content_block": and the closing brace of the event, as the API sent them.
            const string raw = "{\"type\":\"star_chart\" , \"payload\": {\"bodies\":[\"Mun\",\"Minmus\"],\"dv\":[860, 1.50e3, -0.0],"
                + "\"note\":\"café \\/ \\\"quoted\\\" ✓\"},\"nothing\":null}";
            Assert.Contains(raw, Fixtures.Text("unknown-block.sse"));
            var sent = transport.Requests[1].Body;
            Assert.Contains("\"content\":[" + raw + ",{\"type\":\"text\",\"text\":\"Charted.\"}]", sent);
            var sentBytes = new UTF8Encoding(false).GetBytes(sent);
            var rawBytes = new UTF8Encoding(false).GetBytes(raw);
            Assert.True(IndexOf(sentBytes, rawBytes) >= 0, "raw block bytes not found in the request body");
            using (JsonDocument.Parse(sent)) { }
        }

        [Fact]
        public void UnknownBlockThatStreamedDeltasIsDroppedFromTheEchoedTurnAndLogged()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("unknown-block-streamed.sse", 5), ScriptedResponse.Fixture("text.sse"));
            var client = Client(transport);
            var request = Ask();
            var first = Run(client, request);

            Assert.Equal(StreamStatus.Completed, first.Status);
            // The streamed block is gone; the delta-less unknown block and the text stay, in order.
            Assert.Equal(2, first.Message.Content.Count);
            Assert.Equal("star_chart", Assert.IsType<UnknownBlock>(first.Message.Content[0]).Type);
            Assert.Equal("Logged.", Assert.IsType<TextBlock>(first.Message.Content[1]).Text);
            Assert.Contains(_log, l => l.Contains("ignored block type star_log dropped from the echoed turn"));
            Assert.DoesNotContain(_log, l => l.Contains("Mun transfer") || l.Contains("circularise"));

            request.Messages.Add(first.Message.ToMessage());
            request.Messages.Add(Message.UserText("Thanks"));
            Run(client, request);

            var sent = transport.Requests[1].Body;
            Assert.DoesNotContain("star_log", sent);
            Assert.Contains("\"content\":[{\"type\":\"star_chart\",\"payload\":{\"bodies\":[\"Mun\"]}},{\"type\":\"text\",\"text\":\"Logged.\"}]", sent);
        }

        [Fact]
        public void UnknownEventsAndDeltasAreLoggedAndIgnored()
        {
            var result = Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("unknown-block.sse"))));

            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Contains(_log, l => l.Contains("ignored event brand_new_event"));
            Assert.Contains(_log, l => l.Contains("ignored delta brand_new_delta"));
            Assert.Contains(_log, l => l.Contains("ignored block type star_chart kept as raw JSON"));
            Assert.DoesNotContain(_log, l => l.Contains("anything") || l.Contains("Minmus") || l.Contains("Charted"));
        }

        [Fact]
        public void FallbackAndCompactionAreTypedWithPerAttemptUsage()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("compaction-fallback.sse"), ScriptedResponse.Fixture("text.sse"));
            var client = Client(transport);
            var request = Ask();
            var result = Run(client, request);

            var message = result.Message;
            var fallback = Assert.IsType<FallbackBlock>(message.Content[0]);
            Assert.Equal("claude-sonnet-5-5", fallback.FromModel);
            Assert.Equal("claude-opus-5-5", fallback.ToModel);
            Assert.Equal("Mission Goal: land on the Mun. Delta-v remaining 2,140 m/s.", Assert.IsType<CompactionBlock>(message.Content[1]).Content);
            Assert.Equal("Still on track.", message.Text);
            Assert.True(message.FallbackRan);
            Assert.Equal("claude-opus-5-5", message.Model);
            Assert.Equal(new[] { "compaction", "message", "fallback_message" }, message.Usage.Iterations.Select(i => i.Type));
            Assert.Equal(180000, message.Usage.Iterations[0].InputTokens);
            Assert.Equal("claude-opus-5-5", message.Usage.Iterations[2].Model);
            Assert.Equal(12000, message.Usage.CacheReadInputTokens);
            Assert.Contains("served_by=claude-opus-5-5", RequestLines());

            request.Messages.Add(message.ToMessage());
            request.Messages.Add(Message.UserText("And now?"));
            Run(client, request);
            var sent = transport.Requests[1].Body;
            Assert.Contains("{\"type\":\"fallback\",\"from\":{\"model\":\"claude-sonnet-5-5\"},\"to\":{\"model\":\"claude-opus-5-5\"}}", sent);
            Assert.Contains("{\"type\":\"compaction\",\"content\":\"Mission Goal: land on the Mun. Delta-v remaining 2,140 m/s.\"}", sent);
        }

        [Fact]
        public void AfterAMidOutputFallbackTheDeclinedModelsThinkingAndToolsAreNotEchoed()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var request = Ask();
            var assistant = new Message { Role = Role.Assistant };
            assistant.Content.Add(new ThinkingBlock { Thinking = "", Signature = "declined-sig" });
            assistant.Content.Add(new TextBlock { Text = "Partial " });
            assistant.Content.Add(new ToolUseBlock { Id = "toolu_declined", Name = "read_orbit" });
            assistant.Content.Add(new FallbackBlockForTest().Block);
            assistant.Content.Add(new ThinkingBlock { Thinking = "", Signature = "kept-sig" });
            assistant.Content.Add(new TextBlock { Text = "rest." });
            request.Messages.Add(assistant);
            request.Messages.Add(Message.UserText("ok"));
            Run(Client(transport), request);

            var sent = transport.Requests[0].Body;
            Assert.DoesNotContain("declined-sig", sent);
            Assert.DoesNotContain("toolu_declined", sent);
            Assert.Contains("Partial ", sent);
            Assert.Contains("kept-sig", sent);
            Assert.Contains("rest.", sent);
        }

        [Theory]
        [InlineData("refusal.sse", StopReason.Refusal)]
        [InlineData("max-tokens-tool.sse", StopReason.MaxTokens)]
        [InlineData("context-window.sse", StopReason.ModelContextWindowExceeded)]
        public void StopReasonsAreTyped(string fixture, StopReason expected)
        {
            var result = Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture(fixture))));
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(expected, result.Message.StopReason);
        }

        [Fact]
        public void RefusalCarriesItsCategory()
        {
            var result = Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse"))));
            Assert.Equal("cyber", result.Message.RefusalCategory);
            Assert.Empty(result.Message.Content);
        }

        [Fact]
        public void UnlistedStopReasonIsKeptRaw()
        {
            var body = Fixtures.Text("context-window.sse").Replace("model_context_window_exceeded", "pause_turn");
            var result = Run(Client(new StreamingFakeTransport(new ScriptedResponse { Body = Encoding.UTF8.GetBytes(body) })));
            Assert.Equal(StopReason.Other, result.Message.StopReason);
            Assert.Equal("pause_turn", result.Message.RawStopReason);
        }

        [Fact]
        public void ToolInputCutOffByMaxTokensIsMarkedInvalidAndSentAsEmpty()
        {
            var result = Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("max-tokens-tool.sse"))));
            var tool = Assert.IsType<ToolUseBlock>(Assert.Single(result.Message.Content));
            Assert.Equal("{\"why\": \"Mun transf", tool.InputJson);
            Assert.False(tool.InputIsValid);
        }

        [Fact]
        public void StallBeforeContentIsRetriedWithBackoff()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("start-only.sse", end: StreamEnd.Stall),
                ScriptedResponse.Fixture("text.sse"));
            var started = DateTime.UtcNow;
            var result = Run(Client(transport, stallMs: 150));

            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(2, result.Attempts);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, _waits);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void StallThatNeverRecoversFailsAsStalledAfterThreeRetries()
        {
            var transport = new StreamingFakeTransport(Enumerable.Range(0, 4)
                .Select(i => ScriptedResponse.Fixture("start-only.sse", end: StreamEnd.Stall)).ToArray());
            var result = Run(Client(transport, stallMs: 80));

            Assert.Equal(StreamStatus.Failed, result.Status);
            Assert.Equal(StreamFailure.Stalled, result.Failure);
            Assert.Equal(4, result.Attempts);
            Assert.Equal(new[] { 1, 2, 4 }.Select(s => TimeSpan.FromSeconds(s)), _waits);
            Assert.Contains("failure=Stalled", RequestLines());
        }

        [Fact]
        public void BytesKeepAStreamAliveLongerThanTheStallTimeout()
        {
            var slow = new SlowTransport(Fixtures.Bytes("text.sse"), chunk: 40, delayMs: 30);
            var client = new ClaudeClient(slow, () => Key, _log.Add);
            client.Timing.StallTimeout = TimeSpan.FromMilliseconds(250);
            var result = client.Stream(Ask(), null, CancellationToken.None);
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(1, result.Attempts);
        }

        [Fact]
        public void RetriesBeforeContentHonourRetryAfterAndBackOffExponentially()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Error(529, "overloaded_error", "Overloaded", retryAfter: "7"),
                ScriptedResponse.Error(500, "api_error", "Internal"),
                ScriptedResponse.Error(429, "rate_limit_error", "Slow down", retryAfter: "3"),
                ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));

            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(4, result.Attempts);
            Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }, _waits);
            Assert.Single(_log);
            Assert.Contains("attempts=4", _log[0]);
        }

        [Fact]
        public void RetriesBeforeContentStopAfterThree()
        {
            var transport = new StreamingFakeTransport(Enumerable.Range(0, 4)
                .Select(i => ScriptedResponse.Error(503, "api_error", "Unavailable")).ToArray());
            var result = Run(Client(transport));

            Assert.Equal(StreamStatus.Failed, result.Status);
            Assert.Equal(StreamFailure.HttpStatus, result.Failure);
            Assert.Equal(503, result.HttpStatus);
            Assert.Equal(4, result.Attempts);
            Assert.Equal(4, transport.Requests.Count);
        }

        [Fact]
        public void NoNetworkIsRetriedBeforeContent()
        {
            var transport = new StreamingFakeTransport(new ScriptedResponse { NoNetwork = true }, ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(2, result.Attempts);
        }

        [Fact]
        public void ErrorEventBeforeContentIsRetried()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("overloaded-error.sse"), ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(2, result.Attempts);
        }

        [Fact]
        public void RetryAfterContentStartsAgainFromScratchOnce()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Break),
                ScriptedResponse.Fixture("text.sse"));
            var updates = new List<StreamUpdate>();
            var result = Run(Client(transport), updates: updates);

            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(2, result.Attempts);
            Assert.Equal(StreamUpdateKind.Restarted, Assert.Single(updates, u => u.Kind == StreamUpdateKind.Restarted).Kind);
            var afterRestart = updates.SkipWhile(u => u.Kind != StreamUpdateKind.Restarted).Where(u => u.Kind == StreamUpdateKind.TextDelta);
            Assert.Equal(result.Message.Text, string.Concat(afterRestart.Select(u => u.Text)));
            Assert.DoesNotContain("Half a sent", result.Message.Text);
            Assert.Equal(transport.Requests[0].Body, transport.Requests[1].Body);
        }

        [Fact]
        public void FailureAfterContentTwiceGivesUp()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Break),
                ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Close),
                ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));

            Assert.Equal(StreamStatus.Failed, result.Status);
            Assert.Equal(StreamFailure.EndedEarly, result.Failure);
            Assert.Equal(2, result.Attempts);
            Assert.Null(result.Message);
        }

        [Fact]
        public void StallAfterContentIsRetriedOnce()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Stall),
                ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport, stallMs: 100));
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(2, result.Attempts);
        }

        [Theory]
        [InlineData(401, "authentication_error", "invalid x-api-key", StreamFailure.Unauthorized)]
        [InlineData(403, "permission_error", "forbidden", StreamFailure.Unauthorized)]
        [InlineData(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.", StreamFailure.LowCredit)]
        [InlineData(400, "invalid_request_error", "messages: bad", StreamFailure.HttpStatus)]
        public void ClientErrorsAreNotRetried(int status, string type, string message, StreamFailure expected)
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Error(status, type, message), ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));

            Assert.Equal(StreamStatus.Failed, result.Status);
            Assert.Equal(expected, result.Failure);
            Assert.Equal(1, result.Attempts);
            Assert.Equal(type, result.ErrorType);
            Assert.Equal(message, result.ErrorMessage);
            Assert.DoesNotContain(message, string.Join("\n", _log));
        }

        [Fact]
        public void CancelMidStreamAbortsAndIsRecordedAsCancelled()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Stall));
            var client = Client(transport, stallMs: 20000);
            var cancel = new CancellationTokenSource();
            var started = DateTime.UtcNow;

            var result = client.Stream(Ask(), u => { if (u.Kind == StreamUpdateKind.TextDelta) cancel.Cancel(); }, cancel.Token);

            Assert.Equal(StreamStatus.Cancelled, result.Status);
            Assert.Null(result.Message);
            Assert.Equal(1, result.Attempts);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            Assert.Contains("status=cancelled", Assert.Single(_log));
        }

        [Fact]
        public void CancelWhileStalledFromAnotherThreadUnblocksTheRead()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("start-only.sse", end: StreamEnd.Stall));
            var client = Client(transport, stallMs: 20000);
            var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var result = client.Stream(Ask(), null, cancel.Token);
            Assert.Equal(StreamStatus.Cancelled, result.Status);
        }

        [Fact]
        public void CancelWhileAnErrorBodyHangsStillCancels()
        {
            var hanging = ScriptedResponse.Error(503, "api_error", "Unavailable");
            hanging.End = StreamEnd.Stall;
            var client = Client(new StreamingFakeTransport(hanging), stallMs: 20000);
            var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var started = DateTime.UtcNow;

            var result = client.Stream(Ask(), null, cancel.Token);
            Assert.Equal(StreamStatus.Cancelled, result.Status);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void AMidStreamBillingErrorIsLowCreditAndNotRetried()
        {
            var body = Fixtures.Text("overloaded-error.sse").Replace("overloaded_error", "billing_error");
            var transport = new StreamingFakeTransport(new ScriptedResponse { Body = Encoding.UTF8.GetBytes(body) }, ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));
            Assert.Equal(StreamFailure.LowCredit, result.Failure);
            Assert.Equal(1, result.Attempts);
        }

        [Fact]
        public void CancelDuringBackoffStopsRetrying()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Error(529, "overloaded_error", "Overloaded"), ScriptedResponse.Fixture("text.sse"));
            var client = Client(transport);
            var cancel = new CancellationTokenSource();
            client.Timing.Wait = (delay, token) => cancel.Cancel();

            var result = client.Stream(Ask(), null, cancel.Token);
            Assert.Equal(StreamStatus.Cancelled, result.Status);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void CancelBeforeSendingSendsNothing()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var result = Client(transport).Stream(Ask(), null, cancel.Token);
            Assert.Equal(StreamStatus.Cancelled, result.Status);
        }

        [Fact]
        public void SendsPinnedHeadersAndAStreamingBody()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            Run(Client(transport));

            var request = Assert.Single(transport.Requests);
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages", request.Url);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value);
            Assert.Equal("2026-10-21", headers["anthropic-version"]);
            Assert.Equal("server-side-fallback-2026-07-01,compact-2026-01-12,mid-conversation-output-config-2026-07-01", headers["anthropic-beta"]);
            Assert.Equal(Key, headers["x-api-key"]);
            Assert.Equal("application/json", headers["Content-Type"]);

            using (var body = JsonDocument.Parse(request.Body))
            {
                var root = body.RootElement;
                Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
                Assert.True(root.GetProperty("stream").GetBoolean());
                Assert.Equal("default", root.GetProperty("fallbacks").GetString());
                Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
                Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
                Assert.Equal("1h", root.GetProperty("cache_control").GetProperty("ttl").GetString());
                Assert.Equal("You are Claudia.", root.GetProperty("system")[0].GetProperty("text").GetString());
                var edit = root.GetProperty("context_management").GetProperty("edits")[0];
                Assert.Equal("compact_20260112", edit.GetProperty("type").GetString());
                Assert.Equal(150000, edit.GetProperty("trigger").GetProperty("value").GetInt32());
                Assert.Equal("How far is the Mun?", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
                Assert.False(root.TryGetProperty("tool_choice", out _));
            }
        }

        [Fact]
        public void StrictToolsAndStructuredOutputGoInTheBody()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var request = Ask();
            request.Tools.Add(new ToolDefinition
            {
                Name = "read_orbit",
                Description = "Reads the orbit.",
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"vessel\":{\"type\":\"string\"}},\"required\":[\"vessel\"],\"additionalProperties\":false}",
            });
            request.OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"why\":{\"type\":\"string\"}},\"required\":[\"why\"],\"additionalProperties\":false}";
            Run(Client(transport), request);

            using (var body = JsonDocument.Parse(transport.Requests[0].Body))
            {
                var root = body.RootElement;
                var tool = root.GetProperty("tools")[0];
                Assert.True(tool.GetProperty("strict").GetBoolean());
                Assert.Equal("vessel", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());
                Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
                Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
            }
        }

        [Fact]
        public void LogHasOneRequestLineWithUsageAndNoTextOrKey()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            Run(Client(transport), Ask("PRIVATE-QUESTION"));

            var line = Assert.Single(_log);
            Assert.Matches(@"^\[ClaudiaKermin\] request id=\d+ POST /v1/messages model=claude-sonnet-5-5 status=completed http=200 attempts=1 stop=end_turn in=2048 out=21 cache_read=0 cache_write=1500 iterations=1 ms=\d+$", line);
            Assert.DoesNotContain("PRIVATE-QUESTION", line);
            Assert.DoesNotContain("Kerbin", line);
            Assert.DoesNotContain("STREAMSECRET", line);
            Assert.DoesNotContain("x-api-key", line);
        }

        [Fact]
        public void RawDumpReceivesTheStreamByteForByte()
        {
            var dump = new RecordingDump();
            Run(Client(new StreamingFakeTransport(ScriptedResponse.Fixture("unknown-block.sse", 9))), dump: dump);
            Assert.Equal(Fixtures.Text("unknown-block.sse"), dump.Text.ToString());
            Assert.All(dump.Attempts, a => Assert.Equal(1, a));
        }

        [Fact]
        public void FileDumpWritesEachAttemptToARequestFile()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck-dump-" + Guid.NewGuid().ToString("N"));
            try
            {
                var dump = new FileStreamDump(directory);
                var transport = new StreamingFakeTransport(
                    ScriptedResponse.Fixture("overloaded-error.sse"),
                    ScriptedResponse.Fixture("text.sse"));
                Run(Client(transport), dump: dump);

                var files = System.IO.Directory.GetFiles(directory);
                var text = System.IO.File.ReadAllText(Assert.Single(files));
                Assert.Equal(": attempt 1\n" + Fixtures.Text("overloaded-error.sse") + ": attempt 2\n" + Fixtures.Text("text.sse"), text);
            }
            finally
            {
                if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void BadJsonInAKnownEventFailsTheAttemptInsteadOfCrashing()
        {
            var broken = Encoding.UTF8.GetBytes(Fixtures.Text("text.sse").Replace("{\"type\":\"content_block_stop\",\"index\":0}", "{not json"));
            var transport = new StreamingFakeTransport(new ScriptedResponse { Body = broken }, ScriptedResponse.Fixture("text.sse"));
            var result = Run(Client(transport));
            Assert.Equal(StreamStatus.Completed, result.Status);
            Assert.Equal(2, result.Attempts);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
                if (match) return i;
            }
            return -1;
        }

        /// <summary>A fallback block as the stream would build it, made by replaying a one-block stream.</summary>
        private sealed class FallbackBlockForTest
        {
            public readonly ContentBlock Block;

            public FallbackBlockForTest()
            {
                var client = new ClaudeClient(new StreamingFakeTransport(ScriptedResponse.Fixture("compaction-fallback.sse")), () => Key, l => { });
                Block = client.Stream(Ask(), null, CancellationToken.None).Message.Content[0];
            }
        }

        /// <summary>Delivers bytes slowly but steadily, each chunk well inside the stall timeout.</summary>
        private sealed class SlowTransport : IHttpTransport
        {
            private readonly byte[] _body;
            private readonly int _chunk;
            private readonly int _delayMs;

            public SlowTransport(byte[] body, int chunk, int delayMs)
            {
                _body = body;
                _chunk = chunk;
                _delayMs = delayMs;
            }

            public HttpResponseData Send(HttpRequestSpec request) { throw new InvalidOperationException(); }

            public IHttpStream Open(HttpRequestSpec request, CancellationToken cancel)
            {
                return new Slow(this);
            }

            private sealed class Slow : IHttpStream
            {
                private readonly SlowTransport _owner;
                private int _position;

                public Slow(SlowTransport owner) { _owner = owner; }
                public int StatusCode { get { return 200; } }
                public string Header(string name) { return null; }

                public int Read(byte[] buffer, int offset, int count)
                {
                    Thread.Sleep(_owner._delayMs);
                    var n = Math.Min(Math.Min(count, _owner._chunk), _owner._body.Length - _position);
                    Array.Copy(_owner._body, _position, buffer, offset, n);
                    _position += n;
                    return n;
                }

                public void Abort() { }
                public void Dispose() { }
            }
        }
    }
}
