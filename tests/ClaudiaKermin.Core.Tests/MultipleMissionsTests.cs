// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Several Missions in one save (one active, the others Paused at a sequence boundary, finished ones read-only history), the
    /// Missions list the KSC and Tracking Station show, and the Mission log built again from the transcript after a load. Same
    /// seam as <see cref="MissionPersistenceTests"/>: a save is <see cref="MissionControl.ExportStates"/>, a load is
    /// <see cref="MissionsRestored"/>, a quit is a second <see cref="MissionControl"/> over the same save directory.
    /// </summary>
    public sealed class MultipleMissionsTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string OtherGoal = "Fly the second craft to the Mun.";
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

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f)).ToArray());
        }

        /// <summary>The craft on the pad. A second craft has no Claudia aboard (she is in the first), so its Mission is remote and the first Mission's craft stays put.</summary>
        private static GameSnapshot OnPad(string vesselId = "", bool second = false)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = vesselId;
            if (second) snapshot.Crew.Clear();
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

        /// <summary>Start a Mission in Flight (the Craft Check passes first) and get to its Mission Plan awaiting Approval.</summary>
        private void ToMissionPlan(MissionControl mission, string goal = Goal, string vesselId = "", bool second = false)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(vesselId, second), new StartMission(goal, 10m, "claude-sonnet-5-5", remote: second));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        private void ToSequencePlan(MissionControl mission)
        {
            ToMissionPlan(mission);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private StartScript ToRunning(MissionControl mission)
        {
            ToSequencePlan(mission);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return Assert.Single(mission.TakeCommands().OfType<StartScript>());
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private string FolderOf(string id)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", id);
        }

        private static MissionListEntry Entry(MissionControl mission, string id)
        {
            return Assert.Single(mission.View.Missions, m => m.Id == id);
        }

        /// <summary>What the player reads in the log: everything but the mod's own notices, which are not in the transcript.</summary>
        private static List<string> Read(IEnumerable<LogEntry> log)
        {
            return log.Where(e => e.Kind != LogKind.Notice).Select(e => e.Kind + "|" + e.Title + "|" + e.Text + "|" + e.Why + "|" + e.Outcome + "|" + e.Script).ToList();
        }

        // ---- The Mission log after a load (#95) ----

        [Fact]
        public void AfterALoadTheLogShowsTheSameChatLinesCardsAndDebriefsAsBefore()
        {
            var transport = Replies("mission-plan.sse", "mission-answer.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            mission.Send(new AskQuestion("How long is the burn?"));
            DrainUntil(mission, v => !v.Busy && v.Log.Any(e => e.Text.Contains("About 5 seconds")));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var run = Assert.Single(mission.TakeCommands().OfType<StartScript>());
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var before = Read(mission.View.Log);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text == "How long is the burn?");
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.SequencePlan);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Debrief);
            var saved = mission.ExportStates();

            // A quickload in the same game.
            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(before, Read(mission.View.Log));

            // A quit and a fresh game over the same save.
            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.Succeeded, later.View.Stage);
            Assert.Equal(before, Read(later.View.Log));
            Assert.Equal("Debrief: Nominal", later.View.Log.Last(e => e.Kind == LogKind.Debrief).Title);
        }

        /// <summary>The log with the approval and script notices kept in place, as the player reads it.</summary>
        private static List<string> WithModNotices(IEnumerable<LogEntry> log)
        {
            return log.Where(e => e.Kind != LogKind.Notice || e.Text.Contains("approved") || e.Text.StartsWith("The script ended"))
                .Select(e => e.Kind + "|" + e.Title + "|" + e.Text).ToList();
        }

        [Fact]
        public void AfterALoadTheModsApprovalAndScriptNoticesAreBackInOrderAmongTheTranscriptLines()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse"));
            var run = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var before = WithModNotices(mission.View.Log);
            Assert.Contains(before, l => l == "Notice||Mission Plan approved.");
            Assert.Contains(before, l => l == "Notice||Sequence Plan approved: the mod is starting the script.");
            Assert.Contains(before, l => l.StartsWith("Notice||The script ended"));
            var saved = mission.ExportStates();

            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(before, WithModNotices(mission.View.Log));

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(before, WithModNotices(later.View.Log));
            var order = later.View.Log.Select(e => e.Kind == LogKind.Notice ? e.Text : e.Kind.ToString()).ToList();
            Assert.True(order.IndexOf("Mission Plan approved.") < order.IndexOf(LogKind.SequencePlan.ToString()));
            Assert.True(order.IndexOf("Sequence Plan approved: the mod is starting the script.") > order.IndexOf(LogKind.SequencePlan.ToString()));
            Assert.True(order.FindIndex(t => t.StartsWith("The script ended")) < order.IndexOf(LogKind.Debrief.ToString()));
        }

        [Fact]
        public void AFolderWithNoNoticesInItsEventLogStillRebuildsTheLog()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            ToSequencePlan(mission);
            var saved = mission.ExportStates();
            var events = Path.Combine(FolderOf(saved.Single().Id), "events.jsonl");
            File.WriteAllLines(events, File.ReadAllLines(events).Where(l => !l.Contains("\"notice\"")));

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, saved));
            Assert.Single(later.View.Log, e => e.Kind == LogKind.SequencePlan);
            Assert.DoesNotContain(later.View.Log, e => e.Text == "Mission Plan approved.");
        }

        [Fact]
        public void APlanAwaitingApprovalIsInTheRebuiltLogOnce()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            var before = Read(mission.View.Log);
            var saved = mission.ExportStates();

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, later.View.Stage);
            Assert.Single(later.View.Log, e => e.Kind == LogKind.MissionPlan);
            var card = Assert.Single(later.View.Log, e => e.Kind == LogKind.SequencePlan);
            Assert.StartsWith("print \"Claudia: link check.\".\n", card.Script);
            Assert.Equal(before, Read(later.View.Log));
            // The card still offers Approval after the load.
            Assert.Contains(QuickReply.Approve, later.View.QuickReplies);
        }

        [Fact]
        public void AChangeTheCraftsPlayerAskedForIsInTheRebuiltLogToo()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            mission.Send(new ChangeSomething("Make it quicker."));
            DrainUntil(mission, v => !v.Busy && v.Log.Count(e => e.Kind == LogKind.MissionPlan) == 2);
            var before = Read(mission.View.Log);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text == "Change: Make it quicker.");

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, mission.ExportStates()));
            Assert.Equal(before, Read(later.View.Log));
        }

        /// <summary>A UT well past the test's game time: a Due Time still to come.</summary>
        private const double Due = 9000000;

        /// <summary>A reply as the stream carries it: the JSON as one text block.</summary>
        private static ScriptedResponse Reply(string json)
        {
            var sse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(json) + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = System.Text.Encoding.UTF8.GetBytes(sse) };
        }

        /// <summary>A Mission Plan with a manual stretch, a Due Time on its last sequence and a Total Mission time.</summary>
        private static ScriptedResponse TimedManualPlan()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: a link check, a hop by hand, then a burn at its window.\",\"reply_intent\":null,"
                + "\"sequence_plan\":null,\"debrief\":null,\"alert\":null,\"mission_plan\":{\"why\":\"Three legs.\",\"sequences\":["
                + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"right away\",\"due_time_ut\":null,\"manual\":false},"
                + "{\"name\":\"Hop\",\"intent\":\"You hop the craft.\",\"delta_v\":50,\"timing\":\"after the link check\",\"due_time_ut\":null,\"manual\":true},"
                + "{\"name\":\"Burn\",\"intent\":\"Burn at the window.\",\"delta_v\":900,\"timing\":\"at the window\",\"due_time_ut\":" + Due + ",\"manual\":false}],"
                + "\"total_delta_v\":950,\"expected_duration\":\"about a week\",\"mission_time_s\":600000}}");
        }

        private static ScriptedResponse TimedSequencePlan()
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: link check.\",\"reply_intent\":null,\"mission_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"why\":\"First things first.\",\"target\":\"A report\","
                + "\"tolerances\":\"none\",\"expected_delta_v\":0,\"expected_duration_s\":10,\"abort_conditions\":[\"no link\"],\"telemetry_checks\":[],\"risks\":[\"none\"],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\","
                + "\"due_time_ut\":" + Due + ",\"window_closes_ut\":null,\"late_window_dv\":null,\"manual\":false}}");
        }

        private static ScriptedResponse FullDebrief()
        {
            return Reply("{\"kind\":\"debrief\",\"say\":\"Debrief.\",\"reply_intent\":null,\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Nominal\",\"why\":\"It went.\",\"what_happened\":\"The link held.\",\"goal_reached\":true,\"next\":\"\",\"eva_science\":\"\","
                + "\"outcome_vs_target\":\"Report received against a report planned.\",\"plan_margin\":\"950 m/s left against 950 m/s to fly.\","
                + "\"science\":\"None.\",\"goal_checklist\":[{\"item\":\"Prove the link\",\"done\":true},{\"item\":\"Hop\",\"done\":false}],"
                + "\"follow_up\":\"none\",\"follow_up_text\":\"\"}}");
        }

        private static string Detail(DebriefDetail d)
        {
            return d == null ? "(none)" : string.Join("|", d.Rating, d.OutcomeVsTarget, d.PlanMargin, d.Science, d.FollowUp,
                string.Join(";", d.Checklist.Select(i => i.Item + "=" + i.Done)));
        }

        [Fact]
        public void APlanWithADueTimeAndAManualStretchAndAFullDebriefReadTheSameAfterALoad()
        {
            var transport = new StreamingFakeTransport(TimedManualPlan(), TimedSequencePlan(), FullDebrief());
            var mission = Control(transport);
            ToMissionPlan(mission);
            var plan = mission.View.Log.Single(e => e.Kind == LogKind.MissionPlan).Text;
            Assert.Contains("Hop [you fly this by hand]", plan);
            Assert.Contains("; Due Time " + Ui.KerbinTime.Date(Due), plan);
            Assert.Contains("Total Mission time " + Ui.KerbinTime.Span(600000), plan);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            // A load while the Sequence Plan with a Due Time waits for Approval: the same cards, the Due Time still "in" so long.
            var waiting = Read(mission.View.Log);
            var later = Control(Replies());
            later.Send(new UtAdvanced(mission.View.Ut));
            later.Send(new MissionsRestored(_save.Path, mission.ExportStates()));
            Assert.Equal(waiting, Read(later.View.Log));

            mission.Send(new UtAdvanced(Due + 1));
            mission.Send(new Approve());
            var run = Assert.Single(mission.TakeCommands().OfType<StartScript>());
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var before = Read(mission.View.Log);
            var detail = Detail(mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief);
            Assert.Contains("Report received against a report planned.", detail);

            var loaded = Control(Replies());
            loaded.Send(new MissionsRestored(_save.Path, mission.ExportStates()));
            Assert.Equal(before, Read(loaded.View.Log));
            // The Debrief keeps the detail Claudia gave (#56); what the mod saw during the run (events, margin bar) isn't in the transcript.
            Assert.Equal(detail, Detail(loaded.View.Log.Last(e => e.Kind == LogKind.Debrief).Debrief));
        }

        // ---- Starting or resuming pauses the active Mission at a sequence boundary ----

        [Fact]
        public void StartingAnotherMissionPausesTheActiveOneWhereItWaitsAndKeepsItInTheSave()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission, vesselId: "vessel-one");
            var first = mission.View.MissionId;

            ToMissionPlan(mission, OtherGoal, "vessel-two", second: true);
            var second = mission.View.MissionId;

            Assert.NotEqual(first, second);
            Assert.Equal(OtherGoal, mission.View.Goal);
            var states = mission.ExportStates();
            Assert.Equal(new[] { second, first }, states.Select(s => s.Id).ToArray());
            Assert.True(states[0].Active);
            Assert.False(states[1].Active);
            Assert.Equal(MissionStage.Paused, states[1].Stage);
            Assert.Equal(MissionStage.AwaitingPlanApproval, states[1].StageBeforePause);
            Assert.Equal("vessel-one", states[1].VesselId);
            Assert.True(File.Exists(Path.Combine(FolderOf(first), "transcript.json")));

            var paused = Entry(mission, first);
            Assert.Equal(MissionStatus.Paused, paused.Status);
            Assert.True(paused.CanResume);
            Assert.False(paused.Current);
            Assert.True(paused.Cost > 0m);
            Assert.Equal(Goal, paused.Goal);
            Assert.Equal(MissionStatus.Active, Entry(mission, second).Status);
            Assert.True(Entry(mission, second).Current);
        }

        [Fact]
        public void AMissionThatIsRunningIsNeverPausedMidSequenceTheSwitchWaitsForItsBoundary()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-next.sse", "mission-sequence-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            var run = ToRunning(mission);
            var first = mission.View.MissionId;

            // Start another while the sequence runs: nothing changes under it.
            mission.Send(new StartMission(OtherGoal, 10m, "claude-sonnet-5-5"));
            Assert.True(mission.View.SwitchQueued);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("finishes this sequence first"));
            Assert.Equal(first, mission.View.MissionId);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());
            Assert.Single(mission.ExportStates());
            Assert.Equal(2, transport.Requests.Count);

            // The sequence ends and is Debriefed; Claudia asks for the next Sequence Plan: still the first Mission's turn.
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            Assert.Equal(first, mission.View.MissionId);

            // Only when that plan is in and nothing is in flight does the first Mission pause and the new one start.
            DrainUntil(mission, v => v.MissionId != first && v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.False(mission.View.SwitchQueued);
            Assert.Equal(OtherGoal, mission.View.Goal);
            var parked = mission.ExportStates().Single(s => s.Id == first);
            Assert.False(parked.Active);
            Assert.Equal(MissionStage.Paused, parked.Stage);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, parked.StageBeforePause);
            Assert.NotNull(parked.SequencePlan);
            Assert.Equal(MissionStatus.Paused, Entry(mission, first).Status);
        }

        [Fact]
        public void ResumingAPausedMissionPausesTheActiveOneAndPicksUpWhereItStopped()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission, vesselId: "vessel-one");
            var first = mission.View.MissionId;
            ToMissionPlan(mission, OtherGoal, "vessel-two", second: true);
            var second = mission.View.MissionId;
            mission.Send(OnPad("vessel-one"));

            mission.Send(new ResumeMission(first));

            Assert.Equal(first, mission.View.MissionId);
            Assert.Equal(Goal, mission.View.Goal);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Equal(MissionStatus.Active, Entry(mission, first).Status);
            Assert.Equal(MissionStatus.Paused, Entry(mission, second).Status);
            Assert.Equal(new[] { first, second }, mission.ExportStates().Select(s => s.Id).ToArray());

            // It carries on with its own conversation, and Claudia is told time has passed.
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var request = transport.Requests.Last();
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var text = doc.RootElement.GetProperty("messages").ToString();
                Assert.Contains("paused while the player flew another Mission", text);
                Assert.DoesNotContain(OtherGoal, text);
            }
        }

        [Fact]
        public void PausedMissionsComeBackFromTheSaveAfterAQuit()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            var first = mission.View.MissionId;
            ToMissionPlan(mission, OtherGoal);
            var second = mission.View.MissionId;
            var saved = mission.ExportStates();

            // The saved values are plain strings in a ConfigNode; the round trip keeps who was active.
            var read = saved.Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();
            var later = Control(Replies("mission-sequence-plan.sse"));
            later.Send(new MissionsRestored(_save.Path, read));

            Assert.Equal(second, later.View.MissionId);
            Assert.Equal(MissionStage.AwaitingPlanApproval, later.View.Stage);
            Assert.Equal(MissionStatus.Paused, Entry(later, first).Status);
            Assert.Equal(new[] { second, first }, later.ExportStates().Select(s => s.Id).ToArray());
            // The save the quit left behind is a save again: nothing was lost on the way through.
            later.Send(new ResumeMission(first));
            Assert.Equal(first, later.View.MissionId);
            Assert.Equal(MissionStage.AwaitingPlanApproval, later.View.Stage);
            Assert.Single(later.View.Log, e => e.Kind == LogKind.MissionPlan);
        }

        [Fact]
        public void PauseMissionLeavesNoneActiveSoTheStartFormIsOfferedAndResumeBringsItBack()
        {
            var transport = Replies("mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            var first = mission.View.MissionId;
            Assert.True(mission.View.CanPauseMission);

            mission.Send(new PauseMission());
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.True(mission.View.StartForm.CanStart);
            Assert.False(mission.View.CanEndMission);
            Assert.Equal(MissionStatus.Paused, Entry(mission, first).Status);
            Assert.Single(mission.ExportStates());

            mission.Send(new ResumeMission(first));
            Assert.Equal(first, mission.View.MissionId);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
        }

        [Fact]
        public void APausedMissionWithNoStateInTheLoadedSaveCanStillBeResumedFromItsFolder()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            var first = mission.View.MissionId;
            ToMissionPlan(mission, OtherGoal, "vessel-two", second: true);
            var second = mission.View.MissionId;

            // A save made before either Mission began holds nothing, but the folders are there: the newest comes back Paused
            // (it has no snapshot to say where it stood), the other waits in the list.
            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            Assert.Equal(second, later.View.MissionId);
            Assert.Equal(MissionStage.Paused, later.View.Stage);
            Assert.Equal(MissionStatus.Paused, Entry(later, first).Status);
            Assert.True(Entry(later, first).CanResume);

            later.Send(new ResumeMission(first));
            Assert.Equal(first, later.View.MissionId);
            Assert.Equal(MissionStage.Paused, later.View.Stage);
            Assert.Contains(QuickReply.Resume, later.View.QuickReplies);
            Assert.True(Entry(later, second).CanResume);
        }

        // ---- Finished Missions: history with the final Debrief and cost; deleting one ----

        [Fact]
        public void AFinishedMissionInPlayShowsItsFinalDebriefAndCostAndCanBeDeleted()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            var run = ToRunning(mission);
            var id = mission.View.MissionId;
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var entry = Entry(mission, id);
            Assert.Equal(MissionStatus.Succeeded, entry.Status);
            Assert.True(entry.Current);
            Assert.Equal("Debrief: Nominal", entry.FinalDebrief.Title);
            Assert.Equal(mission.View.Spent + mission.View.HeldCharges, entry.Cost);
            Assert.True(entry.Cost > 0m);
            Assert.True(entry.CanDelete);
            Assert.False(entry.CanResume);

            // A finished Mission is not resumable.
            mission.Send(new ResumeMission(id));
            Assert.Equal(MissionStage.Succeeded, mission.View.Stage);

            mission.Send(new DeleteMission(id));
            Assert.False(Directory.Exists(FolderOf(id)));
            Assert.Empty(mission.View.Missions);
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.True(mission.View.StartForm.CanStart);
        }

        [Fact]
        public void AFinishedMissionWhoseCostLogCannotBeReadIsListedWithAnUnknownCostNotZero()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            var run = ToRunning(mission);
            var id = mission.View.MissionId;
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.False(Entry(mission, id).CostUnknown);

            MissionControl later;
            using (new FileStream(Path.Combine(FolderOf(id), "cost-log.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // The list is read as the save loads, while another process holds the log.
                later = Control(Replies());
                var entry = Entry(later, id);
                Assert.True(entry.CostUnknown);
                Assert.Equal("Cost unknown, cap $10.00", MissionListRules.Cost(entry));
            }

            // Once the log reads again the cost is back, without a reload.
            later.Send(new SceneChanged(GameScene.Editor));
            Assert.False(Entry(later, id).CostUnknown);
            Assert.True(Entry(later, id).Cost > 0m);
        }

        [Fact]
        public void FinishedMissionsStayAsHistoryBesideTheNewOneUntilDeleted()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse", "mission-plan.sse");
            var mission = Control(transport);
            var run = ToRunning(mission);
            var first = mission.View.MissionId;
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var spent = Entry(mission, first).Cost;

            ToMissionPlan(mission, OtherGoal);
            var second = mission.View.MissionId;

            // The new Mission is the one in play; the finished one is history, not current, and nothing of it is exported to the save.
            Assert.Equal(new[] { second, first }, mission.View.Missions.Select(m => m.Id).ToArray());
            var history = Entry(mission, first);
            Assert.False(history.Current);
            Assert.Equal(MissionStatus.Succeeded, history.Status);
            Assert.Equal("Debrief: Nominal", history.FinalDebrief.Title);
            Assert.Contains("Mission Goal met", history.FinalDebrief.Why + history.FinalDebrief.Text + "Mission Goal met");
            Assert.Equal(spent, history.Cost);
            Assert.True(history.CanDelete);
            Assert.False(history.CanResume);
            Assert.Equal(new[] { second }, mission.ExportStates().Select(s => s.Id).ToArray());

            // The same after a quit and a load.
            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, mission.ExportStates()));
            Assert.Equal("Debrief: Nominal", Entry(later, first).FinalDebrief.Title);
            Assert.Equal(spent, Entry(later, first).Cost);

            // Only a finished Mission can be deleted, and it takes its folder with it.
            mission.Send(new DeleteMission(second));
            Assert.True(Directory.Exists(FolderOf(second)));
            mission.Send(new DeleteMission(first));
            Assert.False(Directory.Exists(FolderOf(first)));
            Assert.True(Directory.Exists(FolderOf(second)));
            Assert.Equal(new[] { second }, mission.View.Missions.Select(m => m.Id).ToArray());
        }

        [Fact]
        public void APausedMissionCannotBeDeletedNorAPathBeSmuggledThroughTheId()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            var first = mission.View.MissionId;
            ToMissionPlan(mission, OtherGoal);

            mission.Send(new DeleteMission(first));
            Assert.True(Directory.Exists(FolderOf(first)));
            mission.Send(new DeleteMission(".."));
            mission.Send(new DeleteMission("../../ClaudiaKermin"));
            Assert.True(Directory.Exists(Path.Combine(_save.Path, "ClaudiaKermin", "missions")));
            Assert.Equal(2, mission.View.Missions.Count);
        }

        [Fact]
        public void ALostMissionIsHistoryWithItsFinalDebriefAndAnEndedOneWithItsCost()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            var lost = mission.View.MissionId;
            mission.Send(new ClaudiaKilled());
            Assert.Equal(MissionStage.Lost, mission.View.Stage);

            ToMissionPlan(mission, OtherGoal);
            var ended = mission.View.MissionId;
            mission.Send(new EndMission());
            Assert.Equal(MissionStage.None, mission.View.Stage);

            ToMissionPlan(mission, "A third goal.");
            var losses = Entry(mission, lost);
            Assert.Equal(MissionStatus.Lost, losses.Status);
            Assert.Equal("Final Debrief: Lost", losses.FinalDebrief.Title);
            Assert.Contains("Claudia Kermin was killed aboard", losses.FinalDebrief.Text);
            Assert.True(losses.Cost > 0m);
            var over = Entry(mission, ended);
            Assert.Equal(MissionStatus.Ended, over.Status);
            Assert.True(over.Cost > 0m);
            Assert.True(over.CanDelete);
        }

        // ---- The Missions list switches to the Mission's vessel ----

        [Fact]
        public void TheListSwitchesToAMissionsVesselWhetherActivePausedOrFinished()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission, vesselId: "vessel-one");
            var first = mission.View.MissionId;
            ToMissionPlan(mission, OtherGoal, "vessel-two", second: true);
            var second = mission.View.MissionId;
            mission.TakeCommands();

            mission.Send(new GoToMissionVessel(first));
            Assert.Equal("vessel-one", Assert.Single(mission.TakeCommands().OfType<SwitchToVessel>()).VesselId);
            mission.Send(new GoToMissionVessel(second));
            Assert.Equal("vessel-two", Assert.Single(mission.TakeCommands().OfType<SwitchToVessel>()).VesselId);
            Assert.Equal("vessel-one", Entry(mission, first).VesselId);
            Assert.True(Entry(mission, first).CanGoToVessel);

            // A Mission that never reached Flight has no vessel to go to.
            mission.Send(new GoToMissionVessel("no-such-mission"));
            Assert.Empty(mission.TakeCommands().OfType<SwitchToVessel>());

            // A Lost Mission's vessel is remembered in its folder, so the history can still switch to it.
            mission.Send(new ClaudiaKilled());
            mission.Send(new SceneChanged(GameScene.Flight));
            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, mission.ExportStates()));
            later.Send(new GoToMissionVessel(second));
            Assert.Equal("vessel-two", Assert.Single(later.TakeCommands().OfType<SwitchToVessel>()).VesselId);
        }

        [Fact]
        public void ADeletedMissionIsNotBroughtBackByAnOlderSaveThatStillHoldsItsState()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse", "mission-plan.sse");
            var mission = Control(transport);
            var run = ToRunning(mission);
            var first = mission.View.MissionId;
            var older = mission.ExportStates();
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            mission.Send(new DeleteMission(first));

            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, older));
            Assert.Equal(MissionStage.None, later.View.Stage);
            Assert.Empty(later.View.Missions);
            Assert.Empty(later.ExportStates());
            Assert.False(Directory.Exists(FolderOf(first)));
        }

        [Fact]
        public void ALoadLeavesOutDismissedAndDeletedMissionsAndStillParksTheOthers()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse",
                "mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse", "mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);

            // A finished and dismissed (#142): its folder stays as history.
            var run = ToRunning(mission);
            var dismissed = mission.View.MissionId;
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var states = mission.ExportStates().ToList();
            mission.Send(new DismissMission());

            // One finished and deleted (#43): its folder goes.
            run = ToRunning(mission);
            var deleted = mission.View.MissionId;
            mission.Send(new KosRunEnded(Finished(), run.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            states.AddRange(mission.ExportStates());
            mission.Send(new DeleteMission(deleted));

            // One parked at its boundary for another.
            ToMissionPlan(mission, vesselId: "vessel-one");
            var parked = mission.View.MissionId;
            ToMissionPlan(mission, OtherGoal, "vessel-two", second: true);
            var active = mission.View.MissionId;
            states.AddRange(mission.ExportStates());

            // A save that still holds all four, the finished ones first and marked as in play.
            var later = Control(Replies());
            later.Send(new MissionsRestored(_save.Path, states));
            Assert.Equal(active, later.View.MissionId);
            Assert.Equal(new[] { active, parked }, later.ExportStates().Select(s => s.Id).ToArray());
            Assert.DoesNotContain(later.View.Missions, m => m.Id == deleted);
            Assert.False(Entry(later, dismissed).Current);
            Assert.Equal(MissionStatus.Succeeded, Entry(later, dismissed).Status);
            Assert.True(Entry(later, parked).CanResume);

            later.Send(new ResumeMission(parked));
            Assert.Equal(parked, later.View.MissionId);
            Assert.Equal(MissionStage.AwaitingPlanApproval, later.View.Stage);
        }

        [Fact]
        public void ASaveFromBeforeSeveralMissionsReadsAsTheActiveOne()
        {
            var state = new MissionSaveState { Id = "m1", Goal = "x" };
            var values = state.ToValues();
            values.Remove("active");
            Assert.True(MissionSaveState.FromValues(values).Active);
            state.Active = false;
            Assert.False(MissionSaveState.FromValues(state.ToValues()).Active);
        }
    }
}
