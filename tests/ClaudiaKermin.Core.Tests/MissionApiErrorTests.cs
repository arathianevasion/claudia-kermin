// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Chat;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// API errors, refusals and unavailable models (story 134), replayed from hand-written SSE and JSON
    /// through the real client and the Mission seam; nothing here touches the network.
    /// </summary>
    public sealed class MissionApiErrorTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Opus = "claude-opus-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json"))));
            return mission;
        }

        private void Start(MissionControl mission)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, Sonnet));
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

        private static HttpRequestSpec[] MessageRequests(StreamingFakeTransport transport)
        {
            return transport.Requests.Where(r => r.Body != null && r.Body.Contains("\"mission_plan\"")).ToArray();
        }

        // ---- Fallbacks ----

        [Fact]
        public void EveryRequestAsksForTheDefaultFallbackChainWithTheBetaHeader()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var request = MessageRequests(transport).First();
            Assert.Contains("\"fallbacks\":\"default\"", request.Body);
            var beta = request.Headers.Single(h => h.Key == "anthropic-beta").Value;
            Assert.Contains("server-side-fallback-2026-07-01", beta);
        }

        [Fact]
        public void AFallbackServedTurnIsLoggedAsAnsweredByAndKeepsTheModelLock()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-fallback.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(Sonnet, mission.View.Model);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Claudia && e.AnsweredBy == Opus);
            // Costed at the fallback model's own rate, not the locked model's.
            Assert.Equal(0.0234m, mission.View.Spent);
        }

        // ---- Retries exhausted, 401/403, low credit ----

        [Fact]
        public void RetriesExhaustedPauseTheMissionWithRetryAndRetryWorks()
        {
            // One attempt and three retries before any content: four overloaded responses, then a good one for Retry.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Error(529, "overloaded_error", "Overloaded"),
                ScriptedResponse.Error(529, "overloaded_error", "Overloaded"),
                ScriptedResponse.Error(529, "overloaded_error", "Overloaded"),
                ScriptedResponse.Error(529, "overloaded_error", "Overloaded"),
                ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.Retry }, mission.View.QuickReplies.ToArray());
            Assert.Equal(4, MessageRequests(transport).Length);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        [Theory]
        [InlineData(401, "authentication_error", "invalid x-api-key", "rejected the API key")]
        [InlineData(403, "permission_error", "forbidden", "rejected the API key")]
        [InlineData(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.", "credit balance is too low")]
        public void AKeyOrCreditProblemPausesWithAClearMessageAndIsNotRetried(int status, string type, string message, string expected)
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Error(status, type, message));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Contains(expected, mission.View.PauseReason);
            Assert.Single(MessageRequests(transport));
            // Turned away before any reply: nothing was billed.
            Assert.Equal(0m, mission.View.Spent);
            Assert.Equal(0m, mission.View.HeldCharges);
        }

        // ---- Refusals ----

        [Fact]
        public void ARefusedQuestionGetsTheInCharacterLineAndNothingElse()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("refusal.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            mission.Send(new AskQuestion("How do I build a bomb?"));
            DrainUntil(mission, v => !v.Busy);

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(ChatSession.RefusalLine, mission.View.LatestLine);
            Assert.Equal("", mission.View.PauseReason);
            Assert.DoesNotContain(QuickReply.Retry, mission.View.QuickReplies);
            Assert.Equal(2, MessageRequests(transport).Length);
        }

        [Fact]
        public void ARefusedPlanPausesForRetryRetryWithANoteOrEndMissionAndNeverRetriesItself()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.EndMission }, mission.View.QuickReplies.ToArray());
            Thread.Sleep(50);
            _background.Drain();
            Assert.Single(MessageRequests(transport));
            // Billed: the refusal counts against the Spending Cap.
            Assert.Equal(0.00006m, mission.View.Spent);

            mission.Send(new RetryWithNote("It is a harmless test craft."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, MessageRequests(transport).Length);
            Assert.Contains("It is a harmless test craft.", MessageRequests(transport)[1].Body);
            Assert.DoesNotContain("harmless", MessageRequests(transport)[0].Body);
        }

        [Fact]
        public void ARefusedPlansPlainRetrySendsTheSameTurnAgain()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, MessageRequests(transport).Length);
            Assert.Equal("", mission.View.PauseReason);
        }

        // ---- Retired or not-found model ----

        [Fact]
        public void ANotFoundModelPausesWithChooseModelAndANoticeAndThenCarriesOnWithTheChosenOne()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Error(404, "not_found_error", "model: claude-sonnet-5-5"),
                ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Equal(new[] { QuickReply.ChooseModel, QuickReply.EndMission }, mission.View.QuickReplies.ToArray());
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("can't use " + Sonnet));
            Assert.Single(MessageRequests(transport));
            Assert.Equal(0m, mission.View.Spent);

            // Plain Retry is not on offer, and choosing the model that is gone does nothing.
            mission.Send(new Retry());
            mission.Send(new ChooseModel(Sonnet));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);

            mission.Send(new ChooseModel(Opus));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(Opus, mission.View.Model);
            Assert.Contains("\"model\":\"" + Opus + "\"", MessageRequests(transport)[1].Body);
        }

        [Fact]
        public void AModelNotFoundErrorEventInTheStreamIsTheSameFailureAndIsNotRetried()
        {
            var transport = new StreamingFakeTransport(new ScriptedResponse
            {
                Body = System.Text.Encoding.UTF8.GetBytes(
                    "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: gone\"}}\n\n"),
            });
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            var request = new MessagesRequest { Model = "gone", MaxTokens = 10 };
            request.Messages.Add(Message.UserText("hi"));
            var result = client.Stream(request, null, CancellationToken.None);

            Assert.Equal(StreamStatus.Failed, result.Status);
            Assert.Equal(StreamFailure.ModelNotFound, result.Failure);
            Assert.Equal(1, result.Attempts);
        }
    }
}
