// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Threading;
using ClaudiaKermin.Core.Chat;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Clear: which action the button has, what typing /clear means, and the basic chat starting over.</summary>
    public class ClearTests
    {
        private readonly Background _background = new Background();

        private static MissionView View(MissionStage stage, int entries)
        {
            var view = new MissionView { Stage = stage };
            for (var i = 0; i < entries; i++) view.Log.Add(new LogEntry { Kind = LogKind.Notice, Text = "n" + i });
            return view;
        }

        [Fact]
        public void ClearIsDisabledWithNoMission()
        {
            Assert.Equal(ClearAction.None, ClearRules.Action(View(MissionStage.None, 0), 0));
        }

        [Theory]
        [InlineData(MissionStage.Succeeded)]
        [InlineData(MissionStage.Ended)]
        [InlineData(MissionStage.Lost)]
        public void ClearDismissesAFinishedMission(MissionStage stage)
        {
            Assert.Equal(ClearAction.Dismiss, ClearRules.Action(View(stage, 3), 0));
            Assert.Equal(ClearAction.Dismiss, ClearRules.Action(View(stage, 0), 0));
        }

        [Theory]
        [InlineData(MissionStage.Running)]
        [InlineData(MissionStage.AwaitingPlanApproval)]
        [InlineData(MissionStage.Paused)]
        public void ClearHidesTheShownEntriesOfAnActiveMissionAndIsDisabledOnceTheyAreHidden(MissionStage stage)
        {
            Assert.Equal(ClearAction.Hide, ClearRules.Action(View(stage, 3), 0));
            Assert.Equal(ClearAction.None, ClearRules.Action(View(stage, 3), 3));
            Assert.Equal(ClearAction.Hide, ClearRules.Action(View(stage, 4), 3));
            Assert.Equal(ClearAction.None, ClearRules.Action(View(stage, 0), 0));
        }

        [Theory]
        [InlineData("/clear", true)]
        [InlineData("  /clear \n", true)]
        [InlineData("/clear now", false)]
        [InlineData("/Clear", false)]
        [InlineData("/compact", false)]
        [InlineData("please /clear", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyExactlySlashClearIsTheCommand(string text, bool expected)
        {
            Assert.Equal(expected, ClearRules.IsCommand(text));
        }

        [Fact]
        public void TheTooltipSaysWhatClearWillDo()
        {
            Assert.Equal("Clear the window. Claudia keeps everything she knows.", ClearRules.Tooltip(ClearAction.Hide));
            Assert.Equal("Close this finished Mission and start fresh. Its folder and transcript are kept.", ClearRules.Tooltip(ClearAction.Dismiss));
        }

        // ---- The basic chat ----

        private ChatSession Session(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            return new ChatSession(client, _background, () => true);
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
        public void ClearingTheBasicChatStartsANewConversation()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("text.sse"), ScriptedResponse.Fixture("text.sse"));
            var chat = Session(transport);
            chat.Send("How far is the Mun?");
            DrainUntil(() => !chat.Busy);
            Assert.NotEmpty(chat.Conversation);
            var version = chat.Version;

            chat.Clear();

            Assert.Empty(chat.Lines);
            Assert.Empty(chat.Conversation);
            Assert.False(chat.CanRetry);
            Assert.NotEqual(version, chat.Version);

            // The next question carries no history.
            chat.Send("And Minmus?");
            DrainUntil(() => !chat.Busy);
            Assert.Equal(2, chat.Conversation.Count);
        }

        [Fact]
        public void AReplyStillInProgressWhenTheBasicChatIsClearedIsDropped()
        {
            var response = ScriptedResponse.Fixture("text.sse", 64);
            response.PauseAt = 100;
            var chat = Session(new StreamingFakeTransport(response));
            chat.Send("How far is the Mun?");
            Assert.True(chat.Busy);

            chat.Clear();
            Assert.False(chat.Busy);
            response.Gate.Set();
            _background.Drain();
            Thread.Sleep(50);
            _background.Drain();

            Assert.Empty(chat.Lines);
            Assert.Empty(chat.Conversation);
        }
    }
}
