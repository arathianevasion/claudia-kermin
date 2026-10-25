// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// In-script warp (issue #150, ADR 0004), driven through the Mission seam: a sequence that waits longer than an
    /// orbit runs as one approved sequence with no Due Time, and while its script warps, warp drops to 1x (and the
    /// script holds it there) on a Pause, link loss, the Spending Cap, a stock alarm or Manual Control.
    /// </summary>
    public sealed class ScriptWarpTests : IDisposable
    {
        private const string Goal = "Hold in orbit for three orbits, then deorbit.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        /// <summary>The 3-orbit hold, then deorbit: one script, its wait inside it.</summary>
        private const string HoldScript =
            "local res is coast(lexicon(\"until\", time:seconds + 3 * ship:orbit:period)).\n"
            + "if res:success { add node(time:seconds + 300, 0, 0, -60). set res to exec_node(). }\n"
            + "addons:claudia:report(res).\n";

        // ---- A wait longer than an orbit, inside one sequence ----

        [Fact]
        public void ASequencePlanThatWaitsLongerThanAnOrbitIsApprovedAndRunsWithNoDueTime()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse", 64), HoldPlan());
            var mission = Control(transport);
            ToSequencePlan(mission);

            // No Due Time: no alarm, no Warp to Due Time, and Approve is on offer straight away.
            var offered = mission.TakeCommands();
            Assert.Empty(offered.OfType<SetDueTimeAlarm>());
            Assert.False(mission.View.DueTime.HasDueTime);
            Assert.Equal(QuickReply.Approve, mission.View.QuickReplies.First());

            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var start = Assert.Single(mission.TakeCommands().OfType<StartScript>());
            // What runs is the script with the Core Library, whose waits warp on rails with no cap.
            Assert.Equal(CoreLibrary.Bundle(HoldScript), start.Source);
            Assert.DoesNotContain("wait_cap", start.Source);
            Assert.Contains("function coast {", start.Source);
        }

        // ---- Interrupts ----

        [Fact]
        public void TakingManualControlWhileTheScriptWarpsDropsWarpAndHandingBackLetsItGo()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse", 64), HoldPlan()));
            ToRunning(mission);

            mission.Send(new TakeManualControl());
            var commands = mission.TakeCommands();
            Assert.Single(commands.OfType<DropWarp>());
            Assert.Equal("you have Manual Control", Assert.Single(commands.OfType<HoldScriptWarp>()).Reason);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text == "Warp dropped to 1x: you have Manual Control.");

            // Held, not dropped again on every input.
            mission.Send(new UtAdvanced(Pad().Ut + 100));
            Assert.Empty(mission.TakeCommands().OfType<DropWarp>());

            mission.Send(new HandBack(""));
            Assert.Equal("", Assert.Single(mission.TakeCommands().OfType<HoldScriptWarp>()).Reason);
        }

        [Fact]
        public void LosingTheLinkWhileTheScriptWarpsDropsWarpOnceThenTheWaitWarpsOn()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse", 64), HoldPlan()));
            ToRunning(mission);
            var ut = Pad().Ut;

            mission.Send(Pad(s => { s.HasLink = false; s.Ut = ut + 1; }));
            var commands = mission.TakeCommands();
            Assert.Single(commands.OfType<DropWarp>());
            Assert.Equal(ScriptWarp.LinkLost, Assert.Single(commands.OfType<HoldScriptWarp>()).Reason);
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            // Still no link: no second drop; after the moment's hold the script may warp again.
            mission.Send(Pad(s => { s.HasLink = false; s.Ut = ut + 5; }));
            Assert.Empty(mission.TakeCommands());
            mission.Send(Pad(s => { s.HasLink = false; s.Ut = ut + 1 + ScriptWarp.MomentHoldSeconds; }));
            Assert.Equal("", Assert.Single(mission.TakeCommands().OfType<HoldScriptWarp>()).Reason);

            // The link back and lost again is a new moment.
            mission.Send(Pad(s => { s.HasLink = true; s.Ut = ut + 20; }));
            mission.Send(Pad(s => { s.HasLink = false; s.Ut = ut + 21; }));
            Assert.Single(mission.TakeCommands().OfType<DropWarp>());
        }

        [Fact]
        public void AStockAlarmWhileTheScriptWarpsDropsWarpButNotWhenNothingRuns()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse", 64), HoldPlan()));
            ToSequencePlan(mission);
            mission.TakeCommands();
            mission.Send(new StockAlarmFired());
            Assert.Empty(mission.TakeCommands());

            mission.Send(new Approve());
            mission.TakeCommands();
            mission.Send(new StockAlarmFired());
            var commands = mission.TakeCommands();
            Assert.Single(commands.OfType<DropWarp>());
            Assert.Equal(ScriptWarp.StockAlarm, Assert.Single(commands.OfType<HoldScriptWarp>()).Reason);

            mission.Send(new UtAdvanced(Pad().Ut + ScriptWarp.MomentHoldSeconds + 1));
            Assert.Equal("", Assert.Single(mission.TakeCommands().OfType<HoldScriptWarp>()).Reason);
        }

        [Fact]
        public void TheSpendingCapsWaitingCallHoldsWarpUntilTheCapIsRaised()
        {
            // The priced plan and Sequence Plan cost $1.08, so a question's worst case no longer fits a $1.20 cap.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            ToRunning(mission, 1.20m);

            mission.Send(new AskQuestion("How long will it take?"));
            Assert.Contains("Spending Cap", mission.View.CapWait);
            var commands = mission.TakeCommands();
            Assert.Single(commands.OfType<DropWarp>());
            Assert.Equal("Claudia's next request waits at the Spending Cap", Assert.Single(commands.OfType<HoldScriptWarp>()).Reason);

            mission.Send(new RaiseCap(10m));
            Assert.Equal("", Assert.Single(mission.TakeCommands().OfType<HoldScriptWarp>()).Reason);
        }

        [Fact]
        public void TheHoldRuleCoversAPauseTheCapAnAbortRecommendationAMomentAndManualControl()
        {
            Assert.Equal("the Mission is Paused", ScriptWarp.HoldReason(true, MissionStage.Paused, false, false, ""));
            Assert.Equal("Claudia's next request waits at the Spending Cap", ScriptWarp.HoldReason(true, MissionStage.Running, true, false, ""));
            Assert.Equal("Claudia recommends Abort", ScriptWarp.HoldReason(true, MissionStage.Running, false, true, ""));
            Assert.Equal(ScriptWarp.LinkLost, ScriptWarp.HoldReason(true, MissionStage.Running, false, false, ScriptWarp.LinkLost));
            Assert.Equal("you have Manual Control", ScriptWarp.HoldReason(false, MissionStage.ManualControl, false, false, ""));
            Assert.Equal("", ScriptWarp.HoldReason(true, MissionStage.Running, false, false, ""));
            // Nothing runs, nothing to hold: a Pause between sequences is the closed-window alert's business.
            Assert.Equal("", ScriptWarp.HoldReason(false, MissionStage.Paused, true, true, ScriptWarp.StockAlarm));
        }

        // ---- Harness ----

        private static ScriptedResponse HoldPlan()
        {
            var json = "{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: hold, then deorbit.\",\"reply_intent\":null,\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Hold and deorbit\",\"intent\":\"Hold three orbits, then lower the periapsis.\",\"why\":\"The player asked for a hold.\","
                + "\"target\":\"Periapsis 30 km\",\"tolerances\":\"Pe within 5 km\",\"expected_delta_v\":60,\"expected_duration_s\":17000,"
                + "\"abort_conditions\":[],\"risks\":[],\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":" + JsonSerializer.Serialize(HoldScript) + ","
                + "\"due_time_ut\":null,\"window_closes_ut\":null,\"late_window_dv\":null},\"debrief\":null}";
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_w\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\n"
                + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\n"
                + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(json) + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Pad());
            return mission;
        }

        private void ToSequencePlan(MissionControl mission, decimal cap = 10m)
        {
            CraftCheckRouter.StartChecked(mission, _background, Pad(), new StartMission(Goal, cap, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private void ToRunning(MissionControl mission, decimal cap = 10m)
        {
            ToSequencePlan(mission, cap);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            mission.TakeCommands();
        }

        private static GameSnapshot Pad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static GameSnapshot Pad(Action<GameSnapshot> change)
        {
            var snapshot = Pad();
            change(snapshot);
            return snapshot;
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
    }
}
