// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
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
    /// The Spending Cap and cost, driven through the Mission seam with hand-written streams whose
    /// <c>usage.iterations</c> have known prices (a "priced" reply costs $0.54 on Sonnet 5.5).
    /// </summary>
    public sealed class MissionCostTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
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

        private string CostLog(string missionId)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", missionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static string[] Events(string log)
        {
            return log.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString())
                .Where(e => !e.StartsWith("craft_check_")).ToArray();
        }

        // ---- Price table ----

        [Fact]
        public void ThePriceTableShipsTheSpecModelsAndChargesAnUnknownModelAtTheHighestPrice()
        {
            var prices = new PlayerConfig().Prices;
            Assert.Equal(2m, prices.PriceOf("claude-sonnet-5-5").InputPerMTok);
            Assert.Equal(10m, prices.PriceOf("claude-sonnet-5-5").OutputPerMTok);
            Assert.Equal(4m, prices.PriceOf("claude-opus-5-5").InputPerMTok);
            Assert.Equal(20m, prices.PriceOf("claude-opus-5-5").OutputPerMTok);
            // The fallback models are in the table too.
            Assert.True(prices.Knows("claude-opus-5"));
            Assert.True(prices.Knows("claude-fable-5-1"));

            var highest = prices.PriceOf("some-future-model");
            Assert.False(prices.Knows("some-future-model"));
            Assert.Equal(prices.Models.Max(m => prices.PriceOf(m).InputPerMTok), highest.InputPerMTok);
            Assert.Equal(prices.Models.Max(m => prices.PriceOf(m).OutputPerMTok), highest.OutputPerMTok);
        }

        [Fact]
        public void EachAttemptInTheUsageIterationsIsPricedAtItsOwnModelsRate()
        {
            var prices = new PlayerConfig().Prices;
            var usage = new Usage();
            // Sonnet: 1M fresh input = $2; cache write 1M = $4 (1-hour TTL, twice input); cache read 1M = $0.20; 1M output = $10.
            usage.Iterations.Add(new UsageIteration
            {
                Type = "message", InputTokens = 1000000, CacheCreationInputTokens = 1000000, CacheReadInputTokens = 1000000, OutputTokens = 1000000,
            });
            Assert.Equal(16.2m, prices.CostOf(usage, "claude-sonnet-5-5"));

            // A compaction attempt without a model is priced at the request's model; a fallback at its own.
            usage.Iterations.Clear();
            usage.Iterations.Add(new UsageIteration { Type = "compaction", InputTokens = 1000000 });
            usage.Iterations.Add(new UsageIteration { Type = "fallback_message", Model = "claude-opus-5-5", OutputTokens = 1000000 });
            Assert.Equal(2m + 20m, prices.CostOf(usage, "claude-sonnet-5-5"));

            // No iterations: the top-level totals are priced at the message's model.
            var plain = new Usage { InputTokens = 1000000, OutputTokens = 1000000 };
            Assert.Equal(12m, prices.CostOf(plain, "claude-sonnet-5-5"));
        }

        [Fact]
        public void ThePlayerConfigCanAddAModelToThePriceTable()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Path);
                File.WriteAllText(Path.Combine(dir.Path, PlayerConfig.FileName), "price.my-model = 1.5, 7\n");
                var prices = PlayerConfig.LoadOrCreate(dir.Path).Prices;
                Assert.Equal(1.5m, prices.PriceOf("my-model").InputPerMTok);
                Assert.Equal(7m, prices.PriceOf("my-model").OutputPerMTok);
                Assert.Equal(2m, prices.PriceOf("claude-sonnet-5-5").InputPerMTok);
            }
        }

        // ---- Spending, pending charges and the pre-send check ----

        [Fact]
        public void TheRealUsageReplacesThePendingChargeAndTheStripShowsSpentAgainstTheCap()
        {
            var plan = ScriptedResponse.Fixture("mission-plan-priced.sse");
            plan.PauseAt = 200;
            var transport = new StreamingFakeTransport(plan);
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));

            // In flight: the worst case is recorded before sending.
            Assert.True(mission.View.Busy);
            Assert.True(mission.View.PendingCharge > 0m);
            Assert.Equal(0m, mission.View.Spent);
            var pending = mission.View.PendingCharge;

            plan.Gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(0m, mission.View.PendingCharge);
            Assert.Equal(0.54m, mission.View.Spent);
            // The worst case covered at least the full 32000-token output ($0.32 on Sonnet).
            Assert.True(pending > 0.32m);
            Assert.Equal(0m, mission.View.HeldCharges);
            Assert.False(mission.View.CapWarning);

            Assert.Equal(new[] { "pending", "paid" }, Events(CostLog(mission.View.MissionId)));
        }

        [Fact]
        public void ARequestWhoseWorstCaseCouldPassTheCapIsNotSentAndRaisingTheCapSendsIt()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse"));
            var mission = Control(transport);
            // 32000 output tokens alone could cost $0.32 on Sonnet; the input estimate is on top.
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 0.30m, Sonnet));

            Assert.Empty(transport.Requests);
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Contains("Spending Cap", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal(0m, mission.View.PendingCharge);
            Assert.Equal(new[] { "blocked" }, Events(CostLog(mission.View.MissionId)));

            mission.Send(new RaiseCap(0.30m));
            Assert.Empty(transport.Requests);
            Assert.Equal(0.30m, mission.View.SpendingCap);

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Single(transport.Requests);
            Assert.Equal(10m, mission.View.SpendingCap);
        }

        [Fact]
        public void ACancelledRequestKeepsItsWorstCaseChargeInTheLog()
        {
            var plan = ScriptedResponse.Fixture("mission-plan-priced.sse");
            plan.PauseAt = 200;
            var mission = Control(new StreamingFakeTransport(plan));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            var id = mission.View.MissionId;
            var pending = mission.View.PendingCharge;
            Assert.True(pending > 0m);

            mission.Send(new EndMission());
            plan.Gate.Set();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!Events(CostLog(id)).Contains("kept") && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }

            var lines = CostLog(id).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .Where(l => !l.GetProperty("event").GetString().StartsWith("craft_check_")).ToArray();
            Assert.Equal(new[] { "pending", "kept" },lines.Select(l => l.GetProperty("event").GetString()).ToArray());
            Assert.Equal(pending, lines[1].GetProperty("amount").GetDecimal());
            Assert.Equal("cancelled", lines[1].GetProperty("outcome").GetString());
        }

        [Fact]
        public void ALostRequestKeepsItsWorstCaseChargeAndNothingIsRefundedByRetry()
        {
            var cut1 = ScriptedResponse.Fixture("partial-text.sse", 4096, StreamEnd.Break);
            var cut2 = ScriptedResponse.Fixture("partial-text.sse", 4096, StreamEnd.Break);
            var transport = new StreamingFakeTransport(cut1, cut2, ScriptedResponse.Fixture("mission-plan-priced.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            var held = mission.View.HeldCharges;
            Assert.True(held > 0m);
            Assert.Equal(0m, mission.View.PendingCharge);
            Assert.Equal(0m, mission.View.Spent);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            // The lost request's charge stays; the successful retry adds its real cost.
            Assert.Equal(held, mission.View.HeldCharges);
            Assert.Equal(0.54m, mission.View.Spent);
            Assert.Equal(new[] { "pending", "kept", "pending", "paid" }, Events(CostLog(mission.View.MissionId)));
        }

        [Fact]
        public void ARefusedReplyIsPaidForAndLogged()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("refusal.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            // refusal.sse reports 30 input tokens and no output: $0.00006.
            Assert.Equal(0.00006m, mission.View.Spent);
            Assert.Equal(0m, mission.View.HeldCharges);
            var log = CostLog(mission.View.MissionId);
            Assert.Equal(new[] { "pending", "paid" }, Events(log));
            Assert.Contains("\"outcome\":\"refused\"", log);
        }

        [Fact]
        public void ACutOffReplyAndAnOffSchemaReplyArePaidForAndLogged()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("max-tokens-tool.sse"), ScriptedResponse.Fixture("text.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            var firstSpent = mission.View.Spent;
            Assert.True(firstSpent > 0m);
            Assert.Contains("\"outcome\":\"cut_off\"", CostLog(mission.View.MissionId));

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.Paused && v.Spent > firstSpent);
            Assert.Contains("\"outcome\":\"off_schema\"", CostLog(mission.View.MissionId));
            Assert.Equal(new[] { "pending", "paid", "pending", "paid" }, Events(CostLog(mission.View.MissionId)));
        }

        // ---- Thresholds ----

        [Fact]
        public void At80PercentOfTheCapAWarningAppearsOnceWithoutPausing()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse"));
            var mission = Control(transport);
            // $0.54 of $0.65 is 83%; the worst case ($0.3x) still fits before sending.
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 0.65m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.True(mission.View.CapWarning);
            Assert.Single(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("80%"));
            Assert.Contains(mission.TakeCommands(), c => c is PostScreenMessage && ((PostScreenMessage)c).Text.Contains("80%"));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);

            // Raising the cap clears the warning.
            mission.Send(new RaiseCap(5m));
            Assert.False(mission.View.CapWarning);
        }

        [Fact]
        public void ARunningSequenceFinishesAndTheMissionIsPausedAtTheCapUntilTheCapIsRaised()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-debrief-priced.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 1.20m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(1.08m, mission.View.Spent);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            var start = mission.TakeCommands().OfType<StartScript>().Single();

            // The script ends; the Debrief's worst case would pass the cap, so it is not sent.
            var report = new System.Collections.Generic.Dictionary<string, object> { { "success", true }, { "situation", "PRELAUNCH" } };
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, report, "", "", false), start.RunId));
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.DoesNotContain(mission.TakeCommands(), c => c is AbortScript);

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(1.62m, mission.View.Spent);
        }

        [Fact]
        public void ACostPastTheCapPausesTheMissionWithRaiseCapAndEndMission()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-big.sse"));
            var mission = Control(transport);
            // The plan costs $0.80, past the $0.50 cap, though its worst-case estimate fitted.
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 0.50m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Equal(0.80m, mission.View.Spent);
            Assert.Contains("Spending Cap", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.True(mission.View.CapWarning);

            mission.Send(new RaiseCap(5m));
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.DoesNotContain(QuickReply.RaiseCap, mission.View.QuickReplies);
        }

        // ---- The start form ----

        [Fact]
        public void TheStartFormEstimatesAMissionsCostPerModel()
        {
            var mission = Control(new StreamingFakeTransport());
            var estimates = mission.View.StartForm.Estimates;
            Assert.Equal(2.40m, estimates["claude-sonnet-5-5"]);
            Assert.InRange(estimates["claude-opus-5-5"], 4.5m, 5.5m);
        }

        [Fact]
        public void TheStartFormOffersTheSavedMissionDefaults()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                var mission = Control(new StreamingFakeTransport(), config);

                // Saved from the Space Center while the game runs: the next start form has them.
                config.SetDefaultModel("claude-opus-5-5");
                config.DefaultSpendingCap = 6m;
                config.DefaultDaylightLanding = false;
                config.DefaultTimeVersusDeltaV = TimeVersusDeltaV.MostEfficient;
                config.DefaultRemote = true;
                config.SaveDefaults(dir.Sub);
                mission.Send(new UtAdvanced(1));

                var form = mission.View.StartForm;
                Assert.Equal("claude-opus-5-5", form.DefaultModel);
                Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5" }, form.Models);
                Assert.Equal(6m, form.DefaultSpendingCap);
                Assert.False(form.DefaultSettings.DaylightLanding);
                Assert.Equal(TimeVersusDeltaV.MostEfficient, form.DefaultSettings.TimeVersusDeltaV);
                Assert.True(form.DefaultRemote);

                // And after a restart, from the file.
                var restarted = Control(new StreamingFakeTransport(), PlayerConfig.LoadOrCreate(dir.Sub));
                Assert.Equal("claude-opus-5-5", restarted.View.StartForm.DefaultModel);
                Assert.True(restarted.View.StartForm.DefaultRemote);
            }
        }
    }
}
