// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The window-behaviour inputs of the real <see cref="MissionView"/>: the UT, the entry times and the needs-the-player trigger.</summary>
    public sealed class MissionAttentionTests : IDisposable
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

        private MissionControl PlannedMission(AttentionWatch watch, out Attention alert)
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            var snapshot = OnPad();
            snapshot.Ut = 5000;
            mission.Send(snapshot);
            mission.Send(new UtAdvanced(5000));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission("Reach orbit.", 10m, "claude-sonnet-5-5"));
            Assert.Null(watch.Observe(mission.View));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.Stage != MissionStage.AwaitingPlanApproval && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            alert = watch.Observe(mission.View);
            return mission;
        }

        [Fact]
        public void ThePlanWaitingForApprovalIsAnApprovalAlert()
        {
            Attention alert;
            PlannedMission(new AttentionWatch(), out alert);
            Assert.NotNull(alert);
            Assert.Equal(AttentionKind.Approval, alert.Kind);
        }

        [Fact]
        public void TheViewCarriesTheUtAndStampsEachLogEntryWithIt()
        {
            Attention alert;
            var mission = PlannedMission(new AttentionWatch(), out alert);
            Assert.Equal(5000, mission.View.Ut);
            Assert.All(mission.View.Log, e => Assert.Equal(5000, e.Ut));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
        }

        [Fact]
        public void TheCraftOnThePadIsOnTheCraftCheckViewForTheMissionBoard()
        {
            Attention alert;
            var mission = PlannedMission(new AttentionWatch(), out alert);
            var check = mission.View.CraftCheck;
            Assert.Equal(OnPad().Craft.Name, check.CraftName);
            Assert.Equal(new PlayerConfig().CraftCheckMarginPercent, check.MarginPercent);
        }
    }
}
