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
    /// Crewed (aboard) versus remote Missions, Claudia Lost, and EVA science, through the core's one seam: Game
    /// Snapshots and game events go in, the view model, adapter commands and the requests Claude received come out.
    /// </summary>
    public sealed class MissionAboardTests : IDisposable
    {
        private const string Goal = "Land on the Mun and take an EVA report.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Helpers ----

        private static GameSnapshot Snapshot(Action<GameSnapshot> change = null)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            if (change != null) change(snapshot);
            return snapshot;
        }

        private static SnapshotCrewMember Kerbal(string name)
        {
            return new SnapshotCrewMember { Name = name, Trait = "Pilot" };
        }

        /// <summary>A craft in the editor with <paramref name="seats"/> seats and the given crew in the manifest.</summary>
        private static GameSnapshot Editor(int seats, params string[] crew)
        {
            return Snapshot(s =>
            {
                s.Craft.Seats = seats;
                s.Crew.Clear();
                foreach (var name in crew) s.Crew.Add(name == "Claudia Kermin" ? new SnapshotCrewMember { Name = name, Trait = "SuperIntelligence" } : Kerbal(name));
                s.ClaudiaStanding = "Available";
            });
        }

        /// <summary>A Control whose Craft Check requests go to a transport of their own (the Mission's go to the router's).</summary>
        private MissionControl Control(StreamingFakeTransport missionTransport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(missionTransport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        /// <summary>A Control that sends every request, Craft Check included, to the one transport.</summary>
        private MissionControl Direct(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason
                + " check " + mission.View.CraftCheck.State + " " + mission.View.CraftCheck.Message);
        }

        private void Check(MissionControl mission, GameSnapshot snapshot, bool remote = false)
        {
            mission.Send(snapshot);
            mission.Send(new RunCraftCheck(Goal, 10m, Sonnet, remote));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
        }

        private static IList<AdapterCommand> Commands(MissionControl mission)
        {
            return mission.TakeCommands();
        }

        private static string Facts(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").GetRawText();
        }

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f, 64)).ToArray());
        }

        /// <summary>A Mission through its Mission Plan and Sequence Plan to a script Running in Flight.</summary>
        private MissionControl Running(StreamingFakeTransport transport, bool remote, out StartScript script, GameSnapshot snapshot = null)
        {
            var mission = Control(transport);
            snapshot = snapshot ?? Snapshot();
            mission.Send(new SceneChanged(GameScene.Editor));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, Sonnet, remote));
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(new UtAdvanced(1203.48));
            mission.Send(snapshot);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.TakeCommands();
            mission.Send(new Approve());
            script = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission;
        }

        private static RunOutcome Finished()
        {
            var report = new Dictionary<string, object> { { "success", true }, { "reason", "done" }, { "situation", "LANDED" } };
            return new RunOutcome(RunOutcomeKind.Finished, report, "", "", false);
        }

        // ---- Seating Claudia, and the Craft Check's Blocker ----

        [Fact]
        public void ACrewedMissionStartedFromTheEditorSeatsClaudiaWhenSheIsNotAboard()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Direct(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            Check(mission, Editor(2));
            Commands(mission);

            mission.Send(new StartMission(Goal, 10m, Sonnet));

            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Single(Commands(mission).OfType<SeatClaudia>());
        }

        [Fact]
        public void ClaudiaIsNotSeatedAgainWhenSheIsAlreadyAboardOrTheMissionIsRemote()
        {
            var aboard = Direct(Replies("craft-check-pass.sse"));
            aboard.Send(new SceneChanged(GameScene.Editor));
            Check(aboard, Editor(2, "Claudia Kermin"));
            Commands(aboard);
            aboard.Send(new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.WaitingForFlight, aboard.View.Stage);
            Assert.Empty(Commands(aboard).OfType<SeatClaudia>());

            var remote = Direct(Replies("craft-check-pass.sse"));
            remote.Send(new SceneChanged(GameScene.Editor));
            Check(remote, Editor(2), remote: true);
            Commands(remote);
            remote.Send(new StartMission(Goal, 10m, Sonnet, remote: true));
            Assert.Equal(MissionStage.WaitingForFlight, remote.View.Stage);
            Assert.Empty(Commands(remote).OfType<SeatClaudia>());
        }

        [Fact]
        public void NoFreeSeatIsACraftCheckBlockerEvenWhenClaudeMissesItAndTheMissionDoesNotStart()
        {
            // The only seat is taken by another Kerbal, and Claude's reply (a Pass) says nothing about it.
            var mission = Direct(Replies("craft-check-pass.sse"));
            mission.Send(new SceneChanged(GameScene.Editor));
            Check(mission, Editor(1, "Valentina Kerman"));
            Commands(mission);

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftVerdict.Fail, check.Verdict);
            var blocker = Assert.Single(check.Findings);
            Assert.Equal("no_seat_for_claudia", blocker.Code);
            Assert.Equal(CraftFindingSeverity.Blocker, blocker.Severity);
            Assert.False(check.StartAllowed);

            mission.Send(new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(Commands(mission).OfType<SeatClaudia>());
        }

        [Fact]
        public void ACraftWithNoSeatsIsBlockedForACrewedMissionButNotForARemoteOne()
        {
            var crewed = Direct(Replies("craft-check-pass.sse"));
            crewed.Send(new SceneChanged(GameScene.Editor));
            Check(crewed, Editor(0));
            Assert.Equal("no_seat_for_claudia", Assert.Single(crewed.View.CraftCheck.Findings).Code);

            var remote = Direct(Replies("craft-check-pass.sse"));
            remote.Send(new SceneChanged(GameScene.Editor));
            Check(remote, Editor(0), remote: true);
            Assert.Equal(CraftVerdict.Pass, remote.View.CraftCheck.Verdict);
        }

        private static GameSnapshot RemoteCraft(Action<GameSnapshot> change)
        {
            var snapshot = Editor(0);
            change(snapshot);
            return snapshot;
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void ARemoteCraftWithoutAProbeCoreOrAnAntennaIsABlockerEvenWhenClaudeMissesIt(bool dropCore, bool dropAntenna)
        {
            // Claude's reply (a Pass) says nothing; the mod counts the parts itself.
            var mission = Direct(Replies("craft-check-pass.sse"));
            mission.Send(new SceneChanged(GameScene.Editor));
            Check(mission, RemoteCraft(s =>
            {
                if (dropCore) s.Craft.Parts.RemoveAll(p => p.Tags.Contains("probe_core"));
                if (dropAntenna) s.Craft.Parts.RemoveAll(p => p.Tags.Contains("antenna"));
            }), remote: true);

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftVerdict.Fail, check.Verdict);
            var blocker = Assert.Single(check.Findings);
            Assert.Equal("no_probe_core_or_antenna", blocker.Code);
            Assert.Equal(CraftFindingSeverity.Blocker, blocker.Severity);
            Assert.Equal(dropCore, blocker.Explanation.Contains("no probe core"));
            Assert.Equal(dropAntenna, blocker.Explanation.Contains("no antenna"));
            Assert.False(check.StartAllowed);
        }

        [Fact]
        public void AnAntennaWithNoTransmitterPowerDoesNotReachHomeAndCommNetOffNeedsNone()
        {
            var weak = Direct(Replies("craft-check-pass.sse"));
            weak.Send(new SceneChanged(GameScene.Editor));
            Check(weak, RemoteCraft(s => s.Craft.Parts.First(p => p.Tags.Contains("antenna")).AntennaPower = 0), remote: true);
            Assert.Equal("no_probe_core_or_antenna", Assert.Single(weak.View.CraftCheck.Findings).Code);

            var noCommNet = Direct(Replies("craft-check-pass.sse"));
            noCommNet.Send(new SceneChanged(GameScene.Editor));
            Check(noCommNet, RemoteCraft(s =>
            {
                s.CommNetEnabled = false;
                s.Craft.Parts.RemoveAll(p => p.Tags.Contains("antenna"));
            }), remote: true);
            Assert.Equal(CraftVerdict.Pass, noCommNet.View.CraftCheck.Verdict);
        }

        [Fact]
        public void ACrewedMissionNeedsNeitherAProbeCoreNorAnAntenna()
        {
            var mission = Direct(Replies("craft-check-pass.sse"));
            mission.Send(new SceneChanged(GameScene.Editor));
            var crewed = Editor(2, "Claudia Kermin");
            crewed.Craft.Parts.RemoveAll(p => p.Tags.Contains("probe_core") || p.Tags.Contains("antenna"));
            Check(mission, crewed);
            Assert.Equal(CraftVerdict.Pass, mission.View.CraftCheck.Verdict);
        }

        [Fact]
        public void OtherKerbalsRideAsPassengersAndDoNotBlockASeatThatIsFree()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Direct(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            Check(mission, Editor(2, "Valentina Kerman"));
            Commands(mission);

            Assert.Equal(CraftVerdict.Pass, mission.View.CraftCheck.Verdict);
            // Claude is told they are passengers, and how many seats are left for her.
            var facts = Facts(transport.Requests[0]);
            Assert.Contains("Mission type: crewed", facts);
            Assert.Contains("free: 1", facts);
            Assert.Contains("Kerbals aboard other than Claudia: 1 (they ride as passengers", facts);

            mission.Send(new StartMission(Goal, 10m, Sonnet));
            // She is seated; the command leaves the rest of the manifest alone.
            Assert.Single(Commands(mission).OfType<SeatClaudia>());
        }

        [Fact]
        public void TheCraftCheckTellsClaudeWhetherTheMissionIsCrewedOrRemoteAndWhetherTheCraftIsLinked()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Direct(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            Check(mission, Editor(0), remote: true);

            var facts = Facts(transport.Requests[0]);
            Assert.Contains("Mission type: remote", facts);
            Assert.Contains("has a link to Kerbin", facts);
        }

        // ---- Remote needs a link, crewed needs Claudia available ----

        [Fact]
        public void ARemoteMissionNeedsALinkOrTheLaunchPadToStart()
        {
            var transport = Replies();
            var mission = Direct(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot(s => { s.Situation = "ORBITING"; s.CommNetEnabled = true; s.HasLink = false; }));
            Commands(mission);

            mission.Send(new StartMission(Goal, 10m, Sonnet, remote: true));

            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Contains("CommNet link", mission.View.StartForm.Refusal);
            Assert.True(mission.View.StartForm.CanStart);
            Assert.Contains("CommNet link", Assert.Single(Commands(mission).OfType<PostScreenMessage>()).Text);
            // No Craft Check or any other request was spent on it.
            Assert.Empty(transport.Requests);
        }

        [Theory]
        [InlineData("ORBITING", true, true)]
        [InlineData("PRELAUNCH", true, false)]
        [InlineData("ORBITING", false, false)]
        public void ARemoteMissionStartsWithALinkOnThePadOrWithCommNetOff(string situation, bool commNet, bool link)
        {
            var mission = Direct(Replies("craft-check-pass.sse"));
            mission.Send(new SceneChanged(GameScene.Flight));
            var snapshot = Snapshot(s => { s.Situation = situation; s.CommNetEnabled = commNet; s.HasLink = link; });
            if (commNet && !link) Assert.Equal("PRELAUNCH", situation);
            mission.Send(snapshot);
            mission.Send(new StartMission(Goal, 10m, Sonnet, remote: true));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);

            Assert.Equal("", mission.View.StartForm.Refusal);
            Assert.NotEqual(MissionStage.None, mission.View.Stage);
            Assert.True(mission.View.Remote);
        }

        [Fact]
        public void ACrewedMissionCannotStartWhileClaudiaIsMissingButARemoteOneCan()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Direct(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(Snapshot(s => s.ClaudiaStanding = "Missing"));
            Commands(mission);

            mission.Send(new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Contains("Missing", mission.View.StartForm.Refusal);
            Assert.Empty(transport.Requests);

            mission.Send(new StartMission(Goal, 10m, Sonnet, remote: true));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Equal("", mission.View.StartForm.Refusal);
        }

        [Theory]
        [InlineData("Available")]
        [InlineData("Assigned")]
        public void ACrewedMissionStartedInFlightIsRefusedWhenClaudiaIsNotAboardTheActiveVessel(string standing)
        {
            var transport = Replies();
            var mission = Direct(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot(s => { s.Crew.Clear(); s.Crew.Add(Kerbal("Valentina Kerman")); s.ClaudiaStanding = standing; }));
            Commands(mission);

            mission.Send(new StartMission(Goal, 10m, Sonnet));

            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Contains("Claudia isn't aboard and every crew seat on the craft is taken", mission.View.StartForm.Refusal);
            Assert.Contains("Claudia isn't aboard", Assert.Single(Commands(mission).OfType<PostScreenMessage>()).Text);
            Assert.Empty(transport.Requests);
        }

        [Fact]
        public void ACrewedMissionStartedInFlightGoesAheadWhenClaudiaIsAboard()
        {
            var mission = Direct(Replies("craft-check-pass.sse"));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot(s => { s.Crew.Clear(); s.Crew.Add(new SnapshotCrewMember { Name = "Claudia Kermin", Trait = "SuperIntelligence" }); }));

            mission.Send(new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);

            Assert.Equal("", mission.View.StartForm.Refusal);
            Assert.NotEqual(MissionStage.None, mission.View.Stage);
        }

        [Fact]
        public void ACrewedMissionStartsAgainOnceClaudiaIsBack()
        {
            var mission = Direct(Replies("craft-check-pass.sse"));
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(Snapshot(s => s.ClaudiaStanding = "Missing"));
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.None, mission.View.Stage);

            mission.Send(Snapshot(s => s.ClaudiaStanding = "Available"));
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.False(mission.View.Remote);
        }

        [Fact]
        public void TheMissionsModeReachesClaudiaWithTheMissionPlanTurn()
        {
            var transport = Replies("mission-plan.sse");
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet, remote: true));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Contains("This is a remote Mission", Facts(transport.Requests[0]));
        }

        // ---- Lost ----

        [Fact]
        public void HerDeathAboardEndsTheMissionAsLostWithAFinalDebriefAndAbortsTheScript()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            StartScript script;
            var mission = Running(transport, remote: false, script: out script);
            var requests = transport.Requests.Count;

            mission.Send(new ClaudiaKilled());

            Assert.Equal(MissionStage.Lost, mission.View.Stage);
            var abort = Assert.Single(Commands(mission).OfType<AbortScript>());
            Assert.Equal(script.ProcessorPartId, abort.ProcessorPartId);
            var debrief = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal("Final Debrief: Lost", debrief.Title);
            Assert.Equal("Failed", debrief.Outcome);
            Assert.Contains("Claudia Kermin was killed aboard", debrief.Text);
            Assert.Contains("Link check", debrief.Text);
            Assert.Contains("Mission Goal not met", debrief.Text);
            Assert.Contains("Mission Control", mission.View.LatestLine);
            // The final Debrief is Mission Control's own: no request to Claude.
            Assert.Equal(requests, transport.Requests.Count);
            Assert.False(mission.View.CanEndMission);
            Assert.True(mission.View.StartForm.CanStart);
            Assert.Empty(mission.View.QuickReplies);
            Assert.False(mission.View.CanType);
        }

        [Fact]
        public void ALateRunEventAfterLostIsIgnored()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief.sse");
            StartScript script;
            var mission = Running(transport, remote: false, script: out script);
            mission.Send(new ClaudiaKilled());
            mission.TakeCommands();
            var requests = transport.Requests.Count;
            var cards = mission.View.Log.Count(e => e.Kind == LogKind.Debrief);
            var log = mission.View.Log.Count;

            // The script she started reports after she died, and its processor may be refused late too.
            mission.Send(new KosRunEnded(Finished(), script.RunId));
            mission.Send(new KosRunRefused("busy", script.RunId));
            _background.Drain();

            Assert.Equal(MissionStage.Lost, mission.View.Stage);
            Assert.Equal(requests, transport.Requests.Count);
            Assert.Equal(cards, mission.View.Log.Count(e => e.Kind == LogKind.Debrief));
            Assert.Equal(log, mission.View.Log.Count);
            Assert.False(mission.View.Busy);
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void NothingStartsOrIsAskedOfClaudiaOnceTheMissionIsLost()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-answer.sse");
            StartScript script;
            var mission = Running(transport, remote: false, script: out script);
            mission.Send(new ClaudiaKilled());
            mission.TakeCommands();
            var requests = transport.Requests.Count;

            mission.Send(new AskQuestion("Are you there?"));
            mission.Send(new Approve());
            mission.Send(new Retry());
            mission.Send(new Resume());
            mission.Send(new Reply("go"));
            mission.Send(new ClaudiaKilled());
            _background.Drain();

            Assert.Equal(MissionStage.Lost, mission.View.Stage);
            Assert.Equal(requests, transport.Requests.Count);
            Assert.Empty(mission.TakeCommands());
        }

        [Fact]
        public void HerDeathWhileARequestIsInFlightCancelsItAndKeepsItsWorstCaseCharge()
        {
            var plan = ScriptedResponse.Fixture("mission-plan-priced.sse");
            plan.PauseAt = 200;
            var transport = new StreamingFakeTransport(plan);
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot());
            Assert.True(mission.View.Busy);
            var pending = mission.View.PendingCharge;
            Assert.True(pending > 0m);

            mission.Send(new ClaudiaKilled());
            Assert.Equal(MissionStage.Lost, mission.View.Stage);
            plan.Gate.Set();
            DrainUntil(mission, v => !v.Busy);

            // The reply (if any got through) was not applied; the worst case stays charged.
            Assert.Equal(MissionStage.Lost, mission.View.Stage);
            Assert.DoesNotContain(mission.View.Log, e => e.Kind == LogKind.MissionPlan);
            Assert.Equal(pending, mission.View.HeldCharges);
            Assert.Equal(0m, mission.View.Spent);
            Assert.Equal(0m, mission.View.PendingCharge);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void AQuickloadToBeforeHerDeathStillRestoresTheMissionWhileItsRequestWasInFlight()
        {
            var plan = ScriptedResponse.Fixture("mission-plan-priced.sse");
            plan.PauseAt = 200;
            var mission = Control(new StreamingFakeTransport(plan));
            mission.Send(new SceneChanged(GameScene.Editor));
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet));
            var before = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot());
            mission.Send(new ClaudiaKilled());
            Assert.Equal(MissionStage.Lost, mission.View.Stage);

            mission.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { before }));
            plan.Gate.Set();
            DrainUntil(mission, v => !v.Busy);

            Assert.NotEqual(MissionStage.Lost, mission.View.Stage);
            Assert.True(mission.View.CanEndMission);
        }

        [Fact]
        public void HerDeathDoesNotEndARemoteMission()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            StartScript script;
            var mission = Running(transport, remote: true, script: out script);

            mission.Send(new ClaudiaKilled());

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());
        }

        [Fact]
        public void ALostMissionSurvivesASaveAndALoad()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            StartScript script;
            var mission = Running(transport, remote: false, script: out script);
            mission.Send(new ClaudiaKilled());
            var states = mission.ExportStates();
            var saved = MissionSaveState.FromValues(Assert.Single(states).ToValues());
            Assert.Equal(MissionStage.Lost, saved.Stage);

            var after = Control(Replies());
            after.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));

            Assert.Equal(MissionStage.Lost, after.View.Stage);
            Assert.Equal("Final Debrief: Lost", after.View.Log.Last(e => e.Kind == LogKind.Debrief).Title);
            Assert.True(after.View.StartForm.CanStart);
            Assert.False(after.View.CanEndMission);
        }

        [Fact]
        public void TheMissionsModeSurvivesASaveAndALoad()
        {
            var transport = Replies("mission-plan.sse");
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Editor));
            CraftCheckRouter.StartChecked(mission, _background, Snapshot(), new StartMission(Goal, 10m, Sonnet, remote: true));
            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.True(saved.Remote);

            var after = Control(Replies());
            after.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            Assert.True(after.View.Remote);
            Assert.True(Assert.Single(after.ExportStates()).Remote);
        }

        // ---- EVA science ----

        [Fact]
        public void TheCraftCheckWarnsAboutEvaScienceAndStartWaitsForTheAcknowledgement()
        {
            var mission = Direct(Replies("craft-check-eva.sse"));
            mission.Send(new SceneChanged(GameScene.Editor));
            Check(mission, Editor(1, "Claudia Kermin"));

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftVerdict.PassWithWarnings, check.Verdict);
            var warning = Assert.Single(check.Findings);
            Assert.Equal("eva_science_required", warning.Code);
            Assert.Equal(CraftFindingSeverity.Warning, warning.Severity);
            Assert.False(check.StartAllowed);

            mission.Send(new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.None, mission.View.Stage);

            mission.Send(new AcknowledgeWarning("eva_science_required"));
            Assert.True(mission.View.CraftCheck.StartAllowed);
            mission.Send(new StartMission(Goal, 10m, Sonnet));
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
        }

        /// <summary>A Mission whose first Maneuver Sequence ended and whose Debrief asks the player for EVA science.</summary>
        private MissionControl PausedForEva(StreamingFakeTransport transport)
        {
            StartScript script;
            var mission = Running(transport, remote: false, script: out script);
            mission.Send(new KosRunEnded(Finished(), script.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            return mission;
        }

        [Fact]
        public void AnEvaScienceGoalPausesForThePlayerAndResumesWithTheNextSequencePlan()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-eva.sse", "mission-sequence-plan.sse");
            var mission = PausedForEva(transport);

            // Paused, not ended: the player is told what to do by hand, and Claudia asks for nothing yet.
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal("Take an EVA report from the surface.", mission.View.EvaScience);
            Assert.Contains("Take an EVA report from the surface.", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.Resume }, mission.View.QuickReplies);
            Assert.False(mission.View.Busy);
            Assert.Equal(3, transport.Requests.Count);

            // Nothing moves until the player resumes, whatever else happens.
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot());
            _background.Drain();
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count);

            mission.Send(new Resume());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal("", mission.View.EvaScience);
            Assert.Equal(4, transport.Requests.Count);
            var next = Facts(transport.Requests[3]);
            Assert.Contains("has done the EVA science you asked for by hand", next);
            Assert.Contains("Take an EVA report from the surface.", next);
        }

        [Fact]
        public void TheEvaPauseSurvivesASaveAndALoad()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-eva.sse");
            var mission = PausedForEva(transport);
            var saved = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal("Take an EVA report from the surface.", saved.EvaScience);
            Assert.Equal(MissionStage.Paused, saved.Stage);

            var after = Replies("mission-sequence-plan.sse");
            var reloaded = Control(after);
            reloaded.Send(new MissionsRestored(_save.Path, new List<MissionSaveState> { saved }));
            Assert.Equal(MissionStage.Paused, reloaded.View.Stage);
            Assert.Equal("Take an EVA report from the surface.", reloaded.View.EvaScience);
            Assert.Equal(new[] { QuickReply.Resume }, reloaded.View.QuickReplies);

            reloaded.Send(new SceneChanged(GameScene.Flight));
            reloaded.Send(Snapshot());
            reloaded.Send(new Resume());
            DrainUntil(reloaded, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Contains("has done the EVA science", Facts(after.Requests[0]));
        }

        [Fact]
        public void RaisingTheCapDoesNotSkipThePlayersEva()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse", "mission-debrief-eva.sse");
            var mission = PausedForEva(transport);

            mission.Send(new RaiseCap(20m));

            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal("Take an EVA report from the surface.", mission.View.EvaScience);
            Assert.Equal(new[] { QuickReply.Resume }, mission.View.QuickReplies);
        }
    }
}
