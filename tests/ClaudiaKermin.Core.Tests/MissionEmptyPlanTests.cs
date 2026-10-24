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
    /// A Mission Plan with no sequences is how Claudia declines a goal that can't be flown (issue #117):
    /// a valid reply, kept and paid for, with Change something and End Mission but nothing to Approve.
    /// Driven through the Mission seam with hand-written streams.
    /// </summary>
    public sealed class MissionEmptyPlanTests : IDisposable
    {
        private const string Goal = "Quicksave the game with a kOS script, then report.";
        private const string Say = "0 m/s, no sequences: I can't quicksave from a script";
        private const string Why = "A quicksave from kOS is on the forbidden list, so no script can fly this goal.";
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        /// <summary>Start in Flight and run until the first reply has been applied (or the Mission paused).</summary>
        private void ToFirstReply(MissionControl mission)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval || v.Stage == MissionStage.Paused);
        }

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                return string.Join("\n", messages.Select(m => m.GetRawText()));
            }
        }

        [Fact]
        public void AMissionPlanWithNoSequencesIsClaudiasAnswerNotAFormatError()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Empty(mission.View.PauseReason);
            var claudia = mission.View.Log.Where(e => e.Kind == LogKind.Claudia).ToArray();
            var line = Assert.Single(claudia);
            Assert.Contains(Say, line.Text);
            Assert.Contains(Why, line.Text);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.StartsWith(Say, mission.View.LatestLine);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, mission.View.QuickReplies);
        }

        [Fact]
        public void ThereIsNothingToApproveWhenThePlanHasNoSequences()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new Approve());
            mission.Send(new ConfirmApproval(true));
            _background.Drain();

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void ChangeSomethingPlansAgainWithThePlayersTextAndANormalPlanCanBeApproved()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-empty.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new ChangeSomething("Just prove the kOS link instead."));
            DrainUntil(mission, v => transport.Requests.Count == 2 && v.Stage == MissionStage.AwaitingPlanApproval && v.PlanSequences.Count == 1);
            Assert.Contains("Just prove the kOS link instead.", LastUserText(transport.Requests[1]));
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        [Fact]
        public void TheReplyIsPaidForAndLoggedAsCompletedNotOffSchema()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.True(mission.View.Spent > 0m);
            var log = CostLog(mission);
            Assert.Contains("\"event\":\"paid\"", log);
            Assert.Contains("\"outcome\":\"completed\"", log);
            Assert.DoesNotContain("off_schema", log);
        }

        [Fact]
        public void AReplyWithNoMissionPlanStillPausesAsNotFitting()
        {
            // mission-answer.sse is a well-formed reply with no mission_plan object.
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains("didn't fit: no Mission Plan", mission.View.PauseReason);
            Assert.Contains("\"outcome\":\"off_schema\"", CostLog(mission));
        }

        [Fact]
        public void TheWaitSurvivesASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);
            var saved = mission.ExportStates();

            mission.Send(new MissionsRestored(_save.Path, saved));

            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Empty(mission.View.PlanSequences);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Claudia && e.Text.Contains(Say));
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Single(transport.Requests);

            // A fresh game over the same save comes back to the same place.
            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.AwaitingPlanApproval, reloaded.View.Stage);
            Assert.Equal(new[] { QuickReply.ChangeSomething, QuickReply.EndMission }, reloaded.View.QuickReplies);
        }

        [Fact]
        public void EndMissionWorksWhileWaitingHere()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-empty.sse"));
            var mission = Control(transport);
            ToFirstReply(mission);

            mission.Send(new EndMission());
            Assert.False(mission.View.CanEndMission);
            Assert.True(mission.View.StartForm.CanStart);
        }
    }
}
