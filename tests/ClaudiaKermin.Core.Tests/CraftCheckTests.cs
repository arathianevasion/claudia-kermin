// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Craft Check through the core's one seam: Game Snapshots and player actions go in, the view
    /// model and the requests Claude received come out. Claude's replies are hand-written streams.
    /// </summary>
    public sealed class CraftCheckTests : IDisposable
    {
        private const string Goal = "Land on the Mun and come home.";
        private const string Model = "claude-sonnet-5-5";
        private const decimal Cap = 10m;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            return mission;
        }

        private static GameSnapshot Craft()
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
            Assert.True(condition(mission.View), "never reached; check is " + mission.View.CraftCheck.State + " " + mission.View.CraftCheck.Message);
        }

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f, 64)).ToArray());
        }

        private static string UserFacts(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray()
                    .First(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
            }
        }

        private static string SystemText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return string.Concat(doc.RootElement.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString()));
        }

        private MissionControl Checked(StreamingFakeTransport transport, GameSnapshot craft = null, PlayerConfig config = null)
        {
            var mission = Control(transport, config);
            mission.Send(craft ?? Craft());
            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            return mission;
        }

        [Fact]
        public void APassingCraftCheckIsShownAsPassAndNothingIsEditedInTheGame()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Checked(transport);

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftCheckState.Done, check.State);
            Assert.Equal(CraftVerdict.Pass, check.Verdict);
            Assert.Equal("The craft has delta-v to spare for a pad check, a processor and a seat for me.", check.Summary);
            Assert.Empty(check.Findings);
            Assert.True(check.StartAllowed);
            // Only screen messages may come out: no command changes the craft or runs a script.
            Assert.All(mission.TakeCommands(), c => Assert.IsType<PostScreenMessage>(c));
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        [Fact]
        public void TheRequestCarriesTheFactsTheMapTheFixedListsAndTheMargin()
        {
            var transport = Replies("craft-check-pass.sse");
            Checked(transport);

            var request = transport.Requests.Single();
            var system = SystemText(request);
            foreach (var blocker in new[] { "no_kos_processor", "no_seat_for_claudia", "no_probe_core_or_antenna", "delta_v_short", "twr_below_one",
                "no_parachutes_for_return", "required_experiments_missing", "child_vessel_problem" })
                Assert.Contains(blocker, system);
            foreach (var warning in new[] { "launch_twr_low", "landing_twr_low", "margin_under_25", "no_landing_legs", "no_electric_generation_or_low_battery",
                "weak_control_authority", "no_attitude_control", "no_heat_shield", "eva_science_required", "boot_file_set", "manual_stretches_without_crew" })
                Assert.Contains(warning, system);
            Assert.DoesNotContain("aerodynamically", system, StringComparison.OrdinalIgnoreCase);

            var facts = UserFacts(request);
            Assert.Contains(Goal, facts);
            Assert.Contains("15%", facts);
            Assert.Contains("launch:Kerbin", facts);
            Assert.Contains("transfer:Mun", facts);
            Assert.Contains("kOSMachine1m", facts);
            Assert.Contains("SuperIntelligence", facts);
            // Thrust over weight, from the stage's thrust and start mass: 50 kN / (4.36 t x 9.81) and 60 / (4.36 x 1.628).
            Assert.Contains("\"Kerbin\":1.17", facts);
            Assert.Contains("\"Mun\":8.45", facts);
            using (var doc = JsonDocument.Parse(request.Body))
                Assert.Equal("json_schema", doc.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        }

        [Fact]
        public void NoReactionWheelRcsOrStabiliserIsAWarningNotABlocker()
        {
            var transport = Replies("craft-check-pass.sse");
            Checked(transport);

            // It sits under the Warnings heading, never the Blockers, and names the three part tags the adapter writes.
            var system = SystemText(transport.Requests.Single());
            var split = system.IndexOf("Warnings (", StringComparison.Ordinal);
            Assert.DoesNotContain("no_attitude_control", system.Substring(0, split));
            var line = system.Substring(split).Split('\n').Single(l => l.StartsWith("- no_attitude_control:", StringComparison.Ordinal));
            foreach (var tag in new[] { "reaction_wheel", "rcs", "aero_stabiliser" })
                Assert.Contains(tag, line);
            // The request schema lets Claude report it.
            using (var doc = JsonDocument.Parse(transport.Requests.Single().Body))
                Assert.Contains("no_attitude_control", doc.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("schema").ToString());
        }

        [Fact]
        public void TheMarginComesFromThePlayerConfigAndDefaultsToFifteenPercent()
        {
            Assert.Equal(15, new PlayerConfig().CraftCheckMarginPercent);
            var directory = Path.Combine(_save.Path, "config");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PlayerConfig.FileName), "craft_check_margin = 25\n");
            var config = PlayerConfig.LoadOrCreate(directory);
            Assert.Equal(25, config.CraftCheckMarginPercent);

            var transport = Replies("craft-check-pass.sse");
            Checked(transport, config: config);
            Assert.Contains("25%", UserFacts(transport.Requests.Single()));
            Assert.DoesNotContain("15%", UserFacts(transport.Requests.Single()));
        }

        [Fact]
        public void WarningsMustBeAcknowledgedBeforeStartMissionWorks()
        {
            var transport = Replies("craft-check-warnings.sse");
            var mission = Checked(transport);

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftVerdict.PassWithWarnings, check.Verdict);
            Assert.Equal(new[] { "launch_twr_low", "boot_file_set" }, check.Findings.Select(f => f.Code).ToArray());
            Assert.All(check.Findings, f => Assert.Equal(CraftFindingSeverity.Warning, f.Severity));
            Assert.False(check.StartAllowed);

            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(MissionStage.None, mission.View.Stage);

            mission.Send(new AcknowledgeWarning("launch_twr_low"));
            Assert.False(mission.View.CraftCheck.StartAllowed);
            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(MissionStage.None, mission.View.Stage);

            mission.Send(new AcknowledgeWarning("boot_file_set"));
            Assert.True(mission.View.CraftCheck.StartAllowed);
            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void ManualStretchesWithNoOtherCrewAreAWarningClaudiaJudgesFromTheCrewList()
        {
            var transport = Replies("craft-check-manual-no-crew.sse");
            var mission = Checked(transport);

            // The rule says who counts as other crew: not Claudia, not Tourists. The crew list is in the facts.
            var system = SystemText(transport.Requests.Single());
            Assert.Contains("manual_stretches_without_crew", system);
            Assert.Contains("SuperIntelligence", system);
            Assert.Contains("Tourist", system);
            Assert.Contains("\"trait\":\"SuperIntelligence\"", UserFacts(transport.Requests.Single()));

            var finding = Assert.Single(mission.View.CraftCheck.Findings);
            Assert.Equal("manual_stretches_without_crew", finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.False(mission.View.CraftCheck.StartAllowed);
            mission.Send(new AcknowledgeWarning("manual_stretches_without_crew"));
            Assert.True(mission.View.CraftCheck.StartAllowed);
        }

        [Fact]
        public void RunningTheSameCheckAgainKeepsTheWarningsAlreadyAcknowledged()
        {
            var mission = Checked(Replies("craft-check-warnings.sse", "craft-check-warnings.sse"));
            mission.Send(new AcknowledgeWarning("boot_file_set"));

            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            var findings = mission.View.CraftCheck.Findings.ToDictionary(f => f.Code, f => f.Acknowledged);
            Assert.True(findings["boot_file_set"]);
            Assert.False(findings["launch_twr_low"]);
        }

        [Fact]
        public void ABlockerFailsTheCraftCheckAndStartMissionNeverWorks()
        {
            var transport = Replies("craft-check-fail.sse");
            var mission = Checked(transport);

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftVerdict.Fail, check.Verdict);
            var blocker = check.Findings.Single(f => f.Severity == CraftFindingSeverity.Blocker);
            Assert.Equal("delta_v_short", blocker.Code);
            Assert.Equal("Add a stage or bigger tanks.", blocker.Fix);
            Assert.Contains("3,200", blocker.Explanation);
            Assert.False(check.StartAllowed);

            // A Warning beside a Blocker can be acknowledged, but nothing overrides the Blocker.
            mission.Send(new AcknowledgeWarning("no_landing_legs"));
            mission.Send(new AcknowledgeWarning("delta_v_short"));
            Assert.False(mission.View.CraftCheck.StartAllowed);
            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void TheSeverityOfAFindingComesFromTheFixedListsNotFromClaude()
        {
            var mission = Checked(Replies("craft-check-fail.sse"));
            var severities = mission.View.CraftCheck.Findings.ToDictionary(f => f.Code, f => f.Severity);
            Assert.Equal(CraftFindingSeverity.Blocker, severities["delta_v_short"]);
            Assert.Equal(CraftFindingSeverity.Warning, severities["no_landing_legs"]);
        }

        [Fact]
        public void AFindingOutsideTheFixedListsIsRefusedAndStartStaysClosed()
        {
            var mission = Checked(Replies("craft-check-unknown-code.sse"));
            var check = mission.View.CraftCheck;
            Assert.Equal(CraftCheckState.Error, check.State);
            Assert.Equal(CraftVerdict.None, check.Verdict);
            Assert.False(check.StartAllowed);
            Assert.Empty(check.Findings);
        }

        [Fact]
        public void WithTheStockDeltaVSimulatorOffTheCraftCheckStopsAndAsksWithoutCallingClaude()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);
            var craft = Craft();
            craft.DeltaVSimulator = false;
            mission.Send(craft);
            mission.Send(new RunCraftCheck(Goal, Cap, Model));

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftCheckState.NeedsSimulator, check.State);
            Assert.Contains("delta-v", check.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("enable", check.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(check.StartAllowed);
            Assert.Empty(transport.Requests);

            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(transport.Requests);

            // Once the player has enabled it, the next Craft Check runs.
            craft.DeltaVSimulator = true;
            mission.Send(craft);
            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
        }

        [Fact]
        public void StartMissionRunsTheCraftCheckAutomaticallyAndStartsOnAPass()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);
            mission.Send(Craft());
            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(CraftCheckState.Running, mission.View.CraftCheck.State);
            Assert.Equal(MissionStage.None, mission.View.Stage);

            DrainUntil(mission, v => v.Stage == MissionStage.WaitingForFlight);
            Assert.Equal(CraftVerdict.Pass, mission.View.CraftCheck.Verdict);
            Assert.Equal(Goal, mission.View.Goal);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void StartMissionAfterAFreshPassingCheckDoesNotCheckAgain()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Checked(transport);
            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void StartMissionWithWarningsRunsTheCheckThenWaitsForAcknowledgement()
        {
            var transport = Replies("craft-check-warnings.sse");
            var mission = Control(transport);
            mission.Send(Craft());
            mission.Send(new StartMission(Goal, 10m, Model));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            Assert.Equal(CraftVerdict.PassWithWarnings, mission.View.CraftCheck.Verdict);
            Assert.Equal(MissionStage.None, mission.View.Stage);
        }

        [Fact]
        public void EditingTheCraftMakesTheResultStaleAndStartChecksAgain()
        {
            var transport = Replies("craft-check-pass.sse", "craft-check-fail.sse");
            var mission = Checked(transport);
            Assert.False(mission.View.CraftCheck.Stale);

            // Resources and UT move all the time; only a change to the craft counts.
            var same = Craft();
            same.Ut += 50;
            same.Resources[1].Amount = 10;
            mission.Send(same);
            Assert.False(mission.View.CraftCheck.Stale);

            var edited = Craft();
            edited.Craft.Parts.RemoveAt(3);
            mission.Send(edited);
            Assert.True(mission.View.CraftCheck.Stale);

            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(CraftCheckState.Running, mission.View.CraftCheck.State);
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            Assert.Equal(CraftVerdict.Fail, mission.View.CraftCheck.Verdict);
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public void ADeltaVShortfallIsABlockerThatClosesStartMission()
        {
            var mission = Checked(Replies("craft-check-fail.sse"));
            var check = mission.View.CraftCheck;
            var blocker = check.Findings.Single(f => f.Code == "delta_v_short");
            Assert.Equal(CraftFindingSeverity.Blocker, blocker.Severity);
            Assert.Equal("Not enough delta-v", blocker.Title);
            Assert.NotEqual("", blocker.Fix);
            Assert.True(check.DeltaVShort);
            Assert.Equal(CraftVerdict.Fail, check.Verdict);
            Assert.False(check.StartAllowed);
        }

        [Fact]
        public void AStaleResultThatHadBlockersKeepsStartMissionClosedUntilTheCheckRunsAgain()
        {
            var mission = Checked(Replies("craft-check-fail.sse"));
            var edited = Craft();
            edited.Craft.Parts.RemoveAt(3);
            mission.Send(edited);

            Assert.True(mission.View.CraftCheck.Stale);
            Assert.False(mission.View.CraftCheck.StartAllowed);
        }

        [Fact]
        public void ChangingTheMissionGoalMakesTheResultStaleToo()
        {
            var transport = Replies("craft-check-pass.sse", "craft-check-pass.sse");
            var mission = Checked(transport);
            mission.Send(new StartMission("Fly to Minmus.", 10m, Model));
            Assert.Equal(CraftCheckState.Running, mission.View.CraftCheck.State);
            DrainUntil(mission, v => v.Stage == MissionStage.WaitingForFlight);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("Fly to Minmus.", UserFacts(transport.Requests[1]));
        }

        [Fact]
        public void ClaudiasProcessorDefaultsToTheOneNearestTheRootAndThePlayerCanChooseAnother()
        {
            var craft = Craft();
            craft.Craft.Parts.Add(new SnapshotPart { Id = 3105, ParentId = 3104, Name = "kOSMachine1m", Title = "Second processor", HasKosProcessor = true, BootFile = "boot/mine.ks" });
            var transport = Replies("craft-check-pass.sse", "craft-check-pass.sse");
            var mission = Checked(transport, craft);

            var check = mission.View.CraftCheck;
            Assert.Equal(3102u, check.ChosenProcessor);
            Assert.False(check.ChosenExplicitly);
            Assert.Equal(new uint[] { 3102, 3105 }, check.Processors.Select(p => p.PartId).ToArray());
            Assert.Equal(new[] { true, false }, check.Processors.Select(p => p.Chosen).ToArray());
            Assert.Contains("\"chosen_processor\":{\"id\":3102", UserFacts(transport.Requests[0]).Replace(" ", ""));

            mission.Send(new ChooseProcessor(3105));
            Assert.Equal(3105u, mission.View.CraftCheck.ChosenProcessor);
            Assert.True(mission.View.CraftCheck.ChosenExplicitly);
            // The choice changes what is checked, so the old result is stale.
            Assert.True(mission.View.CraftCheck.Stale);
            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            var facts = UserFacts(transport.Requests[1]).Replace(" ", "");
            Assert.Contains("\"chosen_processor\":{\"id\":3105", facts);
            Assert.Contains("boot/mine.ks", facts);

            // A part that is not a processor on this craft is ignored.
            mission.Send(new ChooseProcessor(3103));
            Assert.Equal(3105u, mission.View.CraftCheck.ChosenProcessor);
            mission.Send(new ChooseProcessor(null));
            Assert.Equal(3102u, mission.View.CraftCheck.ChosenProcessor);
            Assert.False(mission.View.CraftCheck.ChosenExplicitly);
        }

        [Fact]
        public void AFailedRequestLeavesStartClosedAndTheCheckCanBeRunAgain()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Error(401, "authentication_error", "invalid x-api-key"),
                ScriptedResponse.Fixture("craft-check-pass.sse", 64));
            var mission = Control(transport);
            mission.Send(Craft());
            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            Assert.Equal(CraftCheckState.Error, mission.View.CraftCheck.State);
            Assert.Contains("API key", mission.View.CraftCheck.Message);
            Assert.False(mission.View.CraftCheck.StartAllowed);

            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            Assert.True(mission.View.CraftCheck.StartAllowed);
        }

        [Fact]
        public void WithoutACraftThereIsNothingToCheck()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);
            mission.Send(new RunCraftCheck(Goal, Cap, Model));
            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(CraftCheckState.None, mission.View.CraftCheck.State);
            Assert.False(mission.View.CraftCheck.StartAllowed);
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(transport.Requests);
        }

        [Fact]
        public void ApprovedScriptsRunOnTheProcessorTheCheckChose()
        {
            var craft = Craft();
            craft.Craft.Parts.Add(new SnapshotPart { Id = 3105, ParentId = 3104, Name = "kOSMachine1m", Title = "Second processor", HasKosProcessor = true });
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("craft-check-pass.sse", 64),
                ScriptedResponse.Fixture("mission-plan.sse", 64),
                ScriptedResponse.Fixture("mission-sequence-plan.sse", 64));
            var mission = Control(transport);
            mission.Send(craft);
            mission.Send(new ChooseProcessor(3105));
            mission.Send(new StartMission(Goal, 10m, Model));
            DrainUntil(mission, v => v.Stage == MissionStage.WaitingForFlight);
            mission.Send(new SceneChanged(GameScene.Flight));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(3105u, mission.TakeCommands().OfType<StartScript>().Single().ProcessorPartId);
        }

        [Fact]
        public void TheChosenProcessorSurvivesLaunchReassigningPartIds()
        {
            var editor = Craft();
            editor.Craft.Parts.Add(new SnapshotPart { Id = 3105, ParentId = 3104, Name = "kOSMachine1m", Title = "Second processor", HasKosProcessor = true });
            foreach (var part in editor.Craft.Parts) part.CraftId = part.Id + 100;
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("craft-check-pass.sse", 64),
                ScriptedResponse.Fixture("mission-plan.sse", 64),
                ScriptedResponse.Fixture("mission-sequence-plan.sse", 64));
            var mission = Control(transport);
            mission.Send(editor);
            mission.Send(new ChooseProcessor(3105));
            mission.Send(new StartMission(Goal, 10m, Model));
            DrainUntil(mission, v => v.Stage == MissionStage.WaitingForFlight);
            mission.Send(new SceneChanged(GameScene.Flight));

            // Launch gave every part a new persistentId (same craftIDs, parent links follow).
            var flight = Craft();
            flight.Craft.Parts.Add(new SnapshotPart { Id = 3105, ParentId = 3104, Name = "kOSMachine1m", Title = "Second processor", HasKosProcessor = true });
            foreach (var part in flight.Craft.Parts)
            {
                part.CraftId = part.Id + 100;
                part.Id += 5000;
                if (part.ParentId.HasValue) part.ParentId += 5000;
            }
            mission.Send(flight);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(8105u, mission.TakeCommands().OfType<StartScript>().Single().ProcessorPartId);
        }

        [Fact]
        public void AChoiceWhoseProcessorIsGoneFallsBackToTheDefaultEvenIfItsOldIdIsAnotherProcessorNow()
        {
            var editor = Craft();
            editor.Craft.Parts.Add(new SnapshotPart { Id = 3105, ParentId = 3104, Name = "kOSMachine1m", HasKosProcessor = true });
            foreach (var part in editor.Craft.Parts) part.CraftId = part.Id + 100;
            var mission = Control(new StreamingFakeTransport());
            mission.Send(editor);
            mission.Send(new ChooseProcessor(3105));

            // Same craft, but the chosen processor is gone and 3105 now names a different processor.
            var later = Craft();
            later.Craft.Parts.Add(new SnapshotPart { Id = 3105, ParentId = 3104, Name = "kOSMachine1m", HasKosProcessor = true, CraftId = 999 });
            foreach (var part in later.Craft.Parts.Where(p => p.CraftId == 0)) part.CraftId = part.Id + 100;
            mission.Send(later);
            Assert.Equal(3102u, mission.View.CraftCheck.ChosenProcessor);
            Assert.False(mission.View.CraftCheck.ChosenExplicitly);
        }

        [Fact]
        public void TheDeltaVMapAndStageFactsRoundTripThroughTheSnapshotJson()
        {
            var craft = Craft();
            var again = GameSnapshot.Parse(craft.ToJson());
            Assert.True(again.DeltaVSimulator);
            Assert.Equal("SANDBOX", again.GameMode);
            Assert.Equal(4, again.Bodies.Count);
            Assert.Equal(60.0, again.Stages.Single().ThrustVacKn);
            Assert.Equal(1, again.Craft.Seats);
            Assert.Equal("SuperIntelligence", again.Crew.Single().Trait);
            Assert.Equal(new[] { "engine" }, again.Craft.Parts[3].Tags.ToArray());
            Assert.Equal(craft.ToJson(), again.ToJson());
        }
    }
}
