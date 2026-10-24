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
    /// Manual Control and the control locks, driven through the Mission seam with hand-written streams: the state
    /// transitions (take, hand back, Debrief, revised Mission Plan), who may take the craft, run ids across an
    /// aborted run, Approve turning into Re-plan, and persistence.
    /// </summary>
    public sealed class ManualControlTests : IDisposable
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

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f, 64)).ToArray());
        }

        /// <summary>Start in Flight and run to the Mission Plan awaiting Approval.</summary>
        private void ToMissionPlan(MissionControl mission, decimal cap = 10m)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Pad());
            CraftCheckRouter.StartChecked(mission, _background, Pad(), new StartMission(Goal, cap, "claude-sonnet-5-5"));
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
            mission.TakeCommands();
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static string AllText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return string.Join("\n", doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()));
        }

        private static RunOutcome AbortedRun()
        {
            return new RunOutcome(RunOutcomeKind.Aborted, null, "", "the player took Manual Control", true);
        }

        private int PendingCharges(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.ReadAllLines(path).Count(l => l.Contains("\"event\":\"pending\""));
        }

        // ---- Locks ----

        [Fact]
        public void ClaudiaHoldsTheCraftLockedFromTheMissionStartAndWarpIsLockedOnlyWhileRunning()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            Assert.Equal(ControlLocks.None, mission.View.Locks);

            ToSequencePlan(mission);
            var craft = ControlLocks.Staging | ControlLocks.ActionGroups | ControlLocks.Sas | ControlLocks.Rcs | ControlLocks.Gear | ControlLocks.Brakes;
            Assert.Equal(craft, mission.View.Locks);
            Assert.Equal(ControlLocks.None, mission.View.Locks & ControlLocks.Warp);

            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal(craft | ControlLocks.Warp, mission.View.Locks);

            // Out of Flight nothing is locked: the locks are for the craft in Flight.
            mission.Send(new SceneChanged(GameScene.Other));
            Assert.Equal(ControlLocks.None, mission.View.Locks);
        }

        [Fact]
        public void ManualControlUnlocksTheCraftAndHandingBackLocksItAgain()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-manual.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(new TakeManualControl());
            Assert.Equal(ControlLocks.None, mission.View.Locks);

            mission.Send(new HandBack(""));
            Assert.NotEqual(ControlLocks.None, mission.View.Locks);
        }

        // ---- Taking the craft ----

        [Fact]
        public void TheButtonAbortsTheRunMakesSafeAndGivesThePlayerTheControls()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            Assert.Equal(new[] { QuickReply.ManualControl }, mission.View.QuickReplies);

            mission.Send(new TakeManualControl());

            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            var abort = Assert.IsType<AbortScript>(Assert.Single(mission.TakeCommands()));
            Assert.Equal(3102u, abort.ProcessorPartId);
            Assert.Equal(new[] { QuickReply.HandBack }, mission.View.QuickReplies);
            Assert.False(mission.View.LeavingFlightNeedsConfirmation);
            Assert.NotEqual(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void InputHeldOverASecondTakesTheCraftAndShorterInputDoesNot()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);

            mission.Send(new ManualInputHeld(0.4));
            mission.Send(new ManualInputHeld(1.0));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Empty(mission.TakeCommands());

            mission.Send(new ManualInputHeld(1.01));
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.IsType<AbortScript>(Assert.Single(mission.TakeCommands()));
        }

        [Fact]
        public void InputHeldWhileAPlanAwaitsApprovalTakesTheCraftWithNothingToAbort()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            mission.Send(new ManualInputHeld(1.5));

            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.DoesNotContain(mission.TakeCommands(), c => c is AbortScript || c is StartScript);
        }

        [Fact]
        public void InputHeldWhileClaudiaIsWritingAPlanIsIgnored()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);
            mission.Send(new Approve());
            Assert.True(mission.View.Busy);

            mission.Send(new ManualInputHeld(3));

            Assert.NotEqual(MissionStage.ManualControl, mission.View.Stage);
        }

        // ---- Who may take it ----

        [Theory]
        [InlineData(false, new string[0], false)]
        [InlineData(false, new[] { "Tourist" }, false)]
        [InlineData(false, new[] { "Pilot" }, true)]
        [InlineData(false, new[] { "Engineer", "Tourist" }, true)]
        [InlineData(true, new string[0], true)]
        public void ManualControlNeedsALinkOrAnotherNonTouristCrewMember(bool link, string[] otherCrew, bool allowed)
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(Pad(s =>
            {
                s.HasLink = link;
                foreach (var trait in otherCrew) s.Crew.Add(new SnapshotCrewMember { Name = "Kerbal " + trait, Trait = trait });
            }));

            Assert.Equal(allowed, mission.View.ManualControlAllowed);
            Assert.Equal(allowed, mission.View.QuickReplies.Contains(QuickReply.ManualControl));
            mission.Send(new TakeManualControl());
            mission.Send(new ManualInputHeld(5));

            Assert.Equal(allowed ? MissionStage.ManualControl : MissionStage.Running, mission.View.Stage);
            // Refused: the run is untouched and the flight controls stay locked.
            if (!allowed)
            {
                Assert.Empty(mission.TakeCommands());
                Assert.NotEqual(ControlLocks.None, mission.View.Locks);
                var refusals = mission.View.Log.Where(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Manual Control isn't available")).ToList();
                Assert.Single(refusals);
            }
        }

        [Fact]
        public void ARunWithNoLinkAndNoOtherCrewKeepsRunningUntilItEndsNormally()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            var start = ToRunning(mission);
            mission.Send(Pad(s => s.HasLink = false));

            mission.Send(new TakeManualControl());
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            var report = new Dictionary<string, object> { { "success", true }, { "reason", "link check" } };
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, report, "", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
        }

        // ---- A planned manual stretch ----

        [Fact]
        public void AManualStretchInTheSequencePlanStartsManualControlOnApprovalWithNoScript()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-manual.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.TakeCommands();

            var card = mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan);
            Assert.Equal("Hand docking", card.Title);
            Assert.Contains("Manual stretch", card.Text);
            Assert.Equal("", card.Script);
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);

            mission.Send(new Approve());

            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(ControlLocks.None, mission.View.Locks);
            Assert.True(mission.ExportStates().Single().ManualPlanned);
        }

        [Fact]
        public void ThePlayerSayingTheyWillFlyThisOneIsAChangeClaudiaAnswersWithAManualStretch()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-sequence-plan-manual.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);

            mission.Send(new ChangeSomething("I'll fly this one."));
            DrainUntil(mission, v => !v.Busy && v.Log.Any(e => e.Title == "Hand docking"));

            Assert.Contains("I'll fly this one.", AllText(transport.Requests[2]));
            Assert.Contains("manual true and an empty script", AllText(transport.Requests[2]));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            mission.TakeCommands();
            mission.Send(new Approve());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void AManualStretchCannotStartWithNoLinkAndNoOtherCrew()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-manual.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.Send(Pad(s => s.HasLink = false));

            mission.Send(new Approve());

            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Can't start the manual stretch"));
        }

        // ---- Handing back ----

        [Fact]
        public void HandingBackAfterATakeoverGivesAPlainAccountThenARevisedMissionPlanForApproval()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-manual.sse", "mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(new TakeManualControl());
            mission.TakeCommands();

            mission.Send(new HandBack("I burned retrograde by hand and I'm in a 90 km orbit."));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var debriefRequest = AllText(transport.Requests[2]);
            Assert.Contains("took Manual Control", debriefRequest);
            Assert.Contains("I burned retrograde by hand and I'm in a 90 km orbit.", debriefRequest);
            Assert.Contains("plain account of what changed", debriefRequest);
            Assert.DoesNotContain("Judge it against the Sequence Plan's target", debriefRequest);
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Off-Nominal", debrief.Outcome);

            Assert.Contains("Revise the Mission Plan", AllText(transport.Requests[3]));
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);
            Assert.Equal(4, transport.Requests.Count);

            // Nothing runs until the player approves the revised plan; then the loop is back on its usual path.
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void HandingBackAPlannedStretchIsJudgedAgainstItsTargetAndTheMessageIsOptional()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-manual.sse", "mission-debrief-manual.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);

            mission.Send(new HandBack("   "));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var debriefRequest = AllText(transport.Requests[2]);
            Assert.Contains("flew the manual stretch", debriefRequest);
            Assert.Contains("Docked within ten minutes", debriefRequest);
            Assert.Contains("Judge it against the Sequence Plan's target", debriefRequest);
            Assert.Contains("left no message", debriefRequest);
        }

        [Fact]
        public void HandBackIsIgnoredOutsideManualControl()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);

            mission.Send(new HandBack("hello"));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void TheHandBackRequestsGoThroughTheSpendingCapAndAHandBackThatWouldPassTheCapPausesTheMission()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-priced.sse", "mission-debrief-manual.sse", "mission-plan.sse");
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Pad());
            CraftCheckRouter.StartChecked(mission, _background, Pad(), new StartMission(Goal, 0.8m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            mission.Send(new TakeManualControl());

            mission.Send(new HandBack("done"));

            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Equal(4, PendingCharges(mission));
        }

        // ---- Run ids ----

        [Fact]
        public void LateEventsFromTheAbortedRunAreIgnoredWhateverRunComesNext()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-manual.sse", "mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            var first = ToRunning(mission);
            mission.Send(new TakeManualControl());

            // The aborted program's end arrives while the player flies: nothing happens.
            mission.Send(new KosRunEnded(AbortedRun(), first.RunId));
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(new HandBack(""));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
            mission.Send(new Approve());
            var second = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.NotEqual(first.RunId, second.RunId);

            // The first run's end, very late, can't end the second run; the second run's own end does.
            mission.Send(new KosRunEnded(AbortedRun(), first.RunId));
            mission.Send(new KosRunRefused("late", first.RunId));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal(5, transport.Requests.Count);
        }

        // ---- Approve becomes Re-plan ----

        [Fact]
        public void ChangingTheCraftBeyondTheSequencePlansToleranceTurnsApproveIntoReplan()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            Assert.Equal("", mission.View.PlanStale);

            // A small change in delta-v is inside the tolerance; losing a part is not.
            mission.Send(Pad(s => s.Stages[0].DeltaVVac *= 0.95));
            Assert.Equal("", mission.View.PlanStale);
            mission.Send(Pad(s => s.Craft.Parts.RemoveAt(s.Craft.Parts.Count - 1)));
            Assert.Contains("parts", mission.View.PlanStale);
            Assert.Equal(new[] { QuickReply.Replan, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);

            // Approve does nothing now: a stale plan never runs.
            mission.TakeCommands();
            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);

            mission.Send(new Replan());
            Assert.Equal(MissionStage.Sequencing, mission.View.Stage);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("pressed Re-plan", AllText(transport.Requests[2]));
            Assert.Equal("", mission.View.PlanStale);
            Assert.Equal(new[] { QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion }, mission.View.QuickReplies);
        }

        [Fact]
        public void ChangingTheCraftBeyondTheMissionPlansToleranceTurnsApproveIntoReplanToo()
        {
            var transport = Replies("mission-plan.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToMissionPlan(mission);

            mission.Send(Pad(s => s.Stages.ForEach(x => x.DeltaVVac *= 0.5)));
            Assert.Contains("delta-v", mission.View.PlanStale);
            Assert.Equal(QuickReply.Replan, mission.View.QuickReplies[0]);

            mission.Send(new Approve());
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Single(transport.Requests);

            mission.Send(new Replan());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && !v.Busy && v.PlanStale.Length == 0);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void ReplanOnAPlanThatHasNotChangedDoesNothing()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);

            mission.Send(new Replan());

            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
        }

        // ---- Link loss and saves ----

        [Fact]
        public void LosingTheLinkInManualControlWithNoOtherCrewTakesTheCraftBackAndMakesItSafe()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-manual.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(new TakeManualControl());
            mission.TakeCommands();

            mission.Send(Pad(s => s.HasLink = false));

            Assert.Contains(mission.TakeCommands(), c => c is MakeCraftSafe);
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains("link dropped", AllText(transport.Requests[2]));
        }

        [Fact]
        public void LosingTheLinkInManualControlWithAnotherCrewMemberAboardChangesNothing()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(new TakeManualControl());
            mission.TakeCommands();

            mission.Send(Pad(s =>
            {
                s.HasLink = false;
                s.Crew.Add(new SnapshotCrewMember { Name = "Jebediah Kerman", Trait = "Pilot" });
            }));

            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.Empty(mission.TakeCommands());
        }

        [Fact]
        public void ManualControlSurvivesASaveAndLoadWithItsPlannedStretchAndTheHandBackDebriefCarriesOn()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-manual.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            mission.Send(new Approve());
            var saved = mission.ExportStates();
            Assert.Equal(MissionStage.ManualControl, saved.Single().Stage);

            // The saved values survive the ConfigNode round trip.
            var again = MissionSaveState.FromValues(saved.Single().ToValues());
            Assert.True(again.ManualPlanned);
            Assert.Contains("Hand docking", again.ManualOrigin);

            var second = Replies("mission-debrief-manual.sse", "mission-plan.sse");
            var resumed = Control(second);
            resumed.Send(new SceneChanged(GameScene.Flight));
            resumed.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { again }));
            resumed.Send(Pad());
            Assert.Equal(MissionStage.ManualControl, resumed.View.Stage);
            Assert.Equal(new[] { QuickReply.HandBack }, resumed.View.QuickReplies);
            Assert.Equal(ControlLocks.None, resumed.View.Locks);

            resumed.Send(new HandBack("docked"));
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains("flew the manual stretch", AllText(second.Requests[0]));
        }

        [Fact]
        public void AHandBackDebriefOwedAfterALoadIsStillAHandBackDebriefAndRevisesThePlan()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-manual.sse", "mission-plan.sse");
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(new TakeManualControl());
            // Saved while the Debrief of the stretch is being asked for; the game then goes on and finishes the turn.
            mission.Send(new HandBack("by hand"));
            var saved = mission.ExportStates().Single();
            Assert.True(saved.HandBack);
            Assert.Equal("by hand", saved.HandBackMessage);
            Assert.Equal(MissionStage.Debriefing, saved.Stage);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var second = Replies("mission-debrief-manual.sse", "mission-plan.sse");
            var resumed = Control(second);
            resumed.Send(new SceneChanged(GameScene.Flight));
            resumed.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { MissionSaveState.FromValues(saved.ToValues()) }));
            resumed.Send(Pad());
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Contains("took Manual Control", AllText(second.Requests[0]));
            Assert.Contains("by hand", AllText(second.Requests[0]));
            Assert.Contains("Revise the Mission Plan", AllText(second.Requests[1]));
        }
    }
}
