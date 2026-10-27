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
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Auto Mode (spec #327, ticket #328, ADR 0009), driven through the Mission seam with hand-written streams: plans after
    /// the first Mission Plan are approved as they arrive, with "Approved by Auto Mode." after the card; the first Mission
    /// Plan, a manual stretch and a Due Time still wait; ticking it approves a waiting plan and unticking leaves a run alone;
    /// the room check approves only while the Spending Cap has room for the Debrief and next plan request that follow; and
    /// the switch is saved with the Mission and new Missions take <c>default_auto_mode</c>.
    /// </summary>
    public sealed class MissionAutoModeTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string CraftId = "c0ffee00c0ffee00c0ffee00c0ffee00";
        private const string Approved = "Approved by Auto Mode.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Approved on arrival ----

        [Fact]
        public void TheFirstMissionPlanWaitsAndTheSequencePlanAfterItIsApprovedOnArrivalWithTheLogLineAfterItsCard()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")), AutoModeOn());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            Assert.True(mission.View.AutoMode);
            // The first Mission Plan is the player's go-ahead, Auto Mode or not.
            mission.Send(new UtAdvanced(1300));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.DoesNotContain(mission.View.Log, e => e.Text == Approved);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            var log = mission.View.Log.ToList();
            var card = log.FindIndex(e => e.Kind == LogKind.SequencePlan);
            Assert.Equal(Approved, log[card + 1].Text);
            Assert.Equal(LogKind.Notice, log[card + 1].Kind);
            Assert.Single(log, e => e.Text == Approved);
        }

        [Fact]
        public void ARevisedMissionPlanAfterADebriefIsApprovedOnArrival()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefRevisedPlan(),
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, AutoModeOn());
            var first = ToRunning(mission);

            mission.Send(new KosRunEnded(Finished(), first.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Running && transport.Requests.Count == 5);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            var log = mission.View.Log.ToList();
            var revised = log.FindLastIndex(e => e.Kind == LogKind.MissionPlan);
            Assert.Equal(Approved, log[revised + 1].Text);
            Assert.Contains(log.Skip(revised), e => e.Text == "Mission Plan approved.");
            var next = log.FindLastIndex(e => e.Kind == LogKind.SequencePlan);
            Assert.True(next > revised);
            Assert.Equal(Approved, log[next + 1].Text);
        }

        // ---- What still waits for the player ----

        [Fact]
        public void AManualStretchWaitsUnderAutoMode()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan-manual.sse")), AutoModeOn());
            ToSequencePlan(mission);
            mission.Send(new UtAdvanced(1300));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.DoesNotContain(mission.View.Log, e => e.Text == Approved);
            Assert.True(mission.View.AutoMode);
        }

        [Fact]
        public void ADueTimeWaitsUnderAutoModeEvenOnceItIsReached()
        {
            var plan = WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), "\"due_time_ut\":1250,");
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), plan), AutoModeOn());
            ToSequencePlan(mission);
            Assert.NotNull(mission.View.DueTime);
            mission.Send(new UtAdvanced(1300));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.DoesNotContain(mission.View.Log, e => e.Text == Approved);
        }

        // ---- The switch ----

        [Fact]
        public void TickingAutoModeWithAPlanWaitingApprovesIt()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToSequencePlan(mission);
            Assert.False(mission.View.AutoMode);
            mission.TakeCommands();

            mission.Send(new SetAutoMode(true));
            Assert.True(mission.View.AutoMode);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            Assert.Single(mission.View.Log, e => e.Text == Approved);
        }

        [Fact]
        public void UntickingDuringARunLeavesTheRunAloneAndTheNextPlanWaits()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefGoalNotMet(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, AutoModeOn());
            var first = ToRunning(mission);

            mission.Send(new SetAutoMode(false));
            Assert.False(mission.View.AutoMode);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            mission.Send(new KosRunEnded(Finished(), first.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Single(mission.View.Log, e => e.Text == Approved);
        }

        [Fact]
        public void TheSwitchIsIgnoredWithNoMissionInPlay()
        {
            var mission = Control(new StreamingFakeTransport());
            mission.Send(new SetAutoMode(true));
            Assert.False(mission.View.AutoMode);
        }

        // ---- The room check ----

        [Fact]
        public void ExactlyEnoughCapForTheDebriefAndNextPlanApprovesAndACentLessLeavesThePlanWaiting()
        {
            // $0.90 is room for the plan requests, not for a Debrief and a next plan on top.
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToSequencePlan(mission, 0.90m);
            mission.TakeCommands();

            mission.Send(new SetAutoMode(true));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.True(mission.View.AutoMode);
            var blocked = CostLines(mission).Single(l => l.GetProperty("event").GetString() == "blocked");
            Assert.Equal("auto_mode_room", blocked.GetProperty("outcome").GetString());
            var needed = mission.View.Spent + mission.View.HeldCharges + blocked.GetProperty("amount").GetDecimal();
            Assert.True(needed > 0.90m, "the room check needs more than the cap: " + needed);

            mission.Send(new RaiseCap(needed - 0.01m));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            // Held once for the plan, however many inputs it waits through.
            Assert.Single(CostLines(mission), l => l.GetProperty("event").GetString() == "blocked");

            mission.Send(new RaiseCap(needed));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            Assert.Single(mission.View.Log, e => e.Text == Approved);
        }

        [Fact]
        public void InABlackoutBlackoutsRulesStandWithAutoModeOnAndNoRoomCheck()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")), AutoModeOn());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 0.90m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            mission.Send(new Approve());
            mission.Send(Snapshot(s => s.HasLink = false));
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            Assert.DoesNotContain(mission.View.Log, e => e.Text == Approved);
            Assert.DoesNotContain(CostLines(mission), l => l.GetProperty("event").GetString() == "blocked");
        }

        // ---- Saved, and the default ----

        [Fact]
        public void AutoModeIsSavedWithTheMissionAndRestored()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan-manual.sse")));
            ToSequencePlan(mission);
            mission.Send(new SetAutoMode(true));
            var state = Assert.Single(mission.ExportStates());
            Assert.True(state.AutoMode);
            Assert.Equal("true", state.ToValues()["auto_mode"]);
            var again = MissionSaveState.FromValues(state.ToValues());
            Assert.True(again.AutoMode);

            var later = Control(new StreamingFakeTransport());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { again }));
            Assert.True(later.View.AutoMode);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, later.View.Stage);

            // A save from before Auto Mode reads as off.
            var values = state.ToValues();
            values.Remove("auto_mode");
            Assert.False(MissionSaveState.FromValues(values).AutoMode);
        }

        [Fact]
        public void ANewMissionTakesTheDefaultFromThePlayerConfig()
        {
            var off = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse")));
            CraftCheckRouter.StartChecked(off, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(off, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.False(off.View.AutoMode);

            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.DefaultAutoMode = true;
                config.SaveDefaults(dir.Sub);
                var on = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse")), PlayerConfig.LoadOrCreate(dir.Sub));
                CraftCheckRouter.StartChecked(on, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
                DrainUntil(on, v => v.Stage == MissionStage.AwaitingPlanApproval);
                Assert.True(on.View.AutoMode);
            }
        }

        // ---- Telling Claudia ----

        [Fact]
        public void TheAutoModeNoticeIsInTheRequestWhileOnAndAbsentWhenOffAndFlippingItLeavesTheSystemLayersAlone()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport, AutoModeOn());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            var withNotice = transport.Requests.Last().Body;
            Assert.Contains(MissionPrompts.AutoModeNotice, ThisTurnsNotice(withNotice));

            mission.Send(new SetAutoMode(false));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval || v.Stage == MissionStage.Running);
            var without = transport.Requests.Last().Body;
            Assert.DoesNotContain("Auto Mode is on", ThisTurnsNotice(without));
            Assert.Equal(SystemLayers(withNotice), SystemLayers(without));
            Assert.DoesNotContain("Auto Mode is on", SystemLayers(without));
        }

        /// <summary>The turn's notice is the last message of its request; earlier turns' notices stay in the history unchanged.</summary>
        private static string ThisTurnsNotice(string body)
        {
            using (var doc = JsonDocument.Parse(body))
            {
                var last = doc.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content");
                return last.ValueKind == JsonValueKind.String ? last.GetString() : string.Concat(last.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
            }
        }

        private static string SystemLayers(string body)
        {
            using (var doc = JsonDocument.Parse(body))
                return doc.RootElement.GetProperty("system").GetRawText();
        }

        // ---- Helpers ----

        private static PlayerConfig AutoModeOn()
        {
            return new PlayerConfig { DefaultAutoMode = true };
        }

        private List<JsonElement> CostLines(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        }

        /// <summary>The on-pad Game Snapshot, changed.</summary>
        private static GameSnapshot Snapshot(Action<GameSnapshot> change)
        {
            var snapshot = OnPad();
            change(snapshot);
            return snapshot;
        }

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            return Edited(response, "\\\"script\\\":", fields.Replace("\"", "\\\"") + "\\\"script\\\":");
        }

        /// <summary>A stream with one piece of its text replaced.</summary>
        private static ScriptedResponse Edited(ScriptedResponse response, string from, string to)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace(from, to);
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>The Debrief fixture, Nominal but with the Mission Goal not met yet, so the loop goes on.</summary>
        private static ScriptedResponse DebriefGoalNotMet()
        {
            return Edited(ScriptedResponse.Fixture("mission-debrief.sse"), "\\\"goal_reached\\\":true", "\\\"goal_reached\\\":false");
        }

        /// <summary>The Debrief fixture made Off-Nominal, with a revised Mission Plan to follow.</summary>
        private static ScriptedResponse DebriefRevisedPlan()
        {
            return Edited(Edited(ScriptedResponse.Fixture("mission-debrief.sse"), "\\\"goal_reached\\\":true", "\\\"goal_reached\\\":false,\\\"follow_up\\\":\\\"revised_plan\\\""),
                "\\\"outcome\\\":\\\"Nominal\\\"", "\\\"outcome\\\":\\\"Off-Nominal\\\"");
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private void ToSequencePlan(MissionControl mission, decimal cap = 10m)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval || v.Stage == MissionStage.Running);
        }

        private StartScript ToRunning(MissionControl mission, decimal cap = 10m)
        {
            ToSequencePlan(mission, cap);
            if (mission.View.Stage != MissionStage.Running) mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        /// <summary>The on-pad Game Snapshot, of a craft with a vessel id (the fixture has none).</summary>
        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = CraftId;
            return snapshot;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
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
