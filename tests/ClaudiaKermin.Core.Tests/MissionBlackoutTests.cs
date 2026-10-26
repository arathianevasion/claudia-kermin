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
    /// Blackout (spec stories 99 to 105, ADR 0002's amendment), driven through the Mission seam with hand-written streams:
    /// link loss starts it (CommNet off counts as linked), the Mission Plan's Approval stands for each sequence, her messages
    /// are queued and the player's held, Alerts are recorded without a call, Abort is hidden, a Failed Debrief makes the
    /// craft safe and waits for the link, link loss in Manual Control with nobody else aboard takes the craft back (#130),
    /// and an Abort recommendation outside Blackout is a moment the player is needed (#120).
    /// </summary>
    public sealed class MissionBlackoutTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string CraftId = "c0ffee00c0ffee00c0ffee00c0ffee00";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Link state ----

        [Fact]
        public void LosingTheLinkStartsABlackoutAndCommNetOffCountsAsLinked()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToRunning(mission);
            Assert.False(mission.View.Blackout);

            // CommNet off in the save: no connection, but linked all the same.
            mission.Send(Snapshot(s => { s.HasLink = false; s.CommNetEnabled = false; }));
            Assert.False(mission.View.Blackout);

            mission.Send(Snapshot(s => s.HasLink = false));
            Assert.True(mission.View.Blackout);
            // A Blackout is not a Pause: the run carries on.
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains("No link to Kerbin", mission.View.LatestLine);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("No link to Kerbin: Blackout"));

            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.False(mission.View.Blackout);
        }

        [Fact]
        public void TheLinkOfTheCraftSeenFromAnotherVesselCountsToo()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToRunning(mission);

            mission.Send(new CraftLinkSeen("some-other-vessel", false));
            Assert.False(mission.View.Blackout);
            mission.Send(new CraftLinkSeen(CraftId, false));
            Assert.True(mission.View.Blackout);
            mission.Send(new CraftLinkSeen(CraftId, true));
            Assert.False(mission.View.Blackout);
        }

        // ---- Alerts and Abort ----

        [Fact]
        public void InABlackoutACrossedCheckIsRecordedWithoutAClaudeCallAndAbortIsHidden()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            Assert.Contains(QuickReply.Abort, mission.View.QuickReplies);

            mission.Send(Snapshot(s => s.HasLink = false));
            Assert.DoesNotContain(QuickReply.Abort, mission.View.QuickReplies);

            // 100 m/s used against a 50 m/s check.
            mission.Send(Snapshot(s => { s.HasLink = false; s.Stages[0].DeltaVVac = 3100; }));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("recorded without a call to Claudia"));
            Assert.Equal("", mission.View.AbortRecommended);

            // The Abort button does nothing in a Blackout.
            mission.TakeCommands();
            mission.Send(new AbortSequence());
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            // The Debrief still hears of the crossing.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count == 3 && !v.Busy);
            Assert.Contains("delta-v used 100 m/s over 50 m/s: recorded in a Blackout, not judged", LastUserText(transport.Requests[2]));
        }

        // ---- Approval stands; her messages wait for the link ----

        [Fact]
        public void InABlackoutEachSequenceFliesWithoutApprovalAndHerMessagesWaitForTheLinkWithEveryRevisionAndItsReasons()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefGoalNotMet(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();
            var shown = mission.View.Log.Count;

            // The link drops with a Sequence Plan waiting: the approved Mission Plan stands for it.
            mission.Send(Snapshot(s => s.HasLink = false));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var first = mission.TakeCommands().OfType<StartScript>().Single();

            mission.Send(new KosRunEnded(Finished(), first.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            var commands = mission.TakeCommands();
            var second = commands.OfType<StartScript>().Single();
            Assert.NotEqual(first.RunId, second.RunId);
            // Her Debrief and her next plan are queued: not in the log, not on screen.
            Assert.DoesNotContain(mission.View.Log.Skip(shown), e => e.Kind != LogKind.Notice);
            // Her Debrief line and card, and her next Sequence Plan's line and card.
            Assert.Equal(4, mission.View.QueuedMessages);
            Assert.DoesNotContain(commands.OfType<PostScreenMessage>(), m => m.Text.StartsWith("Claudia"));
            // Claudia is told she is in a Blackout and what she may do.
            Assert.Contains("Blackout: there is no link to Kerbin", AllText(transport.Requests[3]));
            Assert.Contains("within the Mission Goal and the Spending Cap", AllText(transport.Requests[3]));

            // The link returns: her messages show, with each revision and her reasons.
            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.False(mission.View.Blackout);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Debrief);
            var revisions = mission.View.Log.Single(e => e.Title == "While there was no link");
            Assert.Equal(2, revisions.Text.Split('\n').Count(l => l.StartsWith("- Sequence Plan \"Link check\" flown without Approval: ")));
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Text.StartsWith("The link to Kerbin is back"));
            Assert.Contains(QuickReply.Abort, mission.View.QuickReplies);
        }

        [Fact]
        public void TheFirstMissionPlanWaitsForTheLinkBecauseThePlayerNeverApprovedOne()
        {
            var plan = ScriptedResponse.Fixture("mission-plan.sse", 64);
            plan.PauseAt = 100;
            var mission = Control(new StreamingFakeTransport(plan, ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.Planning, mission.View.Stage);

            mission.Send(Snapshot(s => s.HasLink = false));
            plan.Gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy);
            Assert.DoesNotContain(QuickReply.Approve, mission.View.QuickReplies);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);

            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Contains(QuickReply.Approve, mission.View.QuickReplies);
        }

        // ---- The player's words are held ----

        [Fact]
        public void ThePlayersWordsAreHeldInABlackoutAndGoToClaudiaWhenTheLinkReturns()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(Snapshot(s => s.HasLink = false));
            Assert.True(mission.View.CanType);

            mission.Send(new AskQuestion("How is it going?"));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(1, mission.View.HeldMessages);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text == "How is it going?");

            mission.Send(Snapshot(s => s.HasLink = true));
            DrainUntil(mission, v => transport.Requests.Count == 3 && !v.Busy);
            Assert.Contains("While the link to Kerbin was down the player said (quoted data): \"How is it going?\"", LastUserText(transport.Requests[2]));
            Assert.Equal(0, mission.View.HeldMessages);
        }

        // ---- A save made in a Blackout (story 101) ----

        [Fact]
        public void ABlackoutsRevisionsHeldWordsAndQueuedMessagesSurviveALoadAndShowWhenTheLinkReturns()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefGoalNotMet(),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();
            mission.Send(Snapshot(s => s.HasLink = false));
            var first = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(Finished(), first.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            mission.Send(new AskQuestion("Are we still on course?"));
            Assert.Equal(4, mission.View.QueuedMessages);
            Assert.Equal(1, mission.View.HeldMessages);

            var saved = mission.ExportStates();
            Assert.Equal(4, saved[0].QueuedEntries);
            Assert.Equal(2, saved[0].BlackoutRevisions.Count);
            Assert.Equal(new[] { "Are we still on course?" }, saved[0].HeldWords);

            // The save file is plain values; the load is a game started over the same save.
            var after = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-debrief.sse"), ScriptedResponse.Fixture("mission-answer.sse"), ScriptedResponse.Fixture("mission-answer.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, new[] { MissionSaveState.FromValues(saved[0].ToValues()) }));
            Assert.Equal(4, resumed.View.QueuedMessages);
            Assert.Equal(1, resumed.View.HeldMessages);
            Assert.DoesNotContain(resumed.View.Log, e => e.Kind == LogKind.Debrief);

            // The link is still down when seen again: still the one Blackout, nothing shown early.
            resumed.Send(Snapshot(s => s.HasLink = false));
            Assert.True(resumed.View.Blackout);
            Assert.Equal(4, resumed.View.QueuedMessages);
            Assert.DoesNotContain(resumed.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("No link to Kerbin: Blackout"));

            resumed.Send(Snapshot(s => s.HasLink = true));
            Assert.False(resumed.View.Blackout);
            Assert.Equal(0, resumed.View.QueuedMessages);
            Assert.Contains(resumed.View.Log, e => e.Kind == LogKind.Debrief);
            var revisions = resumed.View.Log.Single(e => e.Title == "While there was no link");
            Assert.Equal(2, revisions.Text.Split('\n').Count(l => l.StartsWith("- Sequence Plan \"Link check\" flown without Approval: ")));
            DrainUntil(resumed, v => !v.Busy);
            Assert.Equal(0, resumed.View.HeldMessages);
            Assert.Contains(after.Requests, r => AllText(r).Contains("While the link to Kerbin was down the player said (quoted data): \\\"Are we still on course?\\\""));
        }

        [Fact]
        public void ALoadedBlackoutWaitsForTheLinkToBeSeenBeforeItEnds()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToRunning(mission);
            mission.Send(Snapshot(s => s.HasLink = false));
            mission.Send(new AskQuestion("Hello?"));
            var saved = mission.ExportStates();

            var resumed = Control(new StreamingFakeTransport());
            resumed.Send(new MissionsRestored(_save.Path, new[] { MissionSaveState.FromValues(saved[0].ToValues()) }));
            resumed.Send(new UtAdvanced(OnPad().Ut + 1));
            Assert.Equal(1, resumed.View.HeldMessages);
            Assert.True(resumed.View.Blackout);
        }

        [Fact]
        public void ASaveFromBeforeBlackoutsWerePersistedLoadsWithNoneAndSavesNoneWithoutABlackout()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToRunning(mission);
            var values = mission.ExportStates()[0].ToValues();
            Assert.DoesNotContain(values.Keys, k => k == "queuedEntries" || k.StartsWith("blackoutRevision.") || k.StartsWith("heldWord."));

            var old = MissionSaveState.FromValues(values);
            Assert.Equal(0, old.QueuedEntries);
            Assert.Empty(old.BlackoutRevisions);
            Assert.Empty(old.HeldWords);
            var resumed = Control(new StreamingFakeTransport());
            resumed.Send(new MissionsRestored(_save.Path, new[] { old }));
            Assert.False(resumed.View.Blackout);
            Assert.Equal(0, resumed.View.QueuedMessages);
            Assert.Equal(0, resumed.View.HeldMessages);
        }

        // ---- The Spending Cap ----

        [Fact]
        public void InABlackoutARequestThatWouldPassTheCapWaitsInsteadOfPausing()
        {
            // The priced plan and Sequence Plan cost $1.08, so the Debrief's worst case no longer fits a $1.20 cap.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);
            mission.Send(Snapshot(s => s.HasLink = false));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            _background.Drain();

            // Out of a Blackout this Debrief Pauses for Raise cap (story 124); here it waits.
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Contains(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Equal(2, transport.Requests.Count);
        }

        // ---- A Failed Debrief ----

        [Fact]
        public void AFailedDebriefMakesTheCraftSafeAndWaitsForThePlayerWithALink()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            var safe = mission.TakeCommands().OfType<MakeCraftSafe>().Single();
            // Aimed at the Mission's craft, so it holds with the player in another vessel (#191).
            Assert.Equal(CraftId, safe.VesselId);
            Assert.Equal(CraftId, safe.Target("another-vessel"));
        }

        [Fact]
        public void AnUnloadedCraftIsMadeSafeWhenItNextLoadsEvenAcrossASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            mission.TakeCommands();

            // The adapter found the craft unloaded: nothing owed is re-issued while it stays unloaded.
            mission.Send(new CraftSafingUnreached("some other vessel", "another-vessel"));
            Assert.Equal("", mission.ExportStates()[0].SafePending);
            mission.Send(new CraftSafingUnreached("a Failed Debrief", CraftId));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new VesselsSeen(new[] { new SeenVessel { Id = CraftId, Loaded = false } }));
            Assert.Empty(mission.TakeCommands().OfType<MakeCraftSafe>());

            // It survives the save.
            var saved = mission.ExportStates();
            Assert.Equal("a Failed Debrief", saved[0].SafePending);
            Assert.Equal("a Failed Debrief", MissionSaveState.FromValues(saved[0].ToValues()).SafePending);
            Assert.Equal("", MissionSaveState.FromValues(new Dictionary<string, string> { { "id", "m1" } }).SafePending);

            // The craft loads: it is made safe once.
            mission.Send(new VesselsSeen(new[] { new SeenVessel { Id = CraftId, Loaded = true } }));
            var safe = mission.TakeCommands().OfType<MakeCraftSafe>().Single();
            Assert.Equal(CraftId, safe.VesselId);
            Assert.Equal("a Failed Debrief", safe.Reason);
            mission.Send(new VesselsSeen(new[] { new SeenVessel { Id = CraftId, Loaded = true } }));
            Assert.Empty(mission.TakeCommands().OfType<MakeCraftSafe>());
            Assert.Equal("", mission.ExportStates()[0].SafePending);
        }

        [Fact]
        public void MakeCraftSafeFallsBackToTheActiveVesselOnlyWithNoCraftOnRecord()
        {
            Assert.Equal("active", new MakeCraftSafe("x").Target("active"));
            Assert.Null(new MakeCraftSafe("x").Target(""));
            Assert.Null(new MakeCraftSafe("x", null, 7).Target(null));
            Assert.Equal("craft", new MakeCraftSafe("x", "craft", 7).Target("active"));
        }

        [Fact]
        public void AFailedDebriefInABlackoutMakesTheCraftSafeAndWaitsForTheLink()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            mission.Send(Snapshot(s => s.HasLink = false));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);
            Assert.Single(mission.TakeCommands().OfType<MakeCraftSafe>());
            Assert.True(mission.View.CanEndMission);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.Debrief);
            Assert.DoesNotContain(QuickReply.AcceptReducedGoal, mission.View.QuickReplies);

            // With the link back it still waits on the player, with End Mission and Retry with a note.
            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.Equal(MissionStage.AwaitingReducedGoal, mission.View.Stage);
            Assert.Equal(new[] { QuickReply.RetryWithNote, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal("Failed", mission.View.Log.Single(e => e.Kind == LogKind.Debrief).Outcome);
        }

        [Fact]
        public void AFailedDebriefWaitingForTheLinkStillWaitsAfterALoadUntilTheCraftIsSeenLinked()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-failed.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            mission.Send(Snapshot(s => s.HasLink = false));
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingReducedGoal && !v.Busy);

            // The game is quit and the save loaded again: the link isn't known until the craft is seen.
            var states = mission.ExportStates().ToList();
            var client = new ClaudeClient(new CraftCheckRouter(new StreamingFakeTransport()), () => "sk-ant-test", l => { });
            var loaded = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            loaded.Send(new MissionsRestored(_save.Path, states));
            loaded.Send(new SceneChanged(GameScene.Flight));
            Assert.Equal(MissionStage.AwaitingReducedGoal, loaded.View.Stage);
            loaded.Send(Snapshot(s => s.HasLink = false));
            Assert.Equal(MissionStage.AwaitingReducedGoal, loaded.View.Stage);

            loaded.Send(Snapshot(s => s.HasLink = true));
            Assert.Equal(MissionStage.AwaitingReducedGoal, loaded.View.Stage);
            Assert.Contains(QuickReply.EndMission, loaded.View.QuickReplies);
        }

        // ---- Custom Scripts ----

        [Fact]
        public void InABlackoutACustomScriptFliesUnderTheSameScriptCheck()
        {
            var check = new QuicksaveCheck();
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefGoalNotMet(),
                ScriptedResponse.Fixture("mission-sequence-plan-unsafe.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-rewritten.sse"));
            var mission = Control(transport, check: check);
            var start = ToRunning(mission);
            mission.Send(Snapshot(s => s.HasLink = false));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            // The plan that failed the check never ran; its rewrite, a Custom Script, passed and ran without Approval.
            var started = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.DoesNotContain("QUICKSAVE", started.Source.ToUpperInvariant());
            Assert.Equal(5, transport.Requests.Count);

            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.True(mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).CustomScript);
        }

        [Fact]
        public void InABlackoutAScriptThatFailsTheCheckTwiceWaitsForTheLink()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                DebriefGoalNotMet(),
                ScriptedResponse.Fixture("mission-sequence-plan-unsafe.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-unsafe.sse"));
            var mission = Control(transport, check: new QuicksaveCheck());
            var start = ToRunning(mission);
            mission.Send(Snapshot(s => s.HasLink = false));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript && !v.Busy);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Empty(mission.View.QuickReplies.Intersect(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan }));

            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan }, mission.View.QuickReplies);
        }

        // ---- Link loss in Manual Control (#130) ----

        [Fact]
        public void LinkLossInManualControlWithNoOtherCrewFollowsTheBlackoutRules()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-debrief-manual.sse"),
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"));
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(new TakeManualControl());
            mission.TakeCommands();
            var ut = OnPad().Ut;

            mission.Send(Snapshot(s => { s.HasLink = false; s.Ut = ut + 1; }));
            Assert.True(mission.View.Blackout);
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            mission.Send(Snapshot(s => { s.HasLink = false; s.Ut = ut + 7; }));
            Assert.Single(mission.TakeCommands().OfType<MakeCraftSafe>());

            // Claudia takes the craft back, Debriefs the stretch, replans and flies on, all without the player.
            DrainUntil(mission, v => v.Stage == MissionStage.Running);
            Assert.Single(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(5, transport.Requests.Count);
            Assert.DoesNotContain(QuickReply.Abort, mission.View.QuickReplies);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.Debrief);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.MissionPlan);

            mission.Send(Snapshot(s => { s.HasLink = true; s.Ut = ut + 8; }));
            var revisions = mission.View.Log.Single(e => e.Title == "While there was no link").Text;
            Assert.Contains("- Mission Plan revised: The goal only needs the kOS link proven; nothing has to fly.", revisions);
            Assert.Contains("- Sequence Plan \"Link check\" flown without Approval: ", revisions);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Debrief);
        }

        // ---- An Abort recommendation (#120) ----

        [Fact]
        public void AnAbortRecommendationOutsideABlackoutNeedsThePlayerAndHoldsTheScriptsWarpUntilTheyDecide()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-alert-abort.sse"));
            var mission = Control(transport);
            ToRunning(mission);
            var watch = new AttentionWatch();
            watch.Observe(mission.View);

            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            DrainUntil(mission, v => v.AbortRecommended.Length > 0);
            // Replies arrive on the background drain; the next input checks the script's warp.
            mission.Send(new UtAdvanced(OnPad().Ut + 1));

            var attention = watch.Observe(mission.View);
            Assert.Equal(AttentionKind.Abort, attention.Kind);
            var commands = mission.TakeCommands();
            Assert.Contains(commands, c => c is DropWarp);
            Assert.Equal("Claudia recommends Abort", commands.OfType<HoldScriptWarp>().Last().Reason);
            Assert.Equal(QuickReply.Abort, mission.View.QuickReplies.First());

            mission.Send(new AbortSequence());
            Assert.Equal("", mission.TakeCommands().OfType<HoldScriptWarp>().Single().Reason);
        }

        [Fact]
        public void AnAbortVerdictThatComesBackInABlackoutIsNotOffered()
        {
            var alert = ScriptedResponse.Fixture("mission-alert-abort.sse", 64);
            alert.PauseAt = 100;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                alert);
            var mission = Control(transport);
            ToRunning(mission);
            var watch = new AttentionWatch();
            watch.Observe(mission.View);

            mission.Send(Snapshot(s => s.Stages[0].DeltaVVac = 3100));
            mission.Send(Snapshot(s => { s.HasLink = false; s.Stages[0].DeltaVVac = 3100; }));
            alert.Gate.Set();
            DrainUntil(mission, v => !v.Busy);
            mission.Send(new UtAdvanced(OnPad().Ut + 1));

            Assert.Equal("", mission.View.AbortRecommended);
            Assert.DoesNotContain(QuickReply.Abort, mission.View.QuickReplies);
            Assert.Null(watch.Observe(mission.View));
            Assert.DoesNotContain(mission.TakeCommands().OfType<HoldScriptWarp>(), h => h.Reason == "Claudia recommends Abort");
        }

        // ---- Helpers ----

        private static string AllText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return string.Join("\n", doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()));
        }

        private static string DeltaVCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"delta_v_used\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Nothing should burn.\"}],";
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

        /// <summary>Fails any script that quicksaves, the way the real check would.</summary>
        private sealed class QuicksaveCheck : IScriptCheck
        {
            public ScriptCheckResult Check(string script)
            {
                return script.ToUpperInvariant().Contains("QUICKSAVE")
                    ? new ScriptCheckResult(null, new List<string> { "line 1: :QUICKSAVE (quicksave)" })
                    : ScriptCheckResult.Pass();
            }
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null, IScriptCheck check = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, check ?? new PassingScriptCheck());
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
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private StartScript ToRunning(MissionControl mission, decimal cap = 10m)
        {
            ToSequencePlan(mission, cap);
            mission.Send(new Approve());
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

        /// <summary>The text of the last user message in a request: the facts the turn gave Claudia.</summary>
        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }
    }
}
