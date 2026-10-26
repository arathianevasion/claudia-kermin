// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Clear on a finished Mission: <see cref="DismissMission"/> drops it as the current Mission and keeps its folder.</summary>
    public sealed class MissionDismissTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage);
        }

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f)).ToArray());
        }

        /// <summary>A Mission with its script Running in Flight.</summary>
        private MissionControl Running(out StartScript script, params string[] extraReplies)
        {
            var mission = Control(Replies(new[] { "mission-plan.sse", "mission-sequence-plan.sse" }.Concat(extraReplies).ToArray()));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
            mission.Send(new Approve());
            script = mission.TakeCommands().OfType<StartScript>().Single();
            return mission;
        }

        private MissionControl Finished(MissionStage stage)
        {
            StartScript script;
            if (stage == MissionStage.Lost)
            {
                var lost = Running(out script);
                lost.Send(new ClaudiaKilled());
                return lost;
            }
            var mission = Running(out script, "mission-debrief.sse");
            var report = new Dictionary<string, object> { { "success", true }, { "reason", "done" } };
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, report, "", "", false), script.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            return mission;
        }

        /// <summary>The save state of a Mission saved as Ended, as an older save carries it: in memory, ending drops the Mission, so the stage persists only in saves.</summary>
        private MissionSaveState SavedEnded()
        {
            var saved = MissionSaveState.FromValues(Assert.Single(Finished(MissionStage.Succeeded).ExportStates()).ToValues());
            saved.Stage = MissionStage.Ended;
            return saved;
        }

        private string FolderOf(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", Assert.Single(mission.ExportStates()).Id);
        }

        [Theory]
        [InlineData(MissionStage.Succeeded)]
        [InlineData(MissionStage.Lost)]
        public void DismissingAFinishedMissionLeavesNoCurrentMissionAndKeepsItsFolder(MissionStage stage)
        {
            var mission = Finished(stage);
            Assert.Equal(stage, mission.View.Stage);
            var folder = FolderOf(mission);

            mission.Send(new DismissMission());

            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(mission.View.Log);
            Assert.True(mission.View.StartForm.CanStart);
            Assert.Empty(mission.ExportStates());
            Assert.True(File.Exists(Path.Combine(folder, "transcript.json")));
            Assert.True(File.Exists(Path.Combine(folder, "ended.json")));
        }

        [Fact]
        public void DismissingAnEndedMissionFromAnOlderSaveLeavesNoCurrentMissionAndKeepsItsFolder()
        {
            var saved = SavedEnded();
            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            Assert.Equal(MissionStage.Ended, later.View.Stage);

            later.Send(new DismissMission());

            Assert.Equal(MissionStage.None, later.View.Stage);
            Assert.Empty(later.View.Log);
            Assert.True(File.Exists(Path.Combine(_save.Path, "ClaudiaKermin", "missions", saved.Id, "transcript.json")));
        }

        [Fact]
        public void ALoadAfterDismissingAnEndedMissionDoesNotRestoreIt()
        {
            var saved = SavedEnded();
            var mission = Control(Replies());
            mission.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            mission.Send(new DismissMission());

            mission.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));

            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        [Fact]
        public void EndingAnAlreadyEndedMissionDoesNothing()
        {
            var saved = SavedEnded();
            var mission = Control(Replies());
            mission.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            var log = mission.View.Log.Count;

            mission.Send(new EndMission());

            Assert.Equal(MissionStage.Ended, mission.View.Stage);
            Assert.Equal(log, mission.View.Log.Count);
            Assert.False(File.Exists(Path.Combine(_save.Path, "ClaudiaKermin", "missions", saved.Id, "ended.json")));
        }

        [Fact]
        public void DismissingDoesNothingForAnActiveMission()
        {
            StartScript script;
            var mission = Running(out script);
            var log = mission.View.Log.Count;

            mission.Send(new DismissMission());

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal(log, mission.View.Log.Count);
            Assert.False(File.Exists(Path.Combine(FolderOf(mission), "ended.json")));
        }

        [Fact]
        public void DismissingWithNoMissionDoesNothing()
        {
            var mission = Control(Replies());
            mission.Send(new DismissMission());
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        [Theory]
        [InlineData(MissionStage.Succeeded)]
        [InlineData(MissionStage.Lost)]
        public void ALoadAfterDismissingDoesNotRestoreTheMission(MissionStage stage)
        {
            var mission = Finished(stage);
            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            mission.Send(new DismissMission());

            // The same game loading the save made before the dismissal, and a game started fresh.
            mission.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            Assert.Equal(MissionStage.None, mission.View.Stage);

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            Assert.Equal(MissionStage.None, later.View.Stage);
            Assert.Empty(later.View.Log);
        }

        [Fact]
        public void AFinishedMissionThatWasNotDismissedStillRestoresAfterALoad()
        {
            var mission = Finished(MissionStage.Succeeded);
            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));

            Assert.Equal(MissionStage.Succeeded, later.View.Stage);
        }
    }
}
