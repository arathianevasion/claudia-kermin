// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
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
    /// Child Vessels through the Mission seam (#65): the Mission Plan names them and each Sequence Plan its vessel; a script
    /// goes to a Child Vessel only once it has separated and has a CommNet path from Claudia, and keeps running if the link
    /// drops; the vessel left behind must be stable; unplanned vessels are ignored until the plan is revised to use them;
    /// a notice says what left the craft; and when the Mission ends they are ordinary vessels.
    /// </summary>
    public sealed class ChildVesselMissionTests : IDisposable
    {
        private const string Goal = "Release the probe into orbit and fly it to the Mun.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Claudia's replies ----

        private static ScriptedResponse MissionPlan(string childVessels = "[{\"name\":\"Probe\",\"separation_stage\":2,\"vessel_id\":\"\"}]", string secondVessel = "Probe")
        {
            return Mothership.Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: release the probe, then fly it.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"The probe flies on alone.\",\"sequences\":["
                + "{\"name\":\"Release\",\"intent\":\"Release the probe.\",\"delta_v\":0,\"timing\":\"right away\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"},"
                + "{\"name\":\"Probe burn\",\"intent\":\"Raise the probe's orbit.\",\"delta_v\":100,\"timing\":\"after release\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"" + secondVessel + "\"}],"
                + "\"total_delta_v\":100,\"expected_duration\":\"an hour\",\"mission_time_s\":-1,\"child_vessels\":" + childVessels + "},"
                + "\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse SequencePlan(string name, string vessel)
        {
            return Mothership.Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: " + name + ".\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"" + name + "\",\"intent\":\"Fly it.\",\"why\":\"It is next.\",\"target\":\"done\",\"tolerances\":\"none\","
                + "\"expected_delta_v\":10,\"expected_duration_s\":60,\"abort_conditions\":[],\"telemetry_checks\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1,\"manual\":false,\"vessel\":\"" + vessel + "\"},\"debrief\":null}");
        }

        private static ScriptedResponse Debrief(bool goalReached)
        {
            return Mothership.Reply("{\"kind\":\"debrief\",\"say\":\"Debrief: Nominal.\",\"reply_intent\":\"none\",\"mission_plan\":null,\"sequence_plan\":null,"
                + "\"debrief\":{\"outcome\":\"Nominal\",\"why\":\"It went to plan.\",\"what_happened\":\"Done.\",\"goal_reached\":" + (goalReached ? "true" : "false")
                + ",\"next\":\"\",\"eva_science\":\"\"}}");
        }

        // ---- Harness ----

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
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

        private static string Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").GetRawText();
        }

        private static SeenVessel CraftSeen()
        {
            var craft = new SeenVessel { Id = "craft", Name = "Pad Test", ClaudiaAboard = true, HasKosProcessor = true, Situation = "ORBITING", LinkedHome = true };
            craft.ProcessorPartIds.Add(3102);
            craft.PartTitles.AddRange(new[] { "Mk1 Command Pod", "CX-4181 Scriptable Control System" });
            return craft;
        }

        private static SeenVessel ProbeSeen(bool linked = true, string situation = "ORBITING", string id = "probe", string name = "Pad Test Probe", uint processor = Mothership.ProbeCore)
        {
            var probe = new SeenVessel { Id = id, Name = name, HasKosProcessor = true, Situation = situation, LinkedHome = linked };
            probe.ProcessorPartIds.Add(processor);
            probe.PartTitles.AddRange(new[] { "TT-38K Radial Decoupler", "Probodobodyne OKTO", "48-7S \"Spark\"" });
            if (linked) probe.LinkedTo.Add("craft");
            return probe;
        }

        /// <summary>Starts the Mission on the mothership and approves its Mission Plan; returns at the first Sequence Plan.</summary>
        private void ToFirstSequencePlan(MissionControl mission, GameSnapshot craft = null)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, craft ?? Mothership.WithProbe(), new StartMission(Goal, 10m, Sonnet, false));
            mission.Send(craft ?? Mothership.WithProbe());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        /// <summary>Flies "Release" on the craft and Debriefs it; returns with the probe's Sequence Plan awaiting Approval.</summary>
        private void ToProbeSequence(MissionControl mission)
        {
            ToFirstSequencePlan(mission);
            mission.Send(new Approve());
            var release = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.Equal(3102u, release.ProcessorPartId);
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), release.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
        }

        // ---- The plan names them ----

        [Fact]
        public void TheMissionPlanNamesItsChildVesselsAndEachSequencesVessel()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"));
            var mission = Control(transport);
            ToFirstSequencePlan(mission);

            // Claudia was told what could separate, by stage, to name her Child Vessels.
            Assert.Contains("Separations that could be Child Vessels", Messages(transport.Requests[0]));
            var card = mission.View.Log.Last(e => e.Kind == LogKind.MissionPlan);
            Assert.Contains("2. Probe burn [Probe]", card.Text);
            Assert.Contains("Child Vessels: Probe (separates at stage 2)", card.Text);
            var child = Assert.Single(mission.View.ChildVessels);
            Assert.Equal("Probe", child.Name);
            Assert.False(child.Separated);
        }

        [Fact]
        public void AChildVesselAtAStageWhereNothingWithAProcessorSeparatesIsFlaggedWhenThePlanIsOffered()
        {
            var transport = new StreamingFakeTransport(MissionPlan("[{\"name\":\"Probe\",\"separation_stage\":7,\"vessel_id\":\"\"}]"));
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, Mothership.WithProbe(), new StartMission(Goal, 10m, Sonnet, false));
            mission.Send(Mothership.WithProbe());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Child Vessel Probe: nothing with a kOS processor separates at stage 7"));
        }

        // ---- Flying one ----

        [Fact]
        public void AScriptGoesToAChildVesselOnlyOnceItHasSeparatedAndOverACommNetPathFromClaudia()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"), Debrief(false), SequencePlan("Probe burn", "Probe"), Debrief(true));
            var mission = Control(transport);
            ToProbeSequence(mission);
            Assert.Contains("Flies the Child Vessel Probe", mission.View.Log.Last(e => e.Kind == LogKind.SequencePlan).Text);

            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Contains(mission.View.Log, e => e.Text == "Can't run the script: Probe hasn't separated from the craft yet.");

            // It separates, but has no path to the craft Claudia is aboard.
            mission.Send(new VesselsSeparated(CraftSeen(), ProbeSeen(linked: false), false));
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Separated: Pad Test Probe left the craft with 3 parts") && e.Text.EndsWith("It is the planned Child Vessel Probe."));
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen(linked: false) }));
            Assert.True(mission.View.ChildVessels.Single().Separated);
            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Can't run the script: there is no CommNet path from Claudia to Probe (from the craft)"));

            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen() }));
            mission.Send(new Approve());
            var script = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.Equal(Mothership.ProbeCore, script.ProcessorPartId);
            Assert.Equal("Running: Probe burn on Probe", mission.View.RunStatus);
            Assert.Equal("probe", mission.View.RunningChildVesselId);

            // The link drops: the started script carries on.
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen(linked: false) }));
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), script.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var debrief = Messages(transport.Requests[4]);
            Assert.Contains("This sequence flew the Child Vessel", debrief);
            // The separation notice went to Claudia with her next turn.
            Assert.Contains("a vessel separated from the craft", debrief);
            Assert.Contains("vessel id probe, with a kOS processor", debrief);
        }

        [Fact]
        public void TheVesselLeftBehindMustBeStable()
        {
            // After the release, Claudia's next sequence flies the craft on.
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"), Debrief(false), SequencePlan("Circularize", "craft"));
            var mission = Control(transport);
            ToProbeSequence(mission);

            // The probe is falling back: the craft can't be flown away and leave it so.
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen(situation: "SUB_ORBITAL") }));
            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Contains(mission.View.Log, e => e.Text == "Can't run the script: Probe would be left behind SUB_ORBITAL; it must be stable (in orbit, landed or splashed down) first.");

            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen() }));
            mission.Send(new Approve());
            Assert.Equal(3102u, mission.TakeCommands().OfType<StartScript>().Single().ProcessorPartId);
            Assert.Equal("Running: Circularize", mission.View.RunStatus);
        }

        [Fact]
        public void ARunOnAChildVesselIsInterruptedWhenItUnloadsAndItsControlsAreClaudiasMeanwhile()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"), Debrief(false), SequencePlan("Probe burn", "Probe"));
            var mission = Control(transport);
            ToProbeSequence(mission);
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen() }));
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            // The player switches to the probe: its controls are locked while Claudia flies it.
            var probe = Mothership.WithProbe();
            probe.Craft.VesselId = "probe";
            probe.Crew.Clear();
            mission.Send(probe);
            Assert.Equal(ControlLocks.Craft | ControlLocks.Warp, mission.View.Locks);

            mission.Send(new VesselUnloaded("probe"));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.Contains("Probe unloaded: it went out of range of the player's vessel"));
        }

        // ---- Only vessels that left the craft (#192) ----

        private const string NeverLeft = "[{\"name\":\"Bystander\",\"separation_stage\":null,\"vessel_id\":\"bystander\"}]";

        private MissionControl StartedWith(StreamingFakeTransport transport)
        {
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, Mothership.WithProbe(), new StartMission(Goal, 10m, Sonnet, false));
            mission.Send(Mothership.WithProbe());
            return mission;
        }

        [Fact]
        public void AMissionPlanNamingAVesselThatNeverLeftTheCraftGoesBackToClaudiaWithTheReason()
        {
            var transport = new StreamingFakeTransport(MissionPlan(NeverLeft, "Bystander"), MissionPlan("[]", "craft"));
            var mission = StartedWith(transport);
            // A vessel the mod sees in Flight, but which never left the craft.
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen(id: "bystander", name: "Bystander") }));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("no such vessel has left the craft", Messages(transport.Requests[1]));
            Assert.Contains("bystander", Messages(transport.Requests[1]));
            Assert.Empty(mission.View.ChildVessels);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("The probe flies on alone."));
        }

        [Fact]
        public void AMissionPlanNamingAVesselThatLeftTheCraftIsOffered()
        {
            var revised = MissionPlan("[{\"name\":\"Probe\",\"separation_stage\":null,\"vessel_id\":\"probe\"}]");
            var transport = new StreamingFakeTransport(MissionPlan("[]", "craft"), revised);
            var mission = StartedWith(transport);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new VesselsSeparated(ProbeSeen(), CraftSeen(), false));
            mission.Send(new ChangeSomething("Fly the probe."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && v.ChildVessels.Count == 1);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("Probe", mission.View.ChildVessels.Single().Name);
        }

        [Fact]
        public void IfTheCorrectedMissionPlanStillNamesAVesselThatNeverLeftTheMissionPauses()
        {
            var transport = new StreamingFakeTransport(MissionPlan(NeverLeft, "Bystander"), MissionPlan(NeverLeft, "Bystander"));
            var mission = StartedWith(transport);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("has not left the craft", mission.View.PauseReason);
            Assert.Empty(mission.View.ChildVessels);
        }

        [Fact]
        public void TheIdsOfVesselsThatLeftTheCraftAreKeptWithTheMissionInTheSave()
        {
            var transport = new StreamingFakeTransport(MissionPlan("[]", "craft"), SequencePlan("Release", "craft"));
            var mission = Control(transport);
            ToFirstSequencePlan(mission);
            mission.Send(new VesselsSeparated(ProbeSeen(id: "left-1"), CraftSeen(), false));
            var state = Assert.Single(mission.ExportStates());
            Assert.Equal("[\"left-1\"]", state.LeftTheCraft);
            var saved = MissionSaveState.FromValues(state.ToValues());

            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new SceneChanged(GameScene.Flight));
            reloaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            Assert.Equal("[\"left-1\"]", Assert.Single(reloaded.ExportStates()).LeftTheCraft);
        }

        // ---- Unplanned vessels ----

        [Fact]
        public void AnUnplannedSeparatedVesselIsIgnoredUntilClaudiaRevisesTheMissionPlanToUseIt()
        {
            var plan = MissionPlan("[]", "craft");
            var revised = MissionPlan("[{\"name\":\"Stray\",\"separation_stage\":-1,\"vessel_id\":\"stray\"}]", "Stray");
            var transport = new StreamingFakeTransport(plan, revised, SequencePlan("Release", "craft"), Debrief(false), SequencePlan("Stray burn", "Stray"));
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, Mothership.WithProbe(), new StartMission(Goal, 10m, Sonnet, false));
            mission.Send(Mothership.WithProbe());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Empty(mission.View.ChildVessels);

            // A vessel with a processor leaves the craft; the plan doesn't name it.
            var stray = ProbeSeen(id: "stray", name: "Stray Probe", processor: 4001);
            mission.Send(new VesselsSeparated(stray, CraftSeen(), false));
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), stray }));
            Assert.Contains(mission.View.Log, e => e.Text.Contains("isn't a planned Child Vessel, so Claudia ignores it"));
            Assert.Empty(mission.View.ChildVessels);

            // Claudia revises the Mission Plan to use it, by the id the notice gave her.
            mission.Send(new ChangeSomething("Use the stray probe too."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval && v.ChildVessels.Count == 1);
            Assert.Contains("vessel id stray", Messages(transport.Requests[1]));
            Assert.True(mission.View.ChildVessels.Single().Separated);

            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var release = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), release.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(4001u, mission.TakeCommands().OfType<StartScript>().Single().ProcessorPartId);
        }

        [Fact]
        public void ASequencePlanForAVesselTheMissionPlanDoesNotNameIsRefused()
        {
            var transport = new StreamingFakeTransport(MissionPlan("[]", "craft"), SequencePlan("Stray burn", "Stray"));
            var mission = Control(transport);
            ToFirstSequencePlan(mission);
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen(id: "stray", name: "Stray") }));
            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Can't run the script: \"Stray\" isn't a Child Vessel in the Mission Plan"));
        }

        // ---- What left the craft ----

        [Fact]
        public void UndockingSendsANoticeOfWhatLeftTheCraftButDebrisDoesNot()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"));
            var mission = Control(transport);
            ToFirstSequencePlan(mission);
            var before = mission.View.Log.Count;

            var booster = new SeenVessel { Id = "booster", Name = "Pad Test Debris" };
            booster.PartTitles.Add("FL-T400 Fuel Tank");
            mission.Send(new VesselsSeparated(CraftSeen(), booster, false));
            Assert.Equal(before, mission.View.Log.Count);

            var tanker = new SeenVessel { Id = "tanker", Name = "Tanker" };
            tanker.PartTitles.AddRange(new[] { "Clamp-O-Tron Docking Port", "FL-T400 Fuel Tank", "FL-T400 Fuel Tank" });
            mission.Send(new VesselsSeparated(CraftSeen(), tanker, true));
            Assert.Equal("Undocked: Tanker left the craft with 3 parts (Clamp-O-Tron Docking Port, 2x FL-T400 Fuel Tank). ", mission.View.Log.Last().Text);
        }

        // ---- The end of the Mission ----

        [Fact]
        public void WhenTheMissionEndsItsChildVesselsAreOrdinaryVesselsAgain()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"), Debrief(true));
            var mission = Control(transport);
            ToFirstSequencePlan(mission);
            mission.Send(new Approve());
            var release = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new VesselsSeparated(CraftSeen(), ProbeSeen(), false));
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), release.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            Assert.Empty(mission.View.ChildVessels);
            Assert.Equal("", mission.View.RunningChildVesselId);
            var before = mission.View.Log.Count;
            mission.Send(new VesselsSeparated(CraftSeen(), ProbeSeen(id: "other", processor: 5001), false));
            Assert.Equal(before, mission.View.Log.Count);
            Assert.Equal(ControlLocks.None, mission.View.Locks);
        }

        // ---- Persistence ----

        [Fact]
        public void ChildVesselsAreKeptWithTheMissionInTheSave()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"));
            var mission = Control(transport);
            ToFirstSequencePlan(mission);
            mission.Send(new VesselsSeparated(CraftSeen(), ProbeSeen(), false));
            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal("Probe", saved.MissionPlan.ChildVessels.Single().Name);
            Assert.Equal(2, saved.MissionPlan.ChildVessels.Single().SeparationStage);
            Assert.Equal("craft", saved.MissionPlan.Sequences[0].Vessel);

            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new SceneChanged(GameScene.Flight));
            reloaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            reloaded.Send(Mothership.WithProbe());
            var child = Assert.Single(reloaded.View.ChildVessels);
            Assert.Equal("Probe", child.Name);
            Assert.True(child.Separated);
        }

        [Fact]
        public void TheChildVesselTextOnThePlanCardsIsTheSameInTheLogRebuiltAfterALoad()
        {
            var transport = new StreamingFakeTransport(MissionPlan(), SequencePlan("Release", "craft"), Debrief(false), SequencePlan("Probe burn", "Probe"), Debrief(true));
            var mission = Control(transport);
            ToProbeSequence(mission);
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen() }));
            mission.Send(new Approve());
            var script = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false), script.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var live = mission.View.Log.Where(e => e.Kind == LogKind.MissionPlan || e.Kind == LogKind.SequencePlan).Select(e => e.Title + "|" + e.Text).ToList();
            Assert.Contains(live, c => c.Contains("2. Probe burn [Probe]") && c.Contains("Child Vessels: Probe (separates at stage 2)"));
            Assert.Contains(live, c => c.StartsWith("Probe burn|") && c.Contains("Flies the Child Vessel Probe"));
            Assert.DoesNotContain(live, c => c.StartsWith("Release|") && c.Contains("Flies the Child Vessel"));

            // A quit and a fresh game over the same save: the cards are built again from the transcript.
            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new MissionsRestored(_save.Path, mission.ExportStates()));
            Assert.Equal(MissionStage.Succeeded, reloaded.View.Stage);
            Assert.Equal(live, reloaded.View.Log.Where(e => e.Kind == LogKind.MissionPlan || e.Kind == LogKind.SequencePlan).Select(e => e.Title + "|" + e.Text).ToList());
        }

        [Fact]
        public void AMissionPlanWithBothANewGoalAndChildVesselsParsesIsSavedAndRestored()
        {
            const string newGoal = "Release the probe and fly it past the Mun.";
            var plan = Mothership.Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: release the probe.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"The probe flies on alone.\",\"sequences\":["
                + "{\"name\":\"Release\",\"intent\":\"Release the probe.\",\"delta_v\":0,\"timing\":\"right away\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"},"
                + "{\"name\":\"Probe burn\",\"intent\":\"Raise the probe's orbit.\",\"delta_v\":100,\"timing\":\"after release\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"Probe\"}],"
                + "\"total_delta_v\":100,\"expected_duration\":\"an hour\",\"mission_time_s\":-1,\"new_goal\":\"" + newGoal + "\","
                + "\"child_vessels\":[{\"name\":\"Probe\",\"separation_stage\":2,\"vessel_id\":\"\"}]},"
                + "\"sequence_plan\":null,\"debrief\":null}");
            var mission = Control(new StreamingFakeTransport(plan));
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, Mothership.WithProbe(), new StartMission(Goal, 10m, Sonnet, false));
            mission.Send(Mothership.WithProbe());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal(newGoal, saved.MissionPlan.NewGoal);
            Assert.Equal("Probe", saved.MissionPlan.ChildVessels.Single().Name);
            Assert.Equal(2, saved.MissionPlan.ChildVessels.Single().SeparationStage);
            Assert.Equal("Probe", saved.MissionPlan.Sequences[1].Vessel);
            Assert.Equal(Goal, saved.Goal);

            var reloaded = Control(new StreamingFakeTransport());
            reloaded.Send(new SceneChanged(GameScene.Flight));
            reloaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            reloaded.Send(Mothership.WithProbe());
            Assert.Equal("Probe", Assert.Single(reloaded.View.ChildVessels).Name);
            Assert.Equal(Goal, reloaded.View.Goal);
            reloaded.Send(new Approve());
            Assert.Equal(newGoal, reloaded.View.Goal);
        }

        [Fact]
        public void TheSaveCarriesBothAReducedGoalAndChildVessels()
        {
            const string children = "[{\"name\":\"Probe\",\"vessel_id\":\"probe-1\",\"vessel_name\":\"Probe\",\"separated\":true,\"processors\":[7]}]";
            var state = new MissionSaveState { Id = "m1", ReducedGoal = "Reach a stable orbit of Kerbin.", ChildVessels = children };

            var saved = MissionSaveState.FromValues(state.ToValues());

            Assert.Equal("Reach a stable orbit of Kerbin.", saved.ReducedGoal);
            Assert.Equal(children, saved.ChildVessels);
        }
    }
}
