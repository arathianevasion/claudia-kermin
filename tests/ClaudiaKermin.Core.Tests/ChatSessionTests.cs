// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Chat;
using ClaudiaKermin.Core.Claude;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The chat model behind the chat window, driven like the window drives it: Send, Cancel, Retry, Drain.</summary>
    public class ChatSessionTests
    {
        private readonly Background _background = new Background();

        private ChatSession Session(StreamingFakeTransport transport, bool hasKey = true, int stallMs = 2000)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.StallTimeout = TimeSpan.FromMilliseconds(stallMs);
            client.Timing.Wait = (d, c) => { };
            return new ChatSession(client, _background, () => hasKey);
        }

        private void DrainUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(), "condition not reached");
        }

        [Fact]
        public void PlayerTextGoesToClaudeAndTheReplyStreamsIn()
        {
            // Pause the stream just after the first text delta.
            var response = ScriptedResponse.Fixture("text.sse", 9);
            var fixture = Fixtures.Text("text.sse");
            var secondDelta = fixture.IndexOf("event: content_block_delta", fixture.IndexOf("Hello from Kerbin", StringComparison.Ordinal), StringComparison.Ordinal);
            response.PauseAt = System.Text.Encoding.UTF8.GetByteCount(fixture.Substring(0, secondDelta));
            var transport = new StreamingFakeTransport(response);
            var chat = Session(transport);

            Assert.True(chat.Send("  How far is the Mun?  "));
            Assert.True(chat.Busy);
            Assert.Equal(ChatSpeaker.Player, chat.Lines[0].Speaker);
            Assert.Equal("How far is the Mun?", chat.Lines[0].Text);
            Assert.True(chat.Lines[1].Streaming);

            DrainUntil(() => chat.Lines[1].Text == "Hello from Kerbin");
            Assert.True(chat.Lines[1].Streaming);
            response.Gate.Set();
            DrainUntil(() => !chat.Busy);

            var reply = chat.Lines[1];
            Assert.Equal(ChatSpeaker.Claudia, reply.Speaker);
            Assert.False(reply.Streaming);
            Assert.StartsWith("Hello from Kerbin — the Mün", reply.Text);
            Assert.Equal(2, chat.Conversation.Count);

            using (var body = JsonDocument.Parse(transport.Requests[0].Body))
            {
                var root = body.RootElement;
                Assert.Equal(ChatSession.DefaultModel, root.GetProperty("model").GetString());
                Assert.Contains("Claudia", root.GetProperty("system")[0].GetProperty("text").GetString());
                Assert.Equal("How far is the Mun?", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
            }
        }

        [Fact]
        public void TheNextMessageCarriesTheWholeConversationIncludingRawBlocks()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("unknown-block.sse"), ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);

            chat.Send("Chart it");
            DrainUntil(() => !chat.Busy);
            chat.Send("Thanks");
            DrainUntil(() => !chat.Busy);

            var sent = transport.Requests[1].Body;
            Assert.Contains("{\"type\":\"star_chart\" , \"payload\":", sent);
            using (var body = JsonDocument.Parse(sent))
            {
                var messages = body.RootElement.GetProperty("messages");
                Assert.Equal(3, messages.GetArrayLength());
                Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
                Assert.Equal("Thanks", messages[2].GetProperty("content")[0].GetProperty("text").GetString());
            }
            Assert.Equal(4, chat.Conversation.Count);
        }

        [Fact]
        public void ThinkingShowsUntilTextArrives()
        {
            // The thinking block arrives, then the stream goes quiet until cancelled.
            var full = Fixtures.Text("thinking-tool-use.sse");
            var thinkingOnly = full.Substring(0, full.IndexOf("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1", StringComparison.Ordinal));
            var transport = new StreamingFakeTransport(new ScriptedResponse
            {
                Body = System.Text.Encoding.UTF8.GetBytes(thinkingOnly),
                End = StreamEnd.Stall,
            });
            var chat = Session(transport, stallMs: 20000);
            chat.Send("Check the orbit");

            DrainUntil(() => chat.Lines[1].Thinking);
            Assert.Equal("", chat.Lines[1].Text);
            chat.Cancel();
            DrainUntil(() => !chat.Busy);
            Assert.False(chat.Lines[1].Thinking);
        }

        [Fact]
        public void CancelAddsNothingToTheConversationAndOffersRetry()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Stall),
                ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport, stallMs: 20000);

            chat.Send("Tell me a long story");
            DrainUntil(() => chat.Lines[1].Text.Length > 0);
            chat.Cancel();
            DrainUntil(() => !chat.Busy);

            Assert.Empty(chat.Conversation);
            Assert.Equal(ChatSpeaker.Notice, chat.Lines[1].Speaker);
            Assert.Contains("cancelled", chat.Lines[1].Text);
            Assert.True(chat.CanRetry);

            chat.Retry();
            DrainUntil(() => !chat.Busy);
            Assert.Equal(2, chat.Conversation.Count);
            Assert.Equal("Tell me a long story", ((TextBlock)chat.Conversation[0].Content[0]).Text);
            Assert.False(chat.CanRetry);
        }

        [Fact]
        public void ARetryAfterContentClearsThePartialReply()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("partial-text.sse", end: StreamEnd.Break),
                ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);

            chat.Send("Hi");
            DrainUntil(() => !chat.Busy);
            Assert.DoesNotContain("Half a sent", chat.Lines[1].Text);
            Assert.StartsWith("Hello from Kerbin", chat.Lines[1].Text);
        }

        [Fact]
        public void FailureShowsANoticeAndCanBeRetried()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Error(401, "authentication_error", "invalid x-api-key"),
                ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);

            chat.Send("Hi");
            DrainUntil(() => !chat.Busy);

            Assert.Equal(ChatSpeaker.Notice, chat.Lines[1].Speaker);
            Assert.Contains("rejected the API key", chat.Lines[1].Text);
            Assert.Empty(chat.Conversation);
            Assert.True(chat.CanRetry);
        }

        [Fact]
        public void ARefusedChatQuestionGetsAnInCharacterLine()
        {
            var chat = Session(new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse")));
            chat.Send("Something she declines");
            DrainUntil(() => !chat.Busy);

            Assert.Equal(ChatSpeaker.Claudia, chat.Lines[1].Speaker);
            Assert.Equal(ChatSession.RefusalLine, chat.Lines[1].Text);
            Assert.Empty(chat.Conversation);
        }

        [Fact]
        public void AFullContextWindowPointsAtClear()
        {
            var chat = Session(new StreamingFakeTransport(ScriptedResponse.Fixture("context-window.sse")));
            chat.Send("One more thing");
            DrainUntil(() => !chat.Busy);

            Assert.Equal(ChatSpeaker.Notice, chat.Lines[1].Speaker);
            Assert.Equal("This chat has grown too long for Claudia to read. Clear it to start a new conversation.", chat.Lines[1].Text);
        }

        [Fact]
        public void TheBasicChatKeepsThresholdCompactionAtItsConfiguredTrigger()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);
            chat.CompactionTriggerTokens = 90000;
            chat.Send("How far is the Mun?");
            DrainUntil(() => !chat.Busy);

            using (var body = JsonDocument.Parse(transport.Requests[0].Body))
            {
                var edit = body.RootElement.GetProperty("context_management").GetProperty("edits")[0];
                Assert.Equal("compact_20260112", edit.GetProperty("type").GetString());
                Assert.Equal(90000, edit.GetProperty("trigger").GetProperty("value").GetInt32());
                Assert.False(body.RootElement.TryGetProperty("compaction", out _));
            }
        }

        [Fact]
        public void AFallbackServedReplySaysWhoAnswered()
        {
            var chat = Session(new StreamingFakeTransport(ScriptedResponse.Fixture("compaction-fallback.sse")));
            chat.Send("Status?");
            DrainUntil(() => !chat.Busy);

            Assert.Equal("Still on track.", chat.Lines[1].Text);
            Assert.Equal("Answered by claude-opus-5-5.", chat.Lines.Last().Text);
            Assert.IsType<CompactionBlock>(chat.Conversation[1].Content[1]);
        }

        [Fact]
        public void NoKeyMeansNothingIsSent()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport, hasKey: false);

            Assert.False(chat.Send("Hello"));
            Assert.Empty(transport.Requests);
            Assert.Contains("API key", Assert.Single(chat.Lines).Text);
        }

        [Fact]
        public void EmptyTextAndSendingWhileBusyAreRefused()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);

            Assert.False(chat.Send("   "));
            Assert.True(chat.Send("One"));
            Assert.False(chat.Send("Two"));
            DrainUntil(() => !chat.Busy);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void RawDumpIsUsedOnlyWhenSet()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"), ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);
            var dump = new RecordingDump();

            chat.Send("Off");
            DrainUntil(() => !chat.Busy);
            Assert.Equal(0, dump.Text.Length);

            chat.RawDump = dump;
            chat.Send("On");
            DrainUntil(() => !chat.Busy);
            Assert.Equal(Fixtures.Text("text.sse"), dump.Text.ToString());
        }
    }
}
