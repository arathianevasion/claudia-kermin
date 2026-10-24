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
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// A Mission surviving scene changes, quicksaves, reverts, quits and crashes, through the same single
    /// seam as <see cref="MissionTests"/>. A save is <see cref="MissionControl.ExportStates"/> taken at some
    /// moment; a load is <see cref="MissionsRestored"/>; a crash or quit is a second <see cref="MissionControl"/>
    /// started over the same save directory (the old one is never drained again).
    /// </summary>
    public sealed class MissionPersistenceTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly List<ScriptedResponse> _gates = new List<ScriptedResponse>();

        public void Dispose()
        {
            // Let any request left in flight by a "crashed" Mission finish into a queue nobody drains.
            foreach (var response in _gates) response.Gate.Set();
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport, Background background = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, background ?? _background, new PlayerConfig(), () => true);
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        /// <summary>A "crashed" game: its own queue, never drained, so nothing it still has in flight can reach the files.</summary>
        private MissionControl Crashable(StreamingFakeTransport transport)
        {
            var background = new Background();
            var mission = Control(transport, background);
            _crashable[mission] = background;
            return mission;
        }

        private readonly Dictionary<MissionControl, Background> _crashable = new Dictionary<MissionControl, Background>();

        /// <summary>Start Mission with its Craft Check (which passes) drained first, on the queue the Mission was built with.</summary>
        private void StartChecked(MissionControl mission, StartMission start)
        {
            Background background;
            CraftCheckRouter.StartChecked(mission, _crashable.TryGetValue(mission, out background) ? background : _background, OnPad(), start);
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

        private static List<StartScript> Started(MissionControl mission)
        {
            return mission.TakeCommands().OfType<StartScript>().ToList();
        }

        private string MissionFolder(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId);
        }

        private static JsonElement[] Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToArray();
        }

        private static string Text(JsonElement message)
        {
            var content = message.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        }

        private static string RawMessages(string json)
        {
            using (var doc = JsonDocument.Parse(json))
                return doc.RootElement.GetProperty("messages").GetRawText();
        }

        private static void AssertExtends(string earlierTranscript, HttpRequestSpec request)
        {
            var sent = RawMessages(earlierTranscript);
            Assert.StartsWith(sent.Substring(0, sent.Length - 1) + ",", RawMessages(request.Body));
        }

        private static string Notices(HttpRequestSpec request)
        {
            return string.Join("\n", Messages(request).Where(m => m.GetProperty("role").GetString() == "system" && m.GetProperty("content").ValueKind == JsonValueKind.String).Select(Text));
        }

        /// <summary>Start in Flight and get to the Mission Plan awaiting Approval.</summary>
        private void ToMissionPlan(MissionControl mission, decimal cap = 10m)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            StartChecked(mission, new StartMission(Goal, cap, "claude-sonnet-5-5"));
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
            return Assert.Single(Started(mission));
        }

        private string TranscriptPath(MissionControl mission)
        {
            return Path.Combine(MissionFolder(mission), "transcript.json");
        }

        private static IList<MissionSaveState> Saved(MissionControl mission)
        {
            return mission.ExportStates();
        }

        // ---- The save-state as plain data ----

        [Fact]
        public void TheSaveStateHoldsWhatTheSpecSaysAndRoundTripsThroughPlainValues()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);

            var state = Assert.Single(Saved(mission));
            Assert.Equal(mission.View.MissionId, state.Id);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, state.Stage);
            Assert.Equal(10m, state.SpendingCap);
            Assert.Equal("claude-sonnet-5-5", state.Model);
            Assert.Equal(Goal, state.Goal);
            Assert.NotNull(state.SequencePlan);
            Assert.StartsWith("print \"Claudia: link check.\".\n", state.SequencePlan.Script);
            Assert.Single(state.MissionPlan.Sequences);
            // The marker counts the messages in the committed conversation: an effort change, then two turns of user + notice + reply.
            Assert.Equal(7, state.TranscriptMarker);

            state.Settings["daylight_landing"] = "true";
            state.SpendingCap = 12.5m;
            var again = MissionSaveState.FromValues(state.ToValues());
            Assert.Equal(state.Id, again.Id);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, again.Stage);
            Assert.Equal(12.5m, again.SpendingCap);
            Assert.Equal("true", again.Settings["daylight_landing"]);
            Assert.Equal(state.TranscriptMarker, again.TranscriptMarker);
            Assert.Equal(state.SequencePlan.Script, again.SequencePlan.Script);
            Assert.Equal(state.SequencePlan.AbortConditions, again.SequencePlan.AbortConditions);
            Assert.Equal(state.MissionPlan.Sequences[0].Name, again.MissionPlan.Sequences[0].Name);
        }

        [Fact]
        public void TheSaveValuesAreSingleLinesThatSurviveAConfigNodeWhateverThePlayerTyped()
        {
            var state = new MissionSaveState
            {
                Id = "m1",
                Goal = "Land on the Mun.\r\nThen // come {home}\\ safely",
                SequencePlan = new SequencePlan { Name = "Burn", Script = "// go\nprint \"{x}\".\n" },
            };
            var values = state.ToValues();
            foreach (var value in values.Values)
                Assert.True(value.IndexOfAny(new[] { '\n', '\r', '{', '}', '/' }) < 0, value);
            var again = MissionSaveState.FromValues(values);
            Assert.Equal(state.Goal, again.Goal);
            Assert.Equal(state.SequencePlan.Script, again.SequencePlan.Script);
        }

        [Fact]
        public void ThereIsNothingToSaveWithoutAMission()
        {
            Assert.Empty(Saved(Control(new StreamingFakeTransport())));
        }

        // ---- Quickload and revert ----

        [Fact]
        public void ARevertBringsTheLoopStateBackButNeverRewindsTheConversationOrTheFiles()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);
            var saved = Saved(mission);
            var plannedAt = saved[0].TranscriptMarker;

            // Later in the same timeline: the Mission Plan is approved and a Sequence Plan proposed.
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var transcript = File.ReadAllText(TranscriptPath(mission));
            var events = File.ReadAllLines(Path.Combine(MissionFolder(mission), "events.jsonl"));
            Assert.True(RawMessagesCount(transcript) > plannedAt);

            // Revert to the save: the loop state is the save's, the conversation is not.
            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(new[] { "Link check" }, mission.View.PlanSequences);
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("earlier save"));
            Assert.Equal(transcript, File.ReadAllText(TranscriptPath(mission)));
            var eventsNow = File.ReadAllLines(Path.Combine(MissionFolder(mission), "events.jsonl"));
            Assert.Equal(events, eventsNow.Take(events.Length));
            Assert.True(eventsNow.Length > events.Length, "the reload is appended to the event log");
            Assert.Equal(2, transport.Requests.Count);

            // The next request carries every turn Claudia already had, byte for byte, then the notice.
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(3, transport.Requests.Count);
            AssertExtends(transcript, transport.Requests[2]);
            var notice = Notices(transport.Requests[2]);
            Assert.Contains("earlier save", notice);
            Assert.Contains("conversation above still happened", notice);
            // One notice, not one per later request.
            Assert.DoesNotContain("earlier save", Notices(transport.Requests[1]));
        }

        private static int RawMessagesCount(string transcript)
        {
            using (var doc = JsonDocument.Parse(transcript))
                return doc.RootElement.GetProperty("messages").GetArrayLength();
        }

        [Fact]
        public void AQuickloadOfTheSameMomentAddsNoNoticeAndSendsNothing()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);
            var saved = Saved(mission);

            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("earlier save"));
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void TheSpendingCapAndMissionSettingsComeBackFromTheSaveNotFromTheLaterTimeline()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission, 7m);
            var saved = Saved(mission);
            saved[0].SpendingCap = 9m;
            saved[0].Settings["time_over_dv"] = "time";

            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(9m, mission.View.SpendingCap);
            Assert.Equal("time", Assert.Single(Saved(mission)).Settings["time_over_dv"]);
            Assert.Equal(9m, Saved(mission)[0].SpendingCap);
        }

        [Fact]
        public void LoadingASaveDuringARequestLetsItCompleteThenAppendsTheNotice()
        {
            var sequence = ScriptedResponse.Fixture("mission-sequence-plan.sse", 64);
            sequence.PauseAt = 100;
            _gates.Add(sequence);
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                sequence,
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);
            var saved = Saved(mission);

            mission.Send(new Approve());
            Assert.True(mission.View.Busy);

            // The load does not cancel the request.
            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.True(mission.View.Busy);
            sequence.Gate.Set();
            DrainUntil(mission, v => !v.Busy);

            // The reply was kept in the conversation but did not change the loaded loop state.
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(7, RawMessagesCount(File.ReadAllText(TranscriptPath(mission))));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("earlier save"));

            var transcript = File.ReadAllText(TranscriptPath(mission));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            AssertExtends(transcript, transport.Requests[2]);
            Assert.Contains("earlier save", Notices(transport.Requests[2]));
        }

        [Fact]
        public void AnUnrelatedLoadOfAnotherSaveStillDropsTheMissionAndRestoresThatSavesOwn()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);

            mission.Send(new MissionsRestored(Path.Combine(_save.Path, "other"), new List<MissionSaveState>()));
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.True(mission.View.StartForm.CanStart);
        }

        // ---- Spend survives a load: the cost log is the truth, never the save ----

        [Fact]
        public void RestoringAnEarlierSaveKeepsAllSpendAndTheCapCheckSeesIt()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission, 1.20m);
            var saved = Saved(mission);
            Assert.Equal(0.54m, mission.View.Spent);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(1.08m, mission.View.Spent);

            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(1.08m, mission.View.Spent);
            Assert.Equal(0m, mission.View.HeldCharges);

            // $1.08 of $1.20 leaves no room for another request's worst case; at the save's $0.54 there was.
            mission.Send(new Approve());
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
        }

        [Fact]
        public void ACrashMidRequestCountsTheWorstCaseAsKeptInAFreshGame()
        {
            var held = ScriptedResponse.Fixture("mission-plan-priced.sse", 64);
            held.PauseAt = 100;
            _gates.Add(held);
            var crashed = Crashable(new StreamingFakeTransport(held));
            crashed.Send(new SceneChanged(GameScene.Flight));
            crashed.Send(OnPad());
            StartChecked(crashed, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            Assert.True(crashed.View.Busy);
            var worstCase = crashed.View.PendingCharge;
            Assert.True(worstCase > 0m);
            var saved = Saved(crashed);

            // The resumed game has no snapshot yet, so the unanswered turn waits; nothing is sent to settle it.
            var resumed = Control(new StreamingFakeTransport());
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(worstCase, resumed.View.HeldCharges);
            Assert.Equal(0m, resumed.View.Spent);
        }

        [Fact]
        public void AMissionFolderWithNoSnapshotRestoresItsSpendFromTheCostLog()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);
            Assert.Equal(0.54m, mission.View.Spent);

            var later = Control(new StreamingFakeTransport());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            Assert.Equal(mission.View.MissionId, later.View.MissionId);
            Assert.Equal(0.54m, later.View.Spent);
        }

        [Fact]
        public void ATornLastLineInTheCostLogFallsBackToTheLineBeforeIt()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse")));
            ToMissionPlan(mission);
            File.AppendAllText(Path.Combine(MissionFolder(mission), "cost-log.jsonl"), "{\"event\":\"pend");

            var later = Control(new StreamingFakeTransport());
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            Assert.Equal(0.54m, later.View.Spent);
        }

        [Fact]
        public void ALoadDuringARequestRestoresTheSavedStateWithTheSpendIncludingThatRequest()
        {
            var plan = ScriptedResponse.Fixture("mission-plan-priced.sse");
            var sequence = ScriptedResponse.Fixture("mission-sequence-plan-priced.sse");
            sequence.PauseAt = 200;
            _gates.Add(sequence);
            var mission = Control(new StreamingFakeTransport(plan, sequence));
            ToMissionPlan(mission);
            var saved = Saved(mission);

            mission.Send(new Approve());
            Assert.True(mission.View.Busy);
            mission.Send(new MissionsRestored(_save.Path, saved));
            sequence.Gate.Set();
            DrainUntil(mission, v => !v.Busy && v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(1.08m, mission.View.Spent);
        }

        [Fact]
        public void ARestoredMissionPastEightyPercentDoesNotWarnAgainAndOneAtTheCapPausesOnItsNextRequest()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-big.sse"));
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            StartChecked(mission, new StartMission(Goal, 0.50m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(0.80m, mission.View.Spent);
            var saved = Saved(mission);

            var resumed = Control(new StreamingFakeTransport());
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(0.80m, resumed.View.Spent);
            Assert.True(resumed.View.CapWarning);
            Assert.DoesNotContain(resumed.View.Log, e => e.Text.Contains("80%"));
            Assert.DoesNotContain(resumed.TakeCommands(), c => c is PostScreenMessage && ((PostScreenMessage)c).Text.Contains("80%"));

            // Its next request is turned away: Raise cap or End Mission.
            resumed.Send(new Retry());
            Assert.Equal(MissionStage.AwaitingPlanApproval, resumed.View.Stage);
            resumed.Send(new Approve());
            Assert.Equal(MissionStage.Paused, resumed.View.Stage);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, resumed.View.QuickReplies);
            Assert.Equal(0m, resumed.View.PendingCharge);
        }

        // ---- Crash or quit mid-request ----

        [Fact]
        public void ACrashMidRequestResendsTheUnansweredTurnFromTheTranscriptOnResume()
        {
            var held = ScriptedResponse.Fixture("mission-plan.sse", 64);
            held.PauseAt = 100;
            _gates.Add(held);
            var before = new StreamingFakeTransport(held);
            var crashed = Crashable(before);
            crashed.Send(new SceneChanged(GameScene.Flight));
            crashed.Send(OnPad());
            StartChecked(crashed, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            Assert.True(crashed.View.Busy);
            var saved = Saved(crashed);
            var folder = MissionFolder(crashed);

            // The game starts again from the save; the same request goes out and is answered.
            var after = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(Goal, resumed.View.Goal);
            Assert.True(resumed.View.Busy);
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Single(after.Requests);
            Assert.Equal(before.Requests[0].Body, after.Requests[0].Body);
            Assert.Equal(folder, MissionFolder(resumed));
            Assert.True(File.Exists(Path.Combine(folder, "transcript.json")));
            Assert.Empty(Started(resumed));
        }

        [Fact]
        public void ACrashAfterTheLastSaveStillResendsATurnTheSaveNeverSawBecauseTheTranscriptHoldsIt()
        {
            var held = ScriptedResponse.Fixture("mission-plan.sse", 64);
            held.PauseAt = 100;
            _gates.Add(held);
            var before = new StreamingFakeTransport(held);
            var crashed = Crashable(before);
            crashed.Send(new SceneChanged(GameScene.Editor));
            StartChecked(crashed, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            var saved = Saved(crashed);
            Assert.Equal(MissionStage.WaitingForFlight, saved[0].Stage);
            // The craft reaches Flight and Claudia is asked for the Mission Plan, after the save was made.
            crashed.Send(new SceneChanged(GameScene.Flight));
            crashed.Send(OnPad());
            Assert.True(crashed.View.Busy);

            var after = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(before.Requests[0].Body, after.Requests[0].Body);
        }

        [Fact]
        public void AFailedRequestStaysPausedAcrossAReloadAndRetrySendsTheSameTurn()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Error(400, "invalid_request_error", "bad"),
                ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            StartChecked(mission, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            var saved = Saved(mission);
            Assert.Equal(MissionStage.Paused, saved[0].Stage);

            var after = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.Paused, resumed.View.Stage);
            Assert.Empty(after.Requests);
            resumed.Send(new Retry());
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(transport.Requests[0].Body, after.Requests[0].Body);
        }

        // ---- Crash, quit or leaving Flight while Running ----

        [Fact]
        public void AnythingRunningAtACrashIsInterruptedDebriefedAndNeverRestarted()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToRunning(mission);
            var saved = Saved(mission);
            Assert.Equal(MissionStage.Running, saved[0].Stage);

            var after = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-debrief.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            // Running with no program: the sequence is interrupted, not restarted.
            Assert.NotEqual(MissionStage.Running, resumed.View.Stage);
            Assert.Equal("", resumed.View.RunStatus);
            Assert.Empty(Started(resumed));
            Assert.Empty(after.Requests);

            // The Debrief waits for the craft to be in Flight.
            resumed.Send(new SceneChanged(GameScene.Flight));
            Assert.Empty(after.Requests);
            resumed.Send(OnPad());
            DrainUntil(resumed, v => v.Stage == MissionStage.Succeeded);
            Assert.Single(after.Requests);
            var turn = Messages(after.Requests[0]).Where(m => m.GetProperty("role").GetString() == "user").Select(Text).Last();
            Assert.Contains("Interrupted", turn);
            Assert.Contains("Link check", turn);
            Assert.Empty(Started(resumed));
            Assert.Equal("Debrief", resumed.View.Log.Last(e => e.Kind == LogKind.Debrief).Title.Split(':')[0]);
        }

        [Fact]
        public void AQuickloadToARunningSaveInterruptsTheSequenceToo()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            var saved = Saved(mission);

            mission.Send(new MissionsRestored(_save.Path, saved));
            // The reloaded craft has not been seen yet: nothing is asked until a fresh Game Snapshot arrives.
            Assert.Equal(2, transport.Requests.Count);
            mission.Send(OnPad());
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("Interrupted", Messages(transport.Requests[2]).Where(m => m.GetProperty("role").GetString() == "user").Select(Text).Last());
            Assert.Empty(Started(mission));
            // The old program's late end report is not a second Debrief.
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Interrupted, null, "", "gone", false), start.RunId));
            Assert.Equal(3, transport.Requests.Count);
        }

        [Fact]
        public void LeavingFlightWhileRunningAsksFirstAndAConfirmedLeaveMarksTheSequenceInterruptedForTheReturn()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            Assert.False(mission.View.LeavingFlightNeedsConfirmation);
            mission.Send(new Approve());
            Assert.True(mission.View.LeavingFlightNeedsConfirmation);
            var start = Assert.Single(Started(mission));

            // The adapter asked, and the player said yes: the scene changes.
            mission.Send(new SceneChanged(GameScene.Other));
            Assert.False(mission.View.LeavingFlightNeedsConfirmation);
            Assert.NotEqual(MissionStage.Running, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("interrupted"));

            // The program's own end report arrives late and changes nothing.
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Interrupted, null, "", "scene change", false), start.RunId));
            Assert.Equal(2, transport.Requests.Count);

            // On return to Flight the interrupted-sequence Debrief is asked for.
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var turn = Messages(transport.Requests[2]).Where(m => m.GetProperty("role").GetString() == "user").Select(Text).Last();
            Assert.Contains("Interrupted", turn);
            Assert.Contains("left Flight", turn);
        }

        [Fact]
        public void LeavingFlightWhileNotRunningNeedsNoConfirmationAndInterruptsNothing()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.Send(new SceneChanged(GameScene.Other));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.False(mission.View.LeavingFlightNeedsConfirmation);
        }

        // ---- A save with no snapshot for the Mission ----

        [Fact]
        public void AMissionFolderWithNoSnapshotInTheLoadedSaveShowsPausedWithResumeOrEndMission()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);
            var transcript = File.ReadAllText(TranscriptPath(mission));

            // Load a save from before the Mission existed.
            var resumed = Control(transport);
            resumed.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            Assert.Equal(MissionStage.Paused, resumed.View.Stage);
            Assert.Equal(Goal, resumed.View.Goal);
            Assert.Equal(new[] { QuickReply.Resume }, resumed.View.QuickReplies);
            Assert.True(resumed.View.CanEndMission);
            Assert.Contains("save", resumed.View.PauseReason);
            Assert.Single(transport.Requests);
            Assert.Equal(transcript, File.ReadAllText(TranscriptPath(resumed)));

            // Resume: Claudia plans afresh once the craft is in Flight, from the whole conversation so far.
            resumed.Send(new SceneChanged(GameScene.Flight));
            resumed.Send(OnPad());
            resumed.Send(new Resume());
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, transport.Requests.Count);
            AssertExtends(transcript, transport.Requests[1]);
            Assert.Contains("save", Notices(transport.Requests[1]));
        }

        [Fact]
        public void EndMissionOnThatPausedMissionKeepsTheFolderAndLaterLoadsDoNotOfferItAgain()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            ToMissionPlan(mission);
            var folder = MissionFolder(mission);

            var resumed = Control(transport);
            resumed.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            resumed.Send(new EndMission());
            Assert.Equal(MissionStage.None, resumed.View.Stage);
            Assert.True(File.Exists(Path.Combine(folder, "transcript.json")));

            var later = Control(transport);
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            Assert.Equal(MissionStage.None, later.View.Stage);
            Assert.True(later.View.StartForm.CanStart);
        }

        [Fact]
        public void AFinishedMissionIsNotOfferedAsAPausedOne()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            var later = Control(transport);
            later.Send(new MissionsRestored(_save.Path, new List<MissionSaveState>()));
            Assert.Equal(MissionStage.None, later.View.Stage);
        }
    }
}
