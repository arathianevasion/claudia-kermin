// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
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
    /// "Claudia is thinking…": every request the core sends sets <see cref="MissionView.Busy"/> while it is in flight, and
    /// the view clears it on the reply, on an error and on a Pause. Each reply is held half-streamed so the test can look.
    /// </summary>
    public sealed class MissionBusyTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static ScriptedResponse Held(string fixture)
        {
            var response = ScriptedResponse.Fixture(fixture, 64);
            response.PauseAt = 200;
            return response;
        }

        private MissionControl Control(StreamingFakeTransport transport, bool routed = true)
        {
            var client = new ClaudeClient(routed ? new CraftCheckRouter(transport) : (IHttpTransport)transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(1000));
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

        // Waits until the held request has reached the transport, so the Mission is mid-request.
        private void WaitForRequests(StreamingFakeTransport transport, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (transport.Requests.Count < count && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            Assert.True(transport.Requests.Count >= count);
        }

        private static void AssertBusyWith(MissionView view, string line)
        {
            Assert.True(view.Busy);
            Assert.Equal(line, MissionWindowRules.BusyLine(view));
            Assert.Equal(line, MissionWindowRules.StripLine(view));
        }

        private static void AssertIdle(MissionView view)
        {
            Assert.False(view.Busy);
            Assert.Equal("", view.Activity);
            Assert.Equal("", MissionWindowRules.BusyLine(view));
        }

        [Fact]
        public void ACraftCheckIsBusyWhileItRunsAndClearsOnItsReply()
        {
            var reply = Held("craft-check-pass.sse");
            var transport = new StreamingFakeTransport(reply);
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, Sonnet));

            Assert.Equal(CraftCheckState.Running, mission.View.CraftCheck.State);
            AssertBusyWith(mission.View, "Claudia is checking the craft…");

            reply.Gate.Set();
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            AssertIdle(mission.View);
        }

        [Fact]
        public void ACraftCheckThatFailsClearsTheBusyLine()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Error(401, "authentication_error", "bad key"));
            // Not routed: the Craft Check's own request gets the error.
            var mission = Control(transport, routed: false);
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, Sonnet));

            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            Assert.Equal(CraftCheckState.Error, mission.View.CraftCheck.State);
            AssertIdle(mission.View);
        }

        [Fact]
        public void APlanRequestIsBusyUntilThePlanArrives()
        {
            var plan = Held("mission-plan.sse");
            var transport = new StreamingFakeTransport(plan);
            var mission = Control(transport);
            mission.Send(OnPad());
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            WaitForRequests(transport, 1);
            // The Craft Check is routed to its own fixture; the Mission's plan request is the one held.
            Assert.True(mission.View.Busy);

            plan.Gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            AssertIdle(mission.View);
        }

        [Fact]
        public void AnAnswerIsBusyUntilClaudiaReplies()
        {
            var answer = Held("mission-answer.sse");
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse", 64), answer);
            var mission = Control(transport);
            mission.Send(OnPad());
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            AssertIdle(mission.View);

            mission.Send(new AskQuestion("How long will it take?"));
            AssertBusyWith(mission.View, "Claudia is thinking…");

            answer.Gate.Set();
            DrainUntil(mission, v => !v.Busy);
            AssertIdle(mission.View);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
        }

        [Fact]
        public void ADebriefIsBusyUntilItArrives()
        {
            var debrief = Held("mission-debrief.sse");
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse", 64), ScriptedResponse.Fixture("mission-sequence-plan.sse", 64), debrief);
            var mission = Control(transport);
            mission.Send(OnPad());
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            AssertIdle(mission.View);

            var report = new System.Collections.Generic.Dictionary<string, object> { { "success", true }, { "reason", "link check" }, { "situation", "PRELAUNCH" } };
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, report, "", "", false), start.RunId));
            AssertBusyWith(mission.View, "Claudia is thinking…");

            debrief.Gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            AssertIdle(mission.View);
        }

        [Fact]
        public void AFailedRequestClearsTheBusyLineAndPausesTheMission()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Error(401, "authentication_error", "bad key"));
            var mission = Control(transport);
            mission.Send(OnPad());
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            AssertIdle(mission.View);
        }
    }
}
