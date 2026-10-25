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
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Craft Check under the Spending Cap: checked before it is sent, settled like a Mission request, and the
    /// latest check's cost folded into the Mission it starts. Claude's replies are recorded or hand-written streams.
    /// </summary>
    public sealed class CraftCheckCostTests : IDisposable
    {
        private const string Goal = "Land on the Mun and come home.";
        private const string Model = "claude-sonnet-5-5";
        // The recorded pass reply: 2600 input and 40 output tokens at Sonnet's $2 and $10 per million.
        private const decimal PassCost = 0.0056m;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly List<string> _kspLog = new List<string>();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", line => { lock (_kspLog) _kspLog.Add(line); });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json"))));
            return mission;
        }

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f, 64)).ToArray());
        }

        private void DrainUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(), "never reached");
        }

        private void Check(MissionControl mission, decimal cap)
        {
            mission.Send(new RunCraftCheck(Goal, cap, Model));
            DrainUntil(() => mission.View.CraftCheck.State != CraftCheckState.Running);
        }

        private string[] KspLog()
        {
            lock (_kspLog) return _kspLog.Where(l => l.StartsWith("Craft Check")).ToArray();
        }

        private JsonElement[] CostLines(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        }

        private bool AnyMissionFolder()
        {
            var root = Path.Combine(_save.Path, "ClaudiaKermin", "missions");
            return Directory.Exists(root) && Directory.GetDirectories(root).Length > 0;
        }

        // ---- Checked before sending ----

        [Fact]
        public void ACraftCheckWhoseWorstCaseExceedsTheCapSendsNothingAndSaysSo()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);

            // Sonnet's 8000 output tokens alone could cost $0.08.
            mission.Send(new RunCraftCheck(Goal, 0.05m, Model));

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftCheckState.Error, check.State);
            Assert.Contains("Spending Cap", check.Message);
            Assert.Contains("Raise", check.Message);
            Assert.False(check.StartAllowed);
            Assert.Empty(transport.Requests);
        }

        [Fact]
        public void ACapThatFitsTheWorstCaseLetsTheCheckRun()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);
            Check(mission, 1m);
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
            Assert.Single(transport.Requests);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ACraftCheckWithNoCapOrACapOfZeroOrLessDoesNotRun(int cap)
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);

            mission.Send(new RunCraftCheck(Goal, cap, Model));

            Assert.Equal(CraftCheckState.Error, mission.View.CraftCheck.State);
            Assert.Contains("Spending Cap", mission.View.CraftCheck.Message);
            Assert.Empty(transport.Requests);
        }

        [Fact]
        public void StartMissionChecksTheCraftAgainstTheCapItCarries()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);

            mission.Send(new StartMission(Goal, 0.05m, Model));

            Assert.Equal(CraftCheckState.Error, mission.View.CraftCheck.State);
            Assert.Equal(MissionStage.None, mission.View.Stage);
            Assert.Empty(transport.Requests);
            Assert.False(AnyMissionFolder());
        }

        // ---- Folded into the Mission ----

        [Fact]
        public void AMissionStartedAfterAPassedCheckCarriesThatChecksSettledCostAndLogsItFirst()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);
            Check(mission, 10m);
            Assert.Equal(0m, mission.View.Spent);

            mission.Send(new StartMission(Goal, 10m, Model));

            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Equal(PassCost, mission.View.Spent);
            var lines = CostLines(mission);
            Assert.Equal(new[] { "craft_check_pending", "craft_check_paid" }, lines.Select(l => l.GetProperty("event").GetString()).ToArray());
            Assert.All(lines, l => Assert.Equal(Model, l.GetProperty("model").GetString()));
            Assert.Equal(PassCost, lines[1].GetProperty("amount").GetDecimal());
            Assert.Equal(PassCost, lines[1].GetProperty("spent").GetDecimal());
            Assert.Equal(0m, lines[0].GetProperty("spent").GetDecimal());
            Assert.True(lines[0].GetProperty("amount").GetDecimal() > PassCost);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public void AMissionStartedByStartMissionsOwnCheckCarriesItsCostToo()
        {
            var mission = Control(Replies("craft-check-pass.sse"));
            mission.Send(new StartMission(Goal, 10m, Model));
            DrainUntil(() => mission.View.Stage == MissionStage.WaitingForFlight);
            Assert.Equal(PassCost, mission.View.Spent);
            Assert.Equal("craft_check_pending", CostLines(mission)[0].GetProperty("event").GetString());
        }

        [Fact]
        public void RunningTheCheckTwiceBeforeStartingCountsOnlyTheSecondChecksCost()
        {
            var mission = Control(Replies("craft-check-pass.sse", "craft-check-pass.sse"));
            Check(mission, 10m);
            Check(mission, 10m);

            // The first check's cost is in KSP.log only.
            var first = Assert.Single(KspLog());
            Assert.Contains("paid", first);
            Assert.Contains("charged to no Mission", first);

            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(PassCost, mission.View.Spent);
            Assert.Equal(2, CostLines(mission).Length);
        }

        [Fact]
        public void TheCostOfACheckFoldsIntoOnlyTheOneMissionItStarts()
        {
            var mission = Control(Replies("craft-check-pass.sse"));
            Check(mission, 10m);
            mission.Send(new StartMission(Goal, 10m, Model));
            mission.Send(new EndMission());

            mission.Send(new StartMission(Goal, 10m, Model));
            Assert.Equal(0m, mission.View.Spent);
        }

        [Fact]
        public void AFoldedCostCountsTowardTheEightyPercentWarning()
        {
            // $0.0056 is past 80% of a $0.006 cap.
            var mission = Control(Replies("craft-check-pass.sse"));
            mission.Send(new RunCraftCheck(Goal, 10m, Model));
            DrainUntil(() => mission.View.CraftCheck.State != CraftCheckState.Running);

            mission.Send(new StartMission(Goal, 0.006m, Model));

            Assert.True(mission.View.CapWarning);
        }

        // ---- Superseded ----

        [Fact]
        public void ACheckSupersededByASaveLoadCountsTowardNoMissionAndWritesNoMissionFolder()
        {
            var transport = Replies("craft-check-pass.sse");
            var mission = Control(transport);
            Check(mission, 10m);

            using (var other = new TempDir())
            {
                mission.Send(new SaveLoaded(other.Path));
                Assert.Equal(CraftCheckState.None, mission.View.CraftCheck.State);
            }

            var line = Assert.Single(KspLog());
            Assert.Contains("charged to no Mission", line);
            Assert.False(AnyMissionFolder());
        }

        [Fact]
        public void ACheckNoMissionStartsFromWritesNothingToAnyMissionFolder()
        {
            var mission = Control(Replies("craft-check-pass.sse"));
            Check(mission, 10m);
            Assert.False(AnyMissionFolder());
        }

        [Fact]
        public void ACheckCancelledMidRequestKeepsItsPendingChargeInKspLogOnly()
        {
            var response = ScriptedResponse.Fixture("craft-check-pass.sse", 64);
            response.PauseAt = 200;
            var transport = new StreamingFakeTransport(response);
            var mission = Control(transport);

            mission.Send(new RunCraftCheck(Goal, 10m, Model));
            DrainUntil(() => transport.Requests.Count == 1);
            using (var other = new TempDir())
                mission.Send(new SaveLoaded(other.Path));
            response.Gate.Set();
            DrainUntil(() => KspLog().Length == 1);

            Assert.Contains("kept", KspLog()[0]);
            Assert.DoesNotContain("$0.0056", KspLog()[0]);
            Assert.False(AnyMissionFolder());
        }

        // ---- Settled like a Mission request ----

        [Fact]
        public void TheSettlingRulesAreTheMissionRequestsForPaidKeptAndReleased()
        {
            var prices = new PlayerConfig().Prices;
            var message = new AssistantMessage { Model = Model };
            message.Usage.InputTokens = 1000000;
            message.Usage.OutputTokens = 1000000;
            var done = new StreamResult { Status = StreamStatus.Completed, Message = message };
            var paid = RequestCharge.Settle(prices, 0.5m, done, Model);
            Assert.Equal("paid", paid.Kind);
            Assert.Equal(12m, paid.Spent);
            Assert.Equal(0m, paid.Held);

            foreach (var ended in new[] { StreamStatus.Cancelled, StreamStatus.Failed })
            {
                var kept = RequestCharge.Settle(prices, 0.5m, new StreamResult { Status = ended, Failure = StreamFailure.EndedEarly }, Model);
                Assert.Equal("kept", kept.Kind);
                Assert.Equal(0.5m, kept.Held);
                Assert.Equal(0m, kept.Spent);
            }
            Assert.Equal("kept", RequestCharge.Settle(prices, 0.5m, null, Model).Kind);

            var released = RequestCharge.Settle(prices, 0.5m, new StreamResult { Status = StreamStatus.Failed, Failure = StreamFailure.Unauthorized }, Model);
            Assert.Equal("released", released.Kind);
            Assert.Equal(0m, released.Held);
            Assert.Equal(0m, released.Spent);
        }
    }
}
