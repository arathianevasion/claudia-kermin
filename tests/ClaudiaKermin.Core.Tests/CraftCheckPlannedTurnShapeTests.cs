// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Craft Check plans the ascent (#371): the two ascent Warnings, and the planned turn_shape reaching the Mission,
    /// surviving save and load, and appearing in the Mission layer (only when the check planned one).
    /// </summary>
    public sealed class CraftCheckPlannedTurnShapeTests : IDisposable
    {
        private const string Goal = "Reach orbit round Kerbin.";
        private const string Model = "claude-sonnet-5-5";
        private const string Line = "The Craft Check planned turn_shape 0.35 for the launch.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(string fixture)
        {
            var transport = new StreamingFakeTransport(new[] { ScriptedResponse.Fixture(fixture, 64) });
            var client = new ClaudeClient(transport, () => "sk-ant-test", line => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json"))));
            return mission;
        }

        private void Check(MissionControl mission)
        {
            mission.Send(new RunCraftCheck(Goal, 10m, Model));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.CraftCheck.State == CraftCheckState.Running && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
        }

        [Fact]
        public void TheTwoAscentCodesParseAsWarnings()
        {
            foreach (var code in new[] { "ascent_cannot_reach_orbit", "ascent_thin_margin" })
            {
                var rule = CraftCheckRules.Find(code);
                Assert.NotNull(rule);
                Assert.Equal(CraftFindingSeverity.Warning, rule.Severity);
            }
            var reply = CraftCheckReply.Parse("{\"summary\":\"s\",\"findings\":[{\"code\":\"ascent_cannot_reach_orbit\",\"explanation\":\"e\",\"fix\":\"f\"}]}");
            Assert.Equal(CraftFindingSeverity.Warning, Assert.Single(reply.Findings).Severity);
        }

        [Fact]
        public void ThePlannedTurnShapeIsOptionalInTheReplyAndInTheSchema()
        {
            Assert.Equal(0.35, CraftCheckReply.Parse("{\"summary\":\"s\",\"planned_turn_shape\":0.35,\"findings\":[]}").PlannedTurnShape);
            Assert.Null(CraftCheckReply.Parse("{\"summary\":\"s\",\"findings\":[]}").PlannedTurnShape);
            // The schema can't carry an optional field (the API refuses it), so 0 is "none planned".
            Assert.Null(CraftCheckReply.Parse("{\"summary\":\"s\",\"planned_turn_shape\":0,\"findings\":[]}").PlannedTurnShape);
            Assert.Contains("\"planned_turn_shape\"", CraftCheckRules.SchemaJson());
        }

        [Fact]
        public void TheCraftCheckPromptTellsHerToPlanTheAscent()
        {
            Assert.Contains("call plan_ascent with the Mission Goal's first target_ap and the stage_limit the launch would use", CraftCheckPrompts.System);
        }

        [Fact]
        public void TheMissionLayerCarriesTheShapeOnlyWhenThereIsOne()
        {
            Assert.Contains(Line, MissionPrompts.MissionLayer("m1", Goal, "", 0.35));
            Assert.DoesNotContain("planned turn_shape", MissionPrompts.MissionLayer("m1", Goal));
        }

        [Fact]
        public void ThePlannedTurnShapeSurvivesSaveAndLoadAndIsAbsentWhenNotPlanned()
        {
            var state = new MissionSaveState { Id = "m1", PlannedTurnShape = 0.35 };
            Assert.Equal(0.35, MissionSaveState.FromValues(state.ToValues()).PlannedTurnShape);
            var none = new MissionSaveState { Id = "m1" };
            Assert.DoesNotContain("plannedTurnShape", none.ToValues().Keys);
            Assert.Null(MissionSaveState.FromValues(none.ToValues()).PlannedTurnShape);
        }

        [Fact]
        public void TheShapeReachesTheMissionStartedFromTheCheck()
        {
            var mission = Control("craft-check-ascent.sse");
            Check(mission);
            Assert.True(CraftCheckState.Done == mission.View.CraftCheck.State, mission.View.CraftCheck.Message);
            mission.Send(new AcknowledgeWarning("ascent_thin_margin"));

            mission.Send(new StartMission(Goal, 10m, Model));

            Assert.Equal(0.35, Assert.Single(mission.ExportStates()).PlannedTurnShape);
        }

        [Fact]
        public void AMissionStartedFromACheckThatPlannedNoShapeSavesNone()
        {
            var mission = Control("craft-check-pass.sse");
            Check(mission);

            mission.Send(new StartMission(Goal, 10m, Model));

            Assert.Null(Assert.Single(mission.ExportStates()).PlannedTurnShape);
        }
    }
}
