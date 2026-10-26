// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// A Paused quickload keeps the unanswered turn only when it is the request the save paused from: the file is
    /// the latest turn sent, so a save from an earlier stage must not resend a later stage's request.
    /// </summary>
    public sealed class MissionUnansweredStageGuardTests : IDisposable
    {
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
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

        /// <summary>A Mission Paused from Planning by a refusal; the Mission Plan request is the unanswered turn on disk.</summary>
        private MissionControl PausedFromPlanning(StreamingFakeTransport transport)
        {
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            return mission;
        }

        private static IList<MissionSaveState> PausedFrom(MissionControl mission, MissionStage stageBeforePause)
        {
            var values = mission.ExportStates()[0].ToValues();
            Assert.Equal("Planning", values["stageBeforePause"]);
            values["stageBeforePause"] = stageBeforePause.ToString();
            return new List<MissionSaveState> { MissionSaveState.FromValues(values) };
        }

        [Fact]
        public void APausedQuickloadDropsAnUnansweredTurnFromAnotherStageSoRetryHasNothingToResend()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = PausedFromPlanning(transport);
            // The save paused from Sequencing, but the file on disk is the Mission Plan request sent since.
            var saved = PausedFrom(mission, MissionStage.Sequencing);

            mission.Send(new MissionsRestored(_save.Path, saved));
            DrainUntil(mission, v => !v.Busy);
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Single(transport.Requests);

            // A resend would show as a busy Mission and a second request; give it the same time the keeping test gets.
            mission.Send(new Retry());
            Assert.False(mission.View.Busy);
            Thread.Sleep(200);
            _background.Drain();
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void APausedQuickloadKeepsAnUnansweredTurnFromTheStageItPausedFromSoRetryResendsIt()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse"), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = PausedFromPlanning(transport);
            var saved = PausedFrom(mission, MissionStage.Planning);

            mission.Send(new MissionsRestored(_save.Path, saved));
            DrainUntil(mission, v => !v.Busy);
            Assert.Single(transport.Requests);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, transport.Requests.Count);
        }
    }
}
