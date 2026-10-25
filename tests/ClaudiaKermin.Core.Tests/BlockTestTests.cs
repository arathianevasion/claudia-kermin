// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
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
    }
}
