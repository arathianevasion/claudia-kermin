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
    /// Vessel switching and docking (#64, with #124 and #129) through the Mission seam: which vessel is the Mission's
    /// craft, far and nearby switches while Running, flying elsewhere between sequences, Claudia able to fly no vessel,
    /// docking, Resume from another vessel and the control locks on the craft only.
    /// </summary>
    public sealed class VesselSwitchingTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const uint Processor = 3102;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Helpers ----

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        /// <summary>The Mission's craft on the pad, with Claudia aboard and its processor in part 3102.</summary>
        private static GameSnapshot Craft(Action<GameSnapshot> change = null)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = "craft";
            if (change != null) change(snapshot);
            return snapshot;
        }

        /// <summary>Another vessel nearby: a splashed-down capsule with Jeb aboard, no Claudia and no kOS processor.</summary>
        private static GameSnapshot Elsewhere(string name = "Relay Station")
        {
            return Craft(s =>
            {
                s.Craft.VesselId = "relay";
                s.Craft.Name = name;
                s.Situation = "SPLASHED";
                s.Crew.Clear();
                s.Crew.Add(new SnapshotCrewMember { Name = "Jebediah Kerman", Trait = "Pilot" });
                s.Craft.Parts.RemoveAll(p => p.HasKosProcessor);
            });
        }

        private static SeenVessel Vessel(string id, string name, bool claudia = false, bool eva = false, params uint[] processors)
        {
            var vessel = new SeenVessel { Id = id, Name = name, ClaudiaAboard = claudia, Eva = eva, HasKosProcessor = processors.Length > 0 };
            vessel.ProcessorPartIds.AddRange(processors);
            return vessel;
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

        private static string Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").GetRawText();
        }

        private void ToSequencePlan(MissionControl mission, bool remote = false)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, Craft(), new StartMission(Goal, 10m, Sonnet, remote));
            mission.Send(Craft());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
        }

        private StartScript ToRunning(MissionControl mission, bool remote = false)
        {
            ToSequencePlan(mission, remote);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true }, { "reason", "done" } }, "", "", false);
        }

        // ---- The rules ----

        [Fact]
        public void TheCraftFollowsClaudiaAboardAndTheOriginalProcessorRemotely()
        {
            var lander = Vessel("lander", "Lander", claudia: true, processors: 7);
            var probe = Vessel("probe", "Probe", processors: Processor);
            var vessels = new[] { probe, lander };

            Assert.Same(lander, MissionCraft.Follow(false, Processor, "craft", vessels));
            Assert.Same(probe, MissionCraft.Follow(true, Processor, "craft", vessels));
            Assert.Null(MissionCraft.Follow(true, 99, "craft", vessels));
            // A remote Mission with no processor on record (an older save) goes by the vessel id.
            Assert.Same(lander, MissionCraft.Follow(true, null, "lander", vessels));

            Assert.True(MissionCraft.Is(false, null, "", Craft()));
            Assert.False(MissionCraft.Is(false, null, "", Elsewhere()));
            Assert.True(MissionCraft.Is(true, Processor, "", Craft()));
            Assert.False(MissionCraft.Is(true, Processor, "", Elsewhere()));
        }

        [Fact]
        public void ClaudiaCanFlyOnlyAVesselWithAKosProcessorAndNotOnEva()
        {
            Assert.Equal("", MissionCraft.CannotFly(false, Vessel("a", "Lander", claudia: true, processors: 7)));
            Assert.Equal("Claudia is out on EVA", MissionCraft.CannotFly(false, Vessel("eva", "Claudia Kermin", claudia: true, eva: true)));
            Assert.Equal("Claudia is aboard Pod, which has no kOS processor", MissionCraft.CannotFly(false, Vessel("pod", "Pod", claudia: true)));
            Assert.Equal("Claudia isn't aboard any vessel", MissionCraft.CannotFly(false, null));
            Assert.Equal("", MissionCraft.CannotFly(true, Vessel("probe", "Probe", processors: Processor)));
            Assert.Equal("the Mission's kOS processor is no longer on any vessel", MissionCraft.CannotFly(true, null));
        }

        [Theory]
        [InlineData(2000, 2250, 10, 25.0)]
        [InlineData(2200, 2250, 10, 5.0)]
        [InlineData(2300, 2250, 10, 0.0)]
        [InlineData(2200, 2250, 0, null)]
        [InlineData(2200, 2250, -5, null)]
        public void SecondsToUnloadComeFromTheGapToTheUnloadDistanceAndHowFastItOpens(double distance, double unload, double rate, double? seconds)
        {
            Assert.Equal(seconds, MissionCraft.SecondsToUnload(distance, unload, rate));
            Assert.Equal(seconds.HasValue && seconds.Value <= 10, MissionCraft.UnloadsSoon(distance, unload, rate));
        }

        [Fact]
        public void ADockingNoticeListsWhatJoinedTheCraft()
        {
            var craft = Vessel("craft", "Mun Lander", claudia: true, processors: Processor);
            var tanker = Vessel("tanker", "Tanker");
            tanker.PartTitles.AddRange(new[] { "Clamp-O-Tron Docking Port", "FL-T400 Fuel Tank", "FL-T400 Fuel Tank" });

            Assert.Same(tanker, MissionCraft.Added(false, Processor, "craft", craft, tanker));
            Assert.Same(tanker, MissionCraft.Added(false, Processor, "craft", tanker, craft));
            Assert.Null(MissionCraft.Added(false, Processor, "craft", tanker, Vessel("other", "Other")));
            Assert.Equal("Docked with Tanker: the craft gained 3 parts (Clamp-O-Tron Docking Port, 2x FL-T400 Fuel Tank). "
                + "Claudia rechecks the craft's facts before her next plan.", MissionCraft.DockingLine(tanker));
        }

        // ---- A far switch while Running ----

        [Fact]
        public void AConfirmedFarSwitchInterruptsTheRunAndClaudiaDebriefsItBackInTheCraft()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            var script = ToRunning(mission);
            // Running: leaving the craft's range is asked about first (the adapter's guard reads this).
            Assert.True(mission.View.LeavingFlightNeedsConfirmation);
            Assert.Equal("craft", mission.View.CraftVesselId);

            mission.Send(new FarSwitchConfirmed("Relay Station"));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.Contains("switched to Relay Station, far enough away to unload the craft"));

            // The scene reloads on the far vessel: the program's late end is ignored, and no Debrief goes with its facts.
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Interrupted, null, "", "left Flight", false), script.RunId));
            mission.Send(Elsewhere());
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SwitchToVessel>());

            // Back in the craft, Claudia Debriefs it from the craft's own facts.
            mission.Send(Craft());
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var debrief = Messages(transport.Requests[2]);
            Assert.Contains("Interrupted (the player switched to Relay Station, far enough away to unload the craft)", debrief);
            Assert.Contains("PRELAUNCH", debrief);
            Assert.DoesNotContain("SPLASHED", debrief);
        }

        [Fact]
        public void TheFarSwitchReasonSurvivesTheReloadTheSwitchMakes()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToRunning(mission, remote: true);
            mission.Send(new FarSwitchConfirmed("Relay Station"));
            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal(Processor, saved.ProcessorPartId);
            Assert.Contains("Relay Station", saved.InterruptReason);

            var after = Replies("mission-debrief.sse");
            var reloaded = Control(after);
            reloaded.Send(new SceneChanged(GameScene.Flight));
            reloaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            reloaded.Send(Elsewhere());
            _background.Drain();
            Assert.Empty(after.Requests);

            reloaded.Send(Craft());
            DrainUntil(reloaded, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("the player switched to Relay Station", Messages(after.Requests[0]));
        }

        // ---- A nearby switch while Running ----

        [Fact]
        public void NearbyTheRunCarriesOnWithAWarningTenSecondsBeforeTheCraftUnloadsThenItIsInterrupted()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            var script = ToRunning(mission);

            mission.Send(Elsewhere());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            // The craft's last facts stay the Mission's, and nothing is locked in the other vessel.
            Assert.Equal(ControlLocks.None, mission.View.Locks);

            mission.Send(new CraftRange(1500, 2250, 10));
            Assert.Empty(mission.TakeCommands().OfType<PostScreenMessage>());
            mission.Send(new CraftRange(2160, 2250, 10));
            var warning = Assert.Single(mission.TakeCommands().OfType<PostScreenMessage>());
            Assert.Contains("unloads in about 9 s", warning.Text);
            Assert.True(warning.Always);
            mission.Send(new CraftRange(2200, 2250, 10));
            Assert.Empty(mission.TakeCommands().OfType<PostScreenMessage>());
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            mission.Send(new VesselUnloaded("craft"));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Interrupted, null, "", "the kOS processor was lost", false), script.RunId));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(Craft());
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("the craft unloaded", Messages(transport.Requests[2]));
        }

        [Fact]
        public void TheWarningComesAgainAfterThePlayerHeadsBackAndOutAgain()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            ToRunning(mission);
            mission.Send(Elsewhere());
            mission.Send(new CraftRange(2200, 2250, 10));
            mission.Send(new CraftRange(2150, 2250, -10));
            mission.Send(new CraftRange(2200, 2250, 10));
            Assert.Equal(2, mission.TakeCommands().OfType<PostScreenMessage>().Count(m => m.Text.Contains("unloads in about")));
        }

        [Fact]
        public void ARunThatEndsWithThePlayerElsewhereIsDebriefedWhenTheyAreBack()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            var mission = Control(transport);
            var script = ToRunning(mission);
            mission.Send(Elsewhere());

            mission.Send(new KosRunEnded(Finished(), script.RunId));
            _background.Drain();
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);

            mission.Send(Craft());
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("PRELAUNCH", Messages(transport.Requests[2]));
        }

        [Fact]
        public void ACraftThatUnloadsWhileThePlayerIsAboardOrNothingRunsIsLeftAlone()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            ToSequencePlan(mission);
            mission.Send(Elsewhere());
            mission.Send(new VesselUnloaded("craft"));
            mission.Send(new CraftRange(2200, 2250, 10));
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<PostScreenMessage>());
        }

        // ---- Between sequences ----

        [Fact]
        public void BetweenSequencesThePlayerFliesElsewhereAndTheModNeverSwitchesBack()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);
            Assert.NotEqual(ControlLocks.None, mission.View.Locks);

            mission.Send(Elsewhere());
            mission.Send(new UtAdvanced(5000));
            mission.Send(Elsewhere());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<SwitchToVessel>());
            // The other vessel is the player's: nothing is locked in it, and the plan isn't judged against it.
            Assert.Equal(ControlLocks.None, mission.View.Locks);
            Assert.Equal("", mission.View.PlanStale);
            Assert.DoesNotContain(QuickReply.ManualControl, mission.View.QuickReplies);

            mission.Send(Craft());
            Assert.NotEqual(ControlLocks.None, mission.View.Locks);
        }

        // ---- Which vessel is the craft ----

        [Fact]
        public void AboardTheCraftFollowsClaudiaToAnotherVessel()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            ToSequencePlan(mission);

            // Undocked: Claudia is in the lander, which has a processor of its own.
            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Mothership", processors: Processor), Vessel("lander", "Lander", claudia: true, processors: 7) }));
            Assert.Equal("lander", mission.View.CraftVesselId);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);

            // The player stayed in the mothership: it is another vessel now, so it isn't locked.
            mission.Send(Craft(s => s.Crew.Clear()));
            Assert.Equal(ControlLocks.None, mission.View.Locks);
            mission.Send(Craft(s => { s.Craft.VesselId = "lander"; s.Craft.Name = "Lander"; }));
            Assert.NotEqual(ControlLocks.None, mission.View.Locks);
            Assert.Equal("Lander", Assert.Single(mission.ExportStates()).VesselName);
        }

        [Fact]
        public void RemotelyTheCraftFollowsTheOriginalProcessor()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            ToSequencePlan(mission, remote: true);

            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Booster", claudia: true, processors: 7), Vessel("probe", "Probe", processors: Processor) }));
            Assert.Equal("probe", mission.View.CraftVesselId);

            // A snapshot without the processor is another vessel, whoever is aboard it.
            mission.Send(Craft(s => s.Craft.Parts.RemoveAll(p => p.Id == Processor)));
            Assert.Equal(ControlLocks.None, mission.View.Locks);
            mission.Send(Craft(s => s.Craft.VesselId = "probe"));
            Assert.NotEqual(ControlLocks.None, mission.View.Locks);
        }

        [Fact]
        public void WhenClaudiaCanFlyNoVesselTheMissionIsPausedUntilSheCanAndThePlayerResumes()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);

            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Craft", processors: Processor), Vessel("eva", "Claudia Kermin", claudia: true, eva: true) }));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains("Claudia is out on EVA", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.Resume }, mission.View.QuickReplies);

            // Still out: Resume is refused with the reason.
            mission.Send(Craft(s => { s.Craft.VesselId = "eva"; s.Craft.Parts.RemoveAll(p => p.HasKosProcessor); }));
            mission.Send(new Resume());
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Can't resume yet: Claudia can fly no vessel"));

            mission.Send(new VesselsSeen(new[] { Vessel("craft", "Craft", claudia: true, processors: Processor) }));
            mission.Send(Craft());
            mission.Send(new Resume());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void ARemoteMissionWhoseProcessorIsGoneIsPausedAndARequestWaitsForResume()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-eva.sse");
            var mission = Control(transport);
            var script = ToRunning(mission, remote: true);

            // The run ends and its Debrief goes out; meanwhile the processor's vessel is destroyed.
            mission.Send(new VesselsSeen(new[] { Vessel("booster", "Booster") }));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains("the Mission's kOS processor is no longer on any vessel", mission.View.PauseReason);
            Assert.Contains("End Mission", mission.View.PauseReason);
            mission.Send(new KosRunEnded(Finished(), script.RunId));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
        }

        // ---- Docking ----

        [Fact]
        public void DockingAddsANoticeAndThePendingPlanIsReplannedWithTheRecheckedFacts()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ToSequencePlan(mission);

            var tanker = Vessel("tanker", "Tanker");
            tanker.PartTitles.AddRange(new[] { "Clamp-O-Tron Docking Port", "FL-T400 Fuel Tank" });
            mission.Send(new VesselsDocked(Vessel("craft", "Craft", claudia: true, processors: Processor), tanker));

            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Docked with Tanker: the craft gained 2 parts (Clamp-O-Tron Docking Port, FL-T400 Fuel Tank)"));
            Assert.Equal("docked with Tanker", mission.View.PlanStale);
            Assert.Equal(QuickReply.Replan, mission.View.QuickReplies[0]);

            mission.Send(new Approve());
            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());

            mission.Send(new Replan());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var replan = Messages(transport.Requests[2]);
            Assert.Contains("the craft docked with another vessel", replan);
            Assert.Contains("FL-T400 Fuel Tank", replan);
            Assert.Equal("", mission.View.PlanStale);
        }

        [Fact]
        public void DockingThatDoesNotInvolveTheCraftIsIgnored()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            ToSequencePlan(mission);
            mission.Send(new VesselsDocked(Vessel("a", "Station"), Vessel("b", "Ferry")));
            Assert.Equal("", mission.View.PlanStale);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Docked with"));
        }

        // ---- Resume needs the craft (#124) ----

        [Fact]
        public void ResumeAfterEvaScienceIsRefusedFromTheEvaKerbalAndWorksFromTheCraft()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-eva.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            var script = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), script.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(new[] { QuickReply.Resume }, mission.View.QuickReplies);
            // Paused with nothing running: Claudia doesn't hold the craft, so nothing is locked for the EVA.
            Assert.Equal(ControlLocks.None, mission.View.Locks);

            // Jeb on EVA is the active vessel.
            mission.Send(Elsewhere("Jebediah Kerman"));
            mission.Send(new Resume());
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text == "Resume from the Mission's craft: you are flying Jebediah Kerman. Switch to " + Craft().Craft.Name + ", then Resume.");
            Assert.Equal(3, transport.Requests.Count);

            // Out of Flight it still waits for the craft.
            mission.Send(new SceneChanged(GameScene.Other));
            mission.Send(new Resume());
            Assert.Equal(MissionStage.Paused, mission.View.Stage);

            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Craft());
            mission.Send(new Resume());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(4, transport.Requests.Count);
            Assert.DoesNotContain("SPLASHED", Messages(transport.Requests[3]));
        }

        // ---- Control locks on the Mission's vessel (#129) ----

        [Fact]
        public void TheLocksAreOnTheCraftOnlyAndOnlyWhileClaudiaHoldsIt()
        {
            var mission = Control(Replies("mission-plan.sse", "mission-sequence-plan.sse"));
            var script = ToRunning(mission);
            Assert.NotEqual(ControlLocks.None, mission.View.Locks & ControlLocks.Warp);

            // Watching from a nearby vessel: the player's own vessel is free, warp keys too.
            mission.Send(Elsewhere());
            Assert.Equal(ControlLocks.None, mission.View.Locks);
            mission.Send(Craft());
            Assert.NotEqual(ControlLocks.None, mission.View.Locks & ControlLocks.Warp);

            // Before Flight (no craft yet) nothing is locked, even in Flight with a snapshot.
            var waiting = Control(Replies());
            waiting.Send(new SceneChanged(GameScene.Editor));
            CraftCheckRouter.StartChecked(waiting, _background, Craft(), new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.WaitingForFlight, waiting.View.Stage);
            Assert.Equal(ControlLocks.None, waiting.View.Locks);
            Assert.Equal("", waiting.View.CraftVesselId);
        }
    }
}
