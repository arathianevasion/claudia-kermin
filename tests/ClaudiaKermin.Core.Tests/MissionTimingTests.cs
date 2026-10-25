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
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Mission timing (issue #63): Due Times between sequences, a missed Due Time, late Approval and the
    /// Mission Settings. Driven through the Mission seam with hand-written replies; the clock is whatever
    /// the test sends in <see cref="UtAdvanced"/>.
    /// </summary>
    public sealed class MissionTimingTests : IDisposable
    {
        private const string Goal = "Land on the Mun and come home.";
        private const double Now = 1203.48;
        private const double Due = 100000;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Hand-written replies ----

        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\n"
                + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\n"
                + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        private static string D(double? value)
        {
            return value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        }

        /// <summary>A Mission Plan: launch right away, then a Mun transfer after a long wait (Due Time).</summary>
        private static ScriptedResponse TimedMissionPlan(double? transferDue = Due, double? missionTime = 250000)
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: launch, then a Mun transfer at its window.\",\"reply_intent\":null,"
                + "\"mission_plan\":{\"why\":\"The transfer window is a day away.\",\"sequences\":["
                + "{\"name\":\"Launch\",\"intent\":\"Reach a 100 km orbit.\",\"delta_v\":3400,\"timing\":\"right away\",\"due_time_ut\":null},"
                + "{\"name\":\"Mun transfer\",\"intent\":\"Burn for the Mun.\",\"delta_v\":900,\"timing\":\"at the window\",\"due_time_ut\":" + D(transferDue) + "}],"
                + "\"total_delta_v\":4300,\"expected_duration\":\"about a day\",\"mission_time_s\":" + D(missionTime) + "},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse SequencePlan(double? due, double? windowCloses = null, double? lateDv = null, double expectedDv = 100, string name = "Mun transfer")
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: " + name + ".\",\"reply_intent\":null,\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"" + name + "\",\"intent\":\"Burn for the Mun.\",\"why\":\"The window is open then.\",\"target\":\"Mun encounter\","
                + "\"tolerances\":\"Pe within 20 km\",\"expected_delta_v\":" + D(expectedDv) + ",\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\","
                + "\"due_time_ut\":" + D(due) + ",\"window_closes_ut\":" + D(windowCloses) + ",\"late_window_dv\":" + D(lateDv) + "},\"debrief\":null}");
        }

        // ---- Harness ----

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        private static GameSnapshot OnPad(string vesselId = "")
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = vesselId;
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

        private void StartMission(MissionControl mission, MissionSettingsValues settings = null, string vesselId = "")
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad(vesselId));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(vesselId), new StartMission(Goal, 10m, "claude-sonnet-5-5", settings));
        }

        /// <summary>Through the Mission Plan to a Sequence Plan awaiting Approval.</summary>
        private void ToSequencePlan(MissionControl mission, string vesselId = "")
        {
            StartMission(mission, null, vesselId);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private static T[] Of<T>(IList<AdapterCommand> commands) where T : AdapterCommand
        {
            return commands.OfType<T>().ToArray();
        }

        private static string[] SystemLayers(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString()).ToArray();
        }

        private static string Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return string.Join("\n", doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()));
        }

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        // ---- Due Times in the plans ----

        [Fact]
        public void ALongWaitIsTheNextSequencesDueTimeShownInTheMissionPlanWithTotalMissionTime()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan());
            var mission = Control(transport);
            StartMission(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var card = mission.View.Log.Single(e => e.Kind == LogKind.MissionPlan);
            Assert.Contains("Mun transfer", card.Text);
            Assert.Contains("Due Time " + KerbinTime.Date(Due), card.Text);
            Assert.Contains("Total Mission time " + KerbinTime.Span(250000), card.Text);
            // The first sequence goes right after the launch: no Due Time on its line.
            Assert.DoesNotContain("Launch: Reach a 100 km orbit. (3,400 m/s, right away; Due", card.Text);
        }

        [Fact]
        public void TheSequencePlanShowsItsDueTimeSetsAnAlarmAndOffersWarpToDueTimeInsteadOfApprove()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission);

            var card = mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan);
            Assert.StartsWith("Due Time: " + KerbinTime.Date(Due), card.Text);
            var alarm = Assert.Single(Of<SetDueTimeAlarm>(mission.TakeCommands()));
            Assert.Equal(Due, alarm.Ut);
            Assert.Equal(mission.View.MissionId, alarm.MissionId);
            Assert.True(mission.View.DueTime.HasDueTime);
            Assert.False(mission.View.DueTime.Reached);
            Assert.Equal(Due - Now, mission.View.DueTime.SecondsLeft, 1);
            Assert.Equal(new[] { QuickReply.WarpToDueTime, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);
        }

        [Fact]
        public void ApprovalBeforeTheDueTimeStartsNothingAndTheModNeverWarpsOnItsOwn()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new Approve());
            mission.Send(new UtAdvanced(Due - 5000));

            var commands = mission.TakeCommands();
            Assert.Empty(Of<StartScript>(commands));
            Assert.Empty(Of<WarpToUt>(commands));
            Assert.Empty(Of<DropWarp>(commands));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Not before the Due Time"));
        }

        [Fact]
        public void ApprovalRefusedSecondsEarlySaysSecondsNotZeroMinutes()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Due - 5));
            mission.Send(new Approve());

            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("(in 5s)"));
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("(in 0m)"));
        }

        [Fact]
        public void WarpToDueTimeAsksTheAdapterToWarpThereAndAtTheDueTimeWarpDropsAndTheSequenceCanStart()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new WarpToDueTime());
            Assert.Equal(Due, Assert.Single(Of<WarpToUt>(mission.TakeCommands())).Ut);

            // Time passes in the game (UtAdvanced is the only clock): nothing happens until the Due Time.
            mission.Send(new UtAdvanced(Due - 1));
            Assert.Empty(Of<DropWarp>(mission.TakeCommands()));

            mission.Send(new UtAdvanced(Due));
            var commands = mission.TakeCommands();
            Assert.Single(Of<DropWarp>(commands));
            Assert.Contains("Due Time reached", Assert.Single(Of<PostScreenMessage>(commands)).Text);
            Assert.True(mission.View.DueTime.Reached);
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);

            // It drops once, not on every tick.
            mission.Send(new UtAdvanced(Due + 1));
            Assert.Empty(Of<DropWarp>(mission.TakeCommands()));

            mission.Send(new Approve());
            var after = mission.TakeCommands();
            Assert.Single(Of<StartScript>(after));
            Assert.Single(Of<ClearDueTimeAlarm>(after));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
        }

        [Fact]
        public void AtTheDueTimeAwayFromTheVesselGoToVesselIsOffered()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission, "abc123");
            mission.TakeCommands();

            // The player flew elsewhere (or left to the Tracking Station) and the Due Time came.
            mission.Send(new SceneChanged(GameScene.Other));
            mission.Send(new UtAdvanced(Due));

            Assert.Single(Of<DropWarp>(mission.TakeCommands()));
            Assert.Equal(new[] { QuickReply.GoToVessel, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);
            Assert.Equal(OnPad().Craft.Name, mission.View.DueTime.VesselName);

            mission.Send(new GoToVessel());
            Assert.Equal("abc123", Assert.Single(Of<SwitchToVessel>(mission.TakeCommands())).VesselId);
        }

        [Fact]
        public void GoToVesselIsNotOfferedBeforeTheDueTimeOrWhenAlreadyInTheVessel()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission, "abc123");
            mission.TakeCommands();

            mission.Send(new SceneChanged(GameScene.Other));
            mission.Send(new UtAdvanced(Due - 10));
            Assert.DoesNotContain(QuickReply.GoToVessel, mission.View.QuickReplies);

            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad("abc123"));
            mission.Send(new UtAdvanced(Due));
            Assert.DoesNotContain(QuickReply.GoToVessel, mission.View.QuickReplies);
            mission.Send(new GoToVessel());
            Assert.Empty(Of<SwitchToVessel>(mission.TakeCommands()));
        }

        // ---- A missed Due Time ----

        [Fact]
        public void AMissedDueTimeWaitsForTheSequenceAndCostsNothing()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();
            var logBefore = CostLog(mission);

            // The player was elsewhere and the Due Time slipped by 3 Kerbin days.
            mission.Send(new UtAdvanced(Due + 3 * 21600));

            var commands = mission.TakeCommands();
            Assert.Single(Of<DropWarp>(commands));
            Assert.Contains("Due Time missed by 3d", Assert.Single(Of<PostScreenMessage>(commands)).Text);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.False(mission.View.Busy);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(logBefore, CostLog(mission));

            // No window to miss: the sequence simply starts when the player approves.
            mission.Send(new Approve());
            Assert.Single(Of<StartScript>(mission.TakeCommands()));
        }

        [Fact]
        public void IfTheTimingNoLongerWorksClaudiaRevisesTheSequencePlan()
        {
            // Missed by days, so the planned window closed; the next one costs far more than the plan.
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due, Due + 3600, 400), SequencePlan(Due + 90000, null, null, 100, "Mun transfer, next window"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Due + 40000));
            mission.TakeCommands();
            mission.Send(new Approve());

            Assert.Empty(Of<StartScript>(mission.TakeCommands()));
            Assert.True(mission.View.Busy);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Late Approval:") && e.Text.Contains("re-planning"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && v.DueTime.Ut == Due + 90000);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("after its window closed", Messages(transport.Requests[2]));
            Assert.Contains("400 m/s against 100 m/s planned", Messages(transport.Requests[2]));
            Assert.Equal(new[] { QuickReply.WarpToDueTime, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);
            // The re-plan is a real, paid request: recorded as pending before it was sent.
            Assert.Equal(3, CostLog(mission).Split('\n').Count(l => l.Contains("\"pending\"")));
        }

        // ---- Late Approval ----

        [Fact]
        public void LateApprovalTakesTheNextWindowWhenItsDeltaVIsWithinToleranceWithAOneLineNotice()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due, Due + 3600, 105));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Due + 40000));
            mission.TakeCommands();
            mission.Send(new Approve());

            Assert.Single(Of<StartScript>(mission.TakeCommands()));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var notice = mission.View.Log.Single(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Late Approval:"));
            Assert.DoesNotContain("\n", notice.Text);
            Assert.Contains("takes the next window", notice.Text);
            Assert.Contains("105 m/s against 100 m/s planned", notice.Text);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void LateApprovalOutsideToleranceReplansAndNoScriptStarts()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due, Due + 3600, 130), SequencePlan(null, null, null, 130));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Due + 40000));
            mission.TakeCommands();
            mission.Send(new Approve());

            Assert.Empty(Of<StartScript>(mission.TakeCommands()));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("130 m/s against 100 m/s planned", Messages(transport.Requests[2]));
        }

        [Fact]
        public void TheToleranceIsThePlayersConfig()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due, Due + 3600, 130));
            var mission = Control(transport, new PlayerConfig { LateApprovalToleranceDvPercent = 50 });
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Due + 40000));
            mission.TakeCommands();
            mission.Send(new Approve());

            Assert.Single(Of<StartScript>(mission.TakeCommands()));
        }

        [Fact]
        public void ApprovalInsideTheWindowIsNotLate()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due, Due + 3600, 999));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Due + 60));
            mission.TakeCommands();
            mission.Send(new Approve());

            Assert.Single(Of<StartScript>(mission.TakeCommands()));
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Late Approval"));
        }

        [Fact]
        public void LateApprovalAfterAReplyOnTheWayToApprovalIsStillChecked()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(null, Now + 100, 130), SequencePlan(null, null, null, 130));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new UtAdvanced(Now + 5000));
            mission.Send(new ConfirmApproval(true));
            mission.Send(new Approve());

            Assert.Empty(Of<StartScript>(mission.TakeCommands()));
            Assert.True(mission.View.Busy);
        }

        // ---- Mission Settings ----

        [Fact]
        public void SettingsAreChosenAtStartPersistAndReachClaudiaAsAPromptLayer()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan());
            var mission = Control(transport);
            StartMission(mission, new MissionSettingsValues { DaylightLanding = false, TimeVersusDeltaV = TimeVersusDeltaV.MostEfficient });
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.False(mission.View.Settings.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.MostEfficient, mission.View.Settings.TimeVersusDeltaV);
            var layers = SystemLayers(transport.Requests[0]);
            Assert.Equal(3, layers.Length);
            Assert.Contains("Daylight landing: off", layers[2]);
            Assert.Contains("Most efficient: wait for the cheapest window", layers[2]);
            Assert.Contains("due_time_ut", layers[2]);
            // ADR 0004: waits warp inside the script, and a Due Time is only for a wait between sequences that needs the player.
            Assert.DoesNotContain("capped", layers[2]);
            Assert.Contains("inside the Mission Script, however long", layers[2]);
            Assert.Contains("warp on rails", layers[2]);
            Assert.Contains("A Due Time is only for a wait between sequences that needs the player", layers[2]);
            Assert.Contains("Count every wait in expected_duration_s", layers[2]);

            var state = Assert.Single(mission.ExportStates());
            Assert.Equal("false", state.Settings[MissionSettingsValues.DaylightKey]);
            Assert.Equal("MostEfficient", state.Settings[MissionSettingsValues.TimeKey]);
            var reread = MissionSaveState.FromValues(state.ToValues());
            Assert.Equal("MostEfficient", reread.Settings[MissionSettingsValues.TimeKey]);
        }

        [Fact]
        public void SettingsDefaultFromThePlayersConfigWhenTheFormSendsNone()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan());
            var mission = Control(transport, new PlayerConfig { DefaultDaylightLanding = false, DefaultTimeVersusDeltaV = TimeVersusDeltaV.Fastest });
            Assert.Equal(TimeVersusDeltaV.Fastest, mission.View.StartForm.DefaultSettings.TimeVersusDeltaV);
            StartMission(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.False(mission.View.Settings.DaylightLanding);
            Assert.Equal(TimeVersusDeltaV.Fastest, mission.View.Settings.TimeVersusDeltaV);
            Assert.Contains("Fastest: never wait", SystemLayers(transport.Requests[0])[2]);
        }

        [Fact]
        public void EditingSettingsAsksForARevisedMissionPlanThroughTheSpendingCapPath()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), TimedMissionPlan(null, 20000));
            var mission = Control(transport);
            StartMission(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.True(mission.View.CanEditSettings);

            mission.Send(new EditSettings(new MissionSettingsValues { DaylightLanding = true, TimeVersusDeltaV = TimeVersusDeltaV.Fastest }));
            Assert.True(mission.View.Busy);
            Assert.False(mission.View.CanEditSettings);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);

            Assert.Equal(2, transport.Requests.Count);
            var asked = Messages(transport.Requests[1]);
            Assert.Contains("changed the Mission Settings", asked);
            Assert.Contains("Fastest: never wait", asked);
            Assert.Contains("Revise the Mission Plan", asked);
            // The system layers did not change, so the cached prefix stays intact.
            Assert.Equal(SystemLayers(transport.Requests[0]), SystemLayers(transport.Requests[1]));
            Assert.Equal(TimeVersusDeltaV.Fastest, mission.View.Settings.TimeVersusDeltaV);
            Assert.Equal(2, mission.View.Log.Count(e => e.Kind == LogKind.MissionPlan));
            // It needs Approval again, and was recorded as a pending charge like any request.
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.Equal(2, CostLog(mission).Split('\n').Count(l => l.Contains("\"pending\"")));
            Assert.Equal("Fastest", Assert.Single(mission.ExportStates()).Settings[MissionSettingsValues.TimeKey]);
        }

        [Fact]
        public void EditingSettingsWhileASequencePlanAwaitsApprovalDropsItAndReplans()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due), TimedMissionPlan(null, 20000), SequencePlan(null));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();
            Assert.Equal(0, mission.View.CurrentSequence);

            mission.Send(new EditSettings(new MissionSettingsValues { DaylightLanding = false, TimeVersusDeltaV = TimeVersusDeltaV.Balanced }));

            Assert.Single(Of<ClearDueTimeAlarm>(mission.TakeCommands()));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.False(mission.View.DueTime.HasDueTime);
            Assert.Contains("Daylight landing: off", Messages(transport.Requests[2]));
            // The Mission Plan is approved again, then the Sequence Plan is asked for afresh, and counted once.
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(0, mission.View.CurrentSequence);
            Assert.False(mission.View.DueTime.HasDueTime);
        }

        [Fact]
        public void EditingToTheSameSettingsOrWhileRunningOrBusyDoesNothing()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(null));
            var mission = Control(transport);
            StartMission(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            mission.Send(new EditSettings(MissionSettingsValues.Defaults(new PlayerConfig())));
            Assert.False(mission.View.Busy);
            Assert.Single(transport.Requests);

            mission.Send(new Approve());
            Assert.True(mission.View.Busy);
            mission.Send(new EditSettings(new MissionSettingsValues { DaylightLanding = false }));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            mission.Send(new EditSettings(new MissionSettingsValues { DaylightLanding = false }));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.True(mission.View.Settings.DaylightLanding);
        }

        [Fact]
        public void EditingSettingsPausesAtTheSpendingCapLikeAnyOtherRequest()
        {
            // The first plan costs $0.54, so a second request's worst case no longer fits a $0.65 cap.
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse"), TimedMissionPlan());
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 0.65m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            mission.Send(new EditSettings(new MissionSettingsValues { DaylightLanding = false }));

            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Single(transport.Requests);
            Assert.Contains(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains("\"blocked\"", CostLog(mission));

            // Raising the cap sends the revision that was waiting.
            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("changed the Mission Settings", Messages(transport.Requests[1]));
        }

        [Fact]
        public void SettingsEditedBeforeFlightGoIntoTheFirstPlanRequest()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan());
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(OnPad());
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);

            mission.Send(new EditSettings(new MissionSettingsValues { DaylightLanding = false, TimeVersusDeltaV = TimeVersusDeltaV.Fastest }));
            Assert.Empty(transport.Requests);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Contains("Daylight landing: off", Messages(transport.Requests[0]));
            Assert.Contains("Fastest: never wait", Messages(transport.Requests[0]));
        }

        // ---- Persistence ----

        [Fact]
        public void DueTimesAndTheVesselSurviveSaveAndLoad()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due, Due + 3600, 105));
            var mission = Control(transport);
            ToSequencePlan(mission, "abc123");
            mission.Send(new UtAdvanced(Due));
            mission.TakeCommands();

            var state = Assert.Single(mission.ExportStates());
            var reread = MissionSaveState.FromValues(state.ToValues());

            Assert.Equal(Due, reread.SequencePlan.DueTimeUt);
            Assert.Equal(Due + 3600, reread.SequencePlan.WindowClosesUt);
            Assert.Equal(105, reread.SequencePlan.LateWindowDeltaV);
            Assert.Equal(Due, reread.MissionPlan.Sequences[1].DueTimeUt);
            Assert.Null(reread.MissionPlan.Sequences[0].DueTimeUt);
            Assert.Equal(250000, reread.MissionPlan.MissionTimeSeconds);
            Assert.Equal("abc123", reread.VesselId);
            Assert.True(reread.DueNotified);

            // The loaded Mission shows the same Due Time, and does not announce it a second time.
            var reload = Control(new StreamingFakeTransport());
            reload.Send(new SceneChanged(GameScene.Flight));
            reload.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { reread }));
            reload.Send(OnPad("abc123"));
            reload.TakeCommands();
            reload.Send(new UtAdvanced(Due + 10));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, reload.View.Stage);
            Assert.True(reload.View.DueTime.Reached);
            Assert.Empty(Of<DropWarp>(reload.TakeCommands()));
        }

        [Fact]
        public void AReplyWithoutTheNewTimingFieldsStillParses()
        {
            // Earlier replies and saves have none of the Due Time fields.
            var plans = "{\"kind\":\"sequence_plan\",\"say\":\"x\",\"reply_intent\":null,\"mission_plan\":null,\"sequence_plan\":{\"name\":\"n\",\"intent\":\"i\",\"why\":\"w\","
                + "\"target\":\"t\",\"tolerances\":\"t\",\"expected_delta_v\":1,\"expected_duration_s\":2,\"abort_conditions\":[],\"risks\":[],\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"s\"},\"debrief\":null}";
            var state = MissionSaveState.FromValues(new Dictionary<string, string> { { "id", "m1" }, { "plans", plans.Replace("\\", "\\\\").Replace("/", "\\s").Replace("{", "\\o").Replace("}", "\\c") } });
            Assert.Null(state.SequencePlan.DueTimeUt);
            Assert.Null(state.SequencePlan.WindowClosesUt);
            Assert.Null(state.SequencePlan.LateWindowDeltaV);
            Assert.Equal("n", state.SequencePlan.Name);
        }

        [Fact]
        public void TheDueTimeReadsFromNowWithTheKerbinDateForHover()
        {
            var transport = new StreamingFakeTransport(TimedMissionPlan(), SequencePlan(Due));
            var mission = Control(transport);
            ToSequencePlan(mission);

            Assert.Equal(KerbinTime.FromNow(Due, Now), mission.View.DueTime.Text);
            Assert.Equal(KerbinTime.Date(Due), mission.View.DueTime.Date);
        }
    }
}
