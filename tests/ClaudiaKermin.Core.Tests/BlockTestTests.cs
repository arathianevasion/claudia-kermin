// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The debug block test runner's judgement: a test's spec, and its pass/fail against the run's outcome.</summary>
    public class BlockTestTests
    {
        private const string Spec =
            "# exec_node: fly a prograde node in low Kerbin orbit\n"
            + "block = exec_node\n"
            + "craft = reference.craft\n"
            + "orbit_body = Kerbin\n"
            + "orbit_altitude = 100000\n"
            + "orbit_inclination = 0\n"
            + "timeout_s = 900\n"
            + "check = success == true\n"
            + "check = residual < 0.5\n"
            + "check = orbit.ap > 100000\n";

        private static RunOutcome Finished(Dictionary<string, object> report)
        {
            return new RunOutcome(RunOutcomeKind.Finished, report, "", "", false);
        }

        private static Dictionary<string, object> Report(bool success, double residual, double ap)
        {
            return new Dictionary<string, object>
            {
                { "success", success },
                { "reason", success ? "" : "residual too big" },
                { "residual", residual },
                { "orbit", new Dictionary<string, object> { { "ap", ap }, { "pe", 99000.0 } } },
            };
        }

        [Fact]
        public void ASpecNamesItsBlockCraftOrbitTimeoutAndChecks()
        {
            var spec = BlockTestSpec.Parse("exec_node", Spec);
            Assert.Equal("exec_node", spec.Name);
            Assert.Equal("exec_node", spec.Block);
            Assert.Equal("reference.craft", spec.Craft);
            Assert.Equal("Kerbin", spec.OrbitBody);
            Assert.Equal(100000.0, spec.OrbitAltitude);
            Assert.Equal(0.0, spec.OrbitInclination);
            Assert.Equal(900.0, spec.TimeoutSeconds);
            Assert.Equal(new[] { "success == true", "residual < 0.5", "orbit.ap > 100000" }, spec.Checks);
        }

        [Fact]
        public void ASpecWithAPeriapsisStartsOnAnEccentricOrbitFromItsApoapsis()
        {
            Assert.Null(BlockTestSpec.Parse("a", Spec).OrbitPeriapsis);
            var hop = BlockTestSpec.Parse("hop", "block = reentry\norbit_altitude = 100000\norbit_pe = -200000\n");
            Assert.Equal(-200000.0, hop.OrbitPeriapsis);
            Assert.Equal(100000.0, hop.OrbitAltitude);
        }

        [Fact]
        public void ASpecCanStartTheCraftTumbling()
        {
            Assert.Equal(0.0, BlockTestSpec.Parse("a", Spec).TumbleRate);
            Assert.Equal(40.0, BlockTestSpec.Parse("t", "block = reentry\ntumble_rate = 40\n").TumbleRate);
            Assert.Throws<System.FormatException>(() => BlockTestSpec.Parse("t", "block = reentry\ntumble_rate = fast\n"));
        }

        [Fact]
        public void ARunWithinEveryContractRangePasses()
        {
            var result = BlockTestSpec.Parse("exec_node", Spec).Judge(Finished(Report(true, 0.12, 300000)));
            Assert.True(result.Passed);
            Assert.Equal("PASS exec_node", result.Headline);
            Assert.Contains("ok   residual < 0.5 (got 0.12)", result.Lines);
            Assert.Contains("ok   success == true (got True)", result.Lines);
        }

        [Fact]
        public void AResidualOverTheContractRangeFailsAndSaysWhy()
        {
            var result = BlockTestSpec.Parse("exec_node", Spec).Judge(Finished(Report(false, 0.8, 300000)));
            Assert.False(result.Passed);
            Assert.Equal("FAIL exec_node: residual too big", result.Headline);
            Assert.Contains("FAIL residual < 0.5 (got 0.8)", result.Lines);
            Assert.Contains("FAIL success == true (got False)", result.Lines);
        }

        [Fact]
        public void AMissingFieldFailsItsCheck()
        {
            var report = Report(true, 0.1, 300000);
            report.Remove("orbit");
            var result = BlockTestSpec.Parse("exec_node", Spec).Judge(Finished(report));
            Assert.False(result.Passed);
            Assert.Contains("FAIL orbit.ap > 100000 (missing)", result.Lines);
        }

        [Fact]
        public void ARunThatDidNotReportFails()
        {
            var outcome = new RunOutcome(RunOutcomeKind.NoReport, null, "Undefined Variable Name 'foo'.", "", false);
            var result = BlockTestSpec.Parse("exec_node", Spec).Judge(outcome);
            Assert.False(result.Passed);
            Assert.StartsWith("FAIL exec_node: the run ended NoReport", result.Headline);
            Assert.Contains("Undefined Variable Name 'foo'.", string.Join("\n", result.Lines));
        }

        [Fact]
        public void ATimeOutFails()
        {
            var result = BlockTestSpec.Parse("exec_node", Spec).TimedOut();
            Assert.False(result.Passed);
            Assert.Equal("FAIL exec_node: timed out after 900 s", result.Headline);
        }

        [Theory]
        [InlineData("check = residual <= 0.5", 0.5, true)]
        [InlineData("check = residual >= 0.5", 0.4, false)]
        [InlineData("check = residual != 0", 0.4, true)]
        [InlineData("check = residual == 0.4", 0.4, true)]
        [InlineData("check = reason == ", 0.4, true)]
        public void ChecksCompareNumbersBooleansAndStrings(string check, double residual, bool passes)
        {
            var spec = BlockTestSpec.Parse("t", "block = exec_node\n" + check + "\n");
            Assert.Equal(passes, spec.Judge(Finished(Report(true, residual, 1))).Passed);
        }

        [Fact]
        public void ATestStartsInOrbitUnlessItSaysTheLaunchPad()
        {
            Assert.Equal(BlockTestStart.Orbit, BlockTestSpec.Parse("exec_node", Spec).Start);
            Assert.Equal(BlockTestStart.LaunchPad, BlockTestSpec.Parse("t", "block = launch\nstart = launchpad\n").Start);
            Assert.Throws<System.FormatException>(() => BlockTestSpec.Parse("t", "block = launch\nstart = runway\n"));
        }

        [Fact]
        public void AMalformedCheckIsRejectedWhenTheSpecIsRead()
        {
            Assert.Throws<System.FormatException>(() => BlockTestSpec.Parse("t", "block = exec_node\ncheck = residual about 0.5\n"));
            Assert.Throws<System.FormatException>(() => BlockTestSpec.Parse("t", "craft = x.craft\n"));
        }

        // #214: "kept == ran" and "parts_after < parts_before" compared a field with the literal
        // strings "ran" and "parts_before", so they could never pass; that must fail at load instead.
        [Theory]
        [InlineData("check = kept == ran")]
        [InlineData("check = parts_after < parts_before")]
        [InlineData("check = situation == landed_somewhere")]
        [InlineData("check = residual < true")]
        [InlineData("check = situation > ORBITING")]
        [InlineData("check = residual <= ")]
        public void ARightHandSideThatIsNotANumberBoolOrKnownValueIsRejectedWhenTheSpecIsRead(string check)
        {
            var e = Assert.Throws<System.FormatException>(() => BlockTestSpec.Parse("t", "block = exec_node\n" + check + "\n"));
            Assert.Contains(check.Substring("check = ".Length).Trim(), e.Message);
        }

        [Theory]
        [InlineData("check = residual < 0.5")]
        [InlineData("check = dv_used >= -1e3")]
        [InlineData("check = success == true")]
        [InlineData("check = success != False")]
        [InlineData("check = situation == ORBITING")]
        [InlineData("check = situation != splashed")]
        [InlineData("check = situation == SUB_ORBITAL")]
        [InlineData("check = body == Kerbin")]
        [InlineData("check = body == Mun")]
        [InlineData("check = reason == ")]
        public void ANumberABoolASituationABodyOrNothingIsARightHandSideTheCheckerReads(string check)
        {
            var spec = BlockTestSpec.Parse("t", "block = exec_node\n" + check + "\n");
            Assert.Single(spec.Checks);
        }

        // ---- Checks on the warp trace (#335) ----

        private const string CoastSpec = "block = coast\ncheck = success == true\ncheck = warp.first_request_real_s < 10\n";

        private static RunOutcome Traced(WarpTrace trace)
        {
            return new RunOutcome(RunOutcomeKind.Finished, Report(true, 0.1, 1), "", "", false, trace);
        }

        /// <summary>A coast block that waits from real second 0, asking for warp at real second firstAsk (UT 1000 + firstAsk) to UT 6000.</summary>
        private static WarpTrace Coast(double firstAsk)
        {
            var trace = new WarpTrace(100);
            trace.BlockStarted("coast", 100);
            trace.WaitStarted(100, 1000, 6000);
            trace.Sample(100, 1000, 1);
            trace.Sample(100 + firstAsk, 1000 + firstAsk, 1);
            trace.Requested(100 + firstAsk, 1000 + firstAsk, 6000, "rails", null, "");
            trace.Sample(101 + firstAsk, 1100 + firstAsk, 1000);
            return trace;
        }

        [Fact]
        public void AWarpCheckPassesWhenTheBlocksFirstRequestCameSoonEnough()
        {
            var result = BlockTestSpec.Parse("coast", CoastSpec).Judge(Traced(Coast(3)));
            Assert.True(result.Passed, string.Join("\n", result.Lines));
            Assert.Contains("ok   warp.first_request_real_s < 10 (got 3)", result.Lines);
        }

        [Fact]
        public void AFailingWarpCheckFailsTheTest()
        {
            var result = BlockTestSpec.Parse("coast", CoastSpec).Judge(Traced(Coast(25)));
            Assert.False(result.Passed);
            Assert.Contains("FAIL warp.first_request_real_s < 10 (got 25)", result.Lines);
        }

        [Fact]
        public void ABlockThatNeverAskedForWarpFailsItsFirstRequestCheck()
        {
            var trace = new WarpTrace(0);
            trace.BlockStarted("coast", 0);
            trace.BlockEnded("coast", 50);
            var result = BlockTestSpec.Parse("coast", CoastSpec).Judge(Traced(trace));
            Assert.False(result.Passed);
            Assert.Contains("FAIL warp.first_request_real_s < 10 (missing)", result.Lines);
        }

        [Fact]
        public void ARunWithNoTraceFailsItsWarpChecks()
        {
            var result = BlockTestSpec.Parse("coast", CoastSpec).Judge(Finished(Report(true, 0.1, 1)));
            Assert.False(result.Passed);
            Assert.Contains("FAIL warp.first_request_real_s < 10 (missing)", result.Lines);
        }

        [Fact]
        public void TheTraceGivesRealTimeAt1xInsideWaitsTheTopRateAndRetries()
        {
            var trace = new WarpTrace(0);
            trace.BlockStarted("coast", 0);
            trace.Sample(0, 1000, 1);
            // 20 real s at 1x before the wait doesn't count.
            trace.Sample(20, 1020, 1);
            trace.WaitStarted(20, 1020, 9000);
            trace.Sample(24, 1024, 1);
            trace.Requested(24, 1024, 9000, "rails", null, "");
            trace.Sample(25, 1100, 1000);
            trace.Sample(30, 6000, 10000);
            // KSP cut it; 6 s at 1x before the wait asks again. With the frame after each request, 12 s at 1x in all.
            trace.Sample(31, 6001, 1);
            trace.Sample(37, 6007, 1);
            trace.Requested(37, 6007, 9000, "rails", null, "");
            trace.Sample(38, 6100, 1000);
            trace.WaitEnded(40, 9000);
            trace.Sample(60, 9020, 1);
            trace.BlockEnded("coast", 60);

            var spec = BlockTestSpec.Parse("coast", "block = coast\n"
                + "check = warp.first_request_real_s == 24\ncheck = warp.real_s_at_1x == 12\n"
                + "check = warp.max_rate == 10000\ncheck = warp.retries == 1\ncheck = warp.coast.retries == 1\n");
            var result = spec.Judge(Traced(trace));
            Assert.True(result.Passed, string.Join("\n", result.Lines));
        }

        [Fact]
        public void EachRequestKeepsItsTargetModeRateReachedAndKspsRefusal()
        {
            var trace = new WarpTrace(10);
            trace.BlockStarted("exec_node", 10);
            trace.WaitStarted(12, 500, 900);
            trace.Requested(12, 500, 900, "rails", null, "Cannot warp faster than 1x while in atmosphere.");
            trace.Sample(13, 501, 1);
            trace.Requested(15, 503, 900, "physics", 4, "");
            trace.Sample(16, 507, 4);

            var first = trace.Entries[0];
            Assert.Equal(WarpTraceKind.Request, first.Kind);
            Assert.Equal("exec_node", first.Block);
            Assert.Equal(2.0, first.RealS);
            Assert.Equal(500.0, first.Ut);
            Assert.Equal(900.0, first.TargetUt);
            Assert.Equal("rails", first.Mode);
            Assert.Null(first.RateAsked);
            Assert.Equal(1.0, first.RateReached);
            Assert.Equal("Cannot warp faster than 1x while in atmosphere.", first.Refusal);
            Assert.Equal(1, first.Attempt);

            var second = trace.Entries[1];
            Assert.Equal("physics", second.Mode);
            Assert.Equal(4.0, second.RateAsked);
            Assert.Equal(4.0, second.RateReached);
            Assert.Equal(2, second.Attempt);
            Assert.True(second.Retry);
        }

        [Fact]
        public void ARateThatFallsTo1xMidWaitIsADropAndADropAlreadyNamedIsNotCountedTwice()
        {
            var trace = new WarpTrace(0);
            trace.BlockStarted("coast", 0);
            trace.WaitStarted(0, 1000, 9000);
            trace.Requested(0, 1000, 9000, "rails", null, "");
            trace.Sample(1, 1100, 1000);
            Assert.True(trace.Sample(2, 2000, 1));
            trace.Dropped(2, 2000, "cut", "Cannot warp faster than 1x while the ship is under acceleration.");

            trace.Requested(3, 2001, 9000, "rails", null, "");
            trace.Sample(4, 2100, 1000);
            // A drop the mod names itself (the player's, a hold, thrust) before the frame's sample.
            trace.Dropped(5, 3000, "player", "");
            Assert.False(trace.Sample(5, 3000, 1));
            // At the request's target, falling to 1x is the warp ending, not a drop.
            trace.Requested(6, 3001, 9000, "rails", null, "");
            trace.Sample(7, 8000, 1000);
            Assert.False(trace.Sample(8, 8995, 1));

            var drops = trace.Entries.Where(e => e.Kind == WarpTraceKind.Drop).ToList();
            Assert.Equal(new[] { "cut", "player" }, drops.Select(d => d.Drop));
            Assert.Equal(1000.0, drops[0].RateReached);
            Assert.Equal("Cannot warp faster than 1x while the ship is under acceleration.", drops[0].Refusal);
        }

        [Fact]
        public void ANestedBlockCountsItsOwnFirstRequestAndSoDoesTheBlockAroundIt()
        {
            var trace = new WarpTrace(0);
            trace.BlockStarted("circularize", 0);
            trace.Sample(0, 0, 1);
            trace.BlockStarted("exec_node", 30);
            trace.WaitStarted(31, 100, 900);
            trace.Requested(34, 103, 900, "rails", null, "");
            var spec = BlockTestSpec.Parse("circularize", "block = circularize\n"
                + "check = warp.first_request_real_s == 34\ncheck = warp.exec_node.first_request_real_s == 4\n");
            Assert.True(spec.Judge(Traced(trace)).Passed);
        }

        [Theory]
        [InlineData("check = warp.first_request_real_s < 10")]
        [InlineData("check = warp.real_s_at_1x < 60")]
        [InlineData("check = warp.max_rate >= 1000")]
        [InlineData("check = warp.retries <= 3")]
        [InlineData("check = warp.exec_node.first_request_real_s < 10")]
        public void TheWarpTraceFieldsAreChecksASpecReads(string check)
        {
            Assert.Single(BlockTestSpec.Parse("t", "block = coast\n" + check + "\n").Checks);
        }

        [Fact]
        public void AWarpCheckOnAFieldTheTraceDoesNotHaveIsRejectedWhenTheSpecIsRead()
        {
            var e = Assert.Throws<System.FormatException>(() => BlockTestSpec.Parse("t", "block = coast\ncheck = warp.first_reqest_real_s < 10\n"));
            Assert.Contains("first_reqest_real_s", e.Message);
        }

        // KSP's own screen texts for its getMaxOnRailsRateIdx reasons (research #300, from the en-us dictionary).
        [Theory]
        [InlineData("NOT_IN_ATMOSPHERE", 1, null, "Cannot warp faster than 1x while in atmosphere.")]
        [InlineData("NOT_IN_ATMOSPHERE", 50, null, "Cannot warp faster than 50x while in atmosphere.")]
        [InlineData("NOT_UNDER_ACCELERATION", 1, null, "Cannot warp faster than 1x while the ship is under acceleration.")]
        [InlineData("NOT_WHILE_ON_A_LADDER", 1, null, "Cannot warp faster than 1x while Kerbals are on a ladder.")]
        [InlineData("NOT_WHILE_MOVING_OVER_SURFACE", 1, null, "Cannot warp faster than 1x while moving over terrain.")]
        [InlineData("NOT_WHILE_ABOUT_TO_CRASH", 10, 60000.0, "Cannot warp faster than 10x while below 60000m.")]
        [InlineData("ORBIT_EVENT_IMMINENT", 50, null, "Orbit event imminent. Dropping TimeWarp to max limit: 50x")]
        [InlineData("SOMETHING_NEW", 5, null, "KSP limited warp to 5x (SOMETHING_NEW)")]
        public void AKspRefusalReadsAsKspsOwnMessage(string reason, double rate, double? limit, string expected)
        {
            Assert.Equal(expected, WarpRefusal.Message(reason, rate, limit));
        }

        [Fact]
        public void TheTraceReadsAsOneLinePerEntryAndOnePerBlock()
        {
            var lines = Coast(3).Lines();
            Assert.Contains("request real 3.0 s, UT 1003, target 6000, rails, asked KSP's pick, reached 1000x, attempt 1, in coast", lines);
            Assert.Contains("block coast: first request 3.0 s, 4.0 s at 1x in waits, top rate 1000x, 1 requests, 0 retries, 0 drops", lines);
        }
    }
}
