// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// The Core Library's pure helpers, run on kOS.Safe through the real bundle: each test is a tiny
    /// Mission Script bundled with the whole library, exactly as the mod would start it.
    /// </summary>
    public class CoreLibraryTests
    {
        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        private static string Rounded(double value)
        {
            return Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
        }

        // ---- The bundle ----

        [Fact]
        public void Every_shipped_library_file_is_in_the_bundle_and_it_compiles()
        {
            Assert.Contains(CoreLibrary.Files, f => f.Name == "claudia_lib");
            Assert.Contains(CoreLibrary.Blocks, b => b.Name == "exec_node");
            Assert.Null(KosHarness.CompileError(CoreLibrary.Bundle("@lazyglobal off.\nprint 1.")));
        }

        [Fact]
        public void Every_block_test_script_compiles_bundled_with_the_library()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var scripts = System.IO.Directory.GetFiles(System.IO.Path.Combine(dir.FullName, "tests", "blocks"), "test.ks", System.IO.SearchOption.AllDirectories);
            Assert.NotEmpty(scripts);
            foreach (var script in scripts)
                Assert.True(KosHarness.CompileError(CoreLibrary.Bundle(System.IO.File.ReadAllText(script))) == null, script);
        }

        [Fact]
        public void A_mission_script_can_call_the_library()
        {
            Assert.Equal(new[] { "5370" }, KosHarness.Run(CoreLibrary.Bundle("@lazyglobal off.\nprint claudia_warp_end(5400, 30).")));
        }

        // ---- Options ----

        [Fact]
        public void An_unknown_option_fails_with_its_name()
        {
            Assert.Equal("unknown option max_residul", One("claudia_options(lexicon(\"max_residul\", 1), exec_node_defaults(), \"auto\"):error"));
        }

        [Fact]
        public void Block_and_common_options_are_known()
        {
            Assert.Equal("", One("claudia_options(lexicon(\"max_residual\", 0.2, \"min_ec_pct\", 20, \"stage_limit\", 1, \"max_duration\", 900, \"abort_on_part_loss\", false, \"node\", \"next\", \"lead\", 30, \"rcs_trim\", true), exec_node_defaults(), \"auto\"):error"));
        }

        [Fact]
        public void Left_out_options_take_the_spec_defaults()
        {
            var printed = Run(
                "local o is claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt.\n"
                + "print o:node. print o:max_residual. print o:lead. print o:rcs_trim.\n"
                + "print o:min_ec_pct. print o:max_duration. print o:abort_on_part_loss. print o:stage_limit.");
            Assert.Equal(new[] { "next", "0.5", "half_burn", "False", "10", "auto", "True", "0" }, printed);
        }

        [Fact]
        public void A_given_option_overrides_its_default_and_leaves_the_rest()
        {
            var printed = Run("local o is claudia_options(lexicon(\"max_residual\", 0.2), exec_node_defaults(), \"auto\"):opt. print o:max_residual. print o:min_ec_pct.");
            Assert.Equal(new[] { "0.2", "10" }, printed);
        }

        // ---- Aborts ----

        private const string Opt = "claudia_merge_options(lexicon(\"max_duration\", 600), claudia_common_defaults(0))";

        [Fact]
        public void Nothing_crossed_means_no_abort()
        {
            Assert.Equal("", One("claudia_abort_reason(" + Opt + ", 50, 100, 0)"));
        }

        [Fact]
        public void Low_electric_charge_aborts_with_min_ec_pct()
        {
            Assert.Equal("abort min_ec_pct: electric charge 8% is below 10%", One("claudia_abort_reason(" + Opt + ", 8, 100, 0)"));
        }

        [Fact]
        public void Running_too_long_aborts_with_max_duration()
        {
            Assert.Equal("abort max_duration: ran 601 s, over 600 s", One("claudia_abort_reason(" + Opt + ", 50, 601, 0)"));
        }

        [Fact]
        public void An_unresolved_auto_max_duration_never_aborts()
        {
            Assert.Equal("", One("claudia_abort_reason(claudia_common_defaults(\"auto\"), 50, 100000, 0)"));
        }

        [Fact]
        public void Losing_a_part_aborts_unless_turned_off()
        {
            Assert.Equal("abort abort_on_part_loss: 2 part(s) lost", One("claudia_abort_reason(" + Opt + ", 50, 100, 2)"));
            Assert.Equal("", One("claudia_abort_reason(claudia_merge_options(lexicon(\"abort_on_part_loss\", false), " + Opt + "), 50, 100, 2)"));
        }

        // ---- Auto-staging ----

        [Theory]
        [InlineData(3, 0, false, true, false, "none")]
        [InlineData(3, 0, true, false, false, "stage")]
        [InlineData(3, 0, true, true, false, "stage")]
        [InlineData(3, 2, true, false, false, "stage")]
        [InlineData(2, 2, true, false, false, "out_of_fuel")]
        [InlineData(1, 2, true, false, false, "out_of_fuel")]
        [InlineData(0, 0, true, false, false, "out_of_fuel")]
        [InlineData(2, 2, true, true, false, "none")]
        [InlineData(3, 0, true, false, true, "blocked")]
        [InlineData(3, 0, true, true, true, "none")]
        public void Staging_happens_on_flameout_down_to_the_limit_and_never_into_chutes(int stage, int limit, bool needs, bool hasThrust, bool unsafeNext, string expected)
        {
            Assert.Equal(expected, One("claudia_stage_action(" + stage + ", " + limit + ", " + Bool(needs) + ", " + Bool(hasThrust) + ", " + Bool(unsafeNext) + ")"));
        }

        [Theory]
        // A decoupler-only stage with no thrust left, engines next: stage on.
        [InlineData(3, 0, false, true, false, false, true)]
        [InlineData(3, 2, false, true, false, false, true)]
        // The stage fired held engines, or some engine still burns, or no engines next: wait for them.
        [InlineData(3, 0, true, true, false, false, false)]
        [InlineData(3, 0, false, true, true, false, false)]
        [InlineData(3, 0, false, false, false, false, false)]
        // Parachutes or a heat shield next: never.
        [InlineData(3, 0, false, true, false, true, false)]
        // At the stage limit or stage 0.
        [InlineData(2, 2, false, true, false, false, false)]
        [InlineData(0, 0, false, true, false, false, false)]
        public void A_decoupler_only_stage_without_thrust_stages_on_into_the_next_engines(int stage, int limit, bool firedEngines, bool nextEngines, bool hadThrust, bool unsafeNext, bool expected)
        {
            Assert.Equal(expected ? "True" : "False", One("claudia_stage_on(" + stage + ", " + limit + ", " + Bool(firedEngines) + ", " + Bool(nextEngines) + ", " + Bool(hadThrust) + ", " + Bool(unsafeNext) + ")"));
        }

        [Theory]
        // Lists run from the next stage to fire downwards: engines held, then parachutes/heat shield.
        // One decoupler-only stage, then engines.
        [InlineData(5, 0, false, false, "list(false, true)", "list(false, false)", true)]
        // Three decoupler-only stages in a row, then engines.
        [InlineData(5, 0, false, false, "list(false, false, false, true)", "list(false, false, false, false)", true)]
        // Engines straight away still chain.
        [InlineData(5, 0, false, false, "list(true)", "list(false)", true)]
        // A chute or heat shield stage anywhere before the engines stops the chain.
        [InlineData(5, 0, false, false, "list(false, false, true)", "list(false, true, false)", false)]
        [InlineData(5, 0, false, false, "list(false, true)", "list(true, false)", false)]
        // A run that never reaches engines, or runs out of stages, doesn't chain.
        [InlineData(5, 0, false, false, "list(false, false, false)", "list(false, false, false)", false)]
        [InlineData(5, 0, false, false, "list()", "list()", false)]
        // The stage limit cuts the run short: stage 3, limit 2 can fire one stage only.
        [InlineData(3, 2, false, false, "list(false, true)", "list(false, false)", false)]
        [InlineData(4, 2, false, false, "list(false, true)", "list(false, false)", true)]
        [InlineData(4, 2, false, false, "list(false, false, true)", "list(false, false, false)", false)]
        [InlineData(4, 2, false, false, "list(true)", "list(false)", true)]
        // Stage 0 can't be fired.
        [InlineData(1, 0, false, false, "list(false, true)", "list(false, false)", false)]
        // The stage just fired held engines, or some engine still gives thrust.
        [InlineData(5, 0, true, false, "list(false, true)", "list(false, false)", false)]
        [InlineData(5, 0, false, true, "list(false, true)", "list(false, false)", false)]
        public void Staging_on_chains_through_a_run_of_decoupler_only_stages_into_engines(int stage, int limit, bool firedEngines, bool hadThrust, string engines, string unsafeStages, bool expected)
        {
            Assert.Equal(expected ? "True" : "False", One("claudia_stage_chain(" + stage + ", " + limit + ", " + Bool(firedEngines) + ", " + Bool(hadThrust) + ", " + engines + ", " + unsafeStages + ")"));
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        // ---- Waits ----

        // ADR 0004: no wait cap; a wait warps on rails and is back at 1x at least 30 s before what follows.

        [Theory]
        [InlineData(5400, 30, "5370")]
        [InlineData(5400, 10, "5370")]
        [InlineData(5400, 120, "5280")]
        public void Warp_ends_the_lead_before_the_wait_ends_and_never_under_30_s(double ut, double lead, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_warp_end({0}, {1})", ut, lead)));
        }

        [Theory]
        // Asked for once, at the start of the wait.
        [InlineData(0, 1, false, true, "warp")]
        // Warping: carry on.
        [InlineData(3000, 100, false, false, "none")]
        // At the warp's end: drop to 1x before the burn.
        [InlineData(5370, 100, false, false, "drop")]
        [InlineData(5380, 1, false, false, "none")]
        // The player cancelled warp: never overridden.
        [InlineData(3000, 1, false, false, "none")]
        // The mod holds warp at 1x (the Mission needs the player): drop, and don't warp while held.
        [InlineData(3000, 100, true, false, "drop")]
        [InlineData(3000, 1, true, true, "none")]
        // After the hold: warp again.
        [InlineData(3100, 1, false, true, "warp")]
        // Too close to the end to be worth warping.
        [InlineData(5366, 1, false, true, "none")]
        public void A_wait_warps_once_drops_to_1x_before_the_burn_and_warps_again_after_a_hold(double now, double rate, bool held, bool want, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_warp_step({0}, 5370, {1}, {2}, {3})", now, rate, Bool(held), Bool(want))));
        }

        [Fact]
        public void A_three_orbit_hold_warps_by_itself_and_is_at_1x_30_s_before_the_burn()
        {
            // claudia_warp_to's loop over a 3-orbit wait (a 1800 s orbit), with a stand-in for KSP's warp:
            // WARPTO raises the rate to 1000x until the rate is cancelled. Longer than one orbit, and no Due Time.
            var printed = Run(
                "local ut is 3 * 1800. local warp_end is claudia_warp_end(ut, 30). local rate is 1. local want is true.\n"
                + "local warps is 0. local dropped_at is -1. local t is 0.\n"
                + "until t >= ut {\n"
                + "  local action is claudia_warp_step(t, warp_end, rate, false, want).\n"
                + "  if action = \"warp\" { set rate to 1000. set want to false. set warps to warps + 1. }\n"
                + "  if action = \"drop\" { set rate to 1. set dropped_at to t. }\n"
                + "  set t to t + 10.\n"
                + "}\n"
                + "print warps. print dropped_at. print ut - dropped_at >= 30. print rate.");
            Assert.Equal(new[] { "1", "5370", "True", "1" }, printed);
        }

        // ---- coast ----

        [Fact]
        public void Coast_needs_a_ut_and_takes_a_lead_in_seconds()
        {
            Assert.Equal("until is required", One("coast_unreadable(claudia_options(lexicon(), coast_defaults(), \"auto\"):opt)"));
            Assert.Equal("until must be a UT in seconds", One("coast_unreadable(claudia_options(lexicon(\"until\", \"soon\"), coast_defaults(), \"auto\"):opt)"));
            Assert.Equal("lead must be seconds", One("coast_unreadable(claudia_options(lexicon(\"until\", 100, \"lead\", -5), coast_defaults(), \"auto\"):opt)"));
            Assert.Equal("", One("coast_unreadable(claudia_options(lexicon(\"until\", 100000, \"lead\", 60), coast_defaults(), \"auto\"):opt)"));
            Assert.Equal("30", One("claudia_options(lexicon(\"until\", 100), coast_defaults(), \"auto\"):opt:lead"));
        }

        // ---- Burns ----

        private static double BurnTime(double dv, double mass, double thrust, double isp)
        {
            var ve = isp * 9.80665;
            return mass * ve / thrust * (1 - Math.Exp(-dv / ve));
        }

        [Fact]
        public void Burn_time_follows_the_rocket_equation()
        {
            Assert.Equal(Rounded(BurnTime(500, 10, 100, 300)), One("round(claudia_burn_time(500, 10, 100, 300), 3)"));
        }

        [Fact]
        public void With_no_thrust_there_is_no_burn_time()
        {
            Assert.Equal("-1", One("claudia_burn_time(500, 10, 0, 300)"));
        }

        [Fact]
        public void Engines_burning_together_combine_their_isp_by_flow()
        {
            Assert.Equal(Rounded(150 / (100.0 / 300 + 50.0 / 200)), One("round(claudia_mean_isp(list(100, 50), list(300, 200)), 3)"));
            Assert.Equal("0", One("claudia_mean_isp(list(), list())"));
        }

        [Theory]
        [InlineData(100, 10, 0, "1")]
        [InlineData(2, 10, 0, "0.2")]
        [InlineData(0.001, 10, 0, "0.005")]
        [InlineData(100, 0, 0, "1")]
        [InlineData(100, 10, 15, "0")]
        public void The_throttle_eases_off_in_the_last_second_and_waits_while_pointing_off(double dvLeft, double accel, double angle, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_burn_throttle({0}, {1}, {2})", dvLeft, accel, angle)));
        }

        // ---- exec_node ----

        [Fact]
        public void A_half_burn_lead_starts_early_by_the_time_to_burn_half_the_node()
        {
            var printed = Run("local t is exec_node_timing(500, 10, 100, 300, 600, \"half_burn\"). print round(t:burn_time, 3). print round(t:lead, 3). print round(t:start_in, 3).");
            var lead = BurnTime(250, 10, 100, 300);
            Assert.Equal(new[] { Rounded(BurnTime(500, 10, 100, 300)), Rounded(lead), Rounded(600 - lead) }, printed);
        }

        [Fact]
        public void A_node_or_lead_string_it_does_not_know_is_refused()
        {
            Assert.Equal("", One("exec_node_unreadable(claudia_options(lexicon(\"lead\", 20), exec_node_defaults(), \"auto\"):opt)"));
            Assert.Equal("lead must be 'half_burn' or seconds, not 'halfburn'",
                One("exec_node_unreadable(claudia_options(lexicon(\"lead\", \"halfburn\"), exec_node_defaults(), \"auto\"):opt)"));
            Assert.Equal("node must be 'next' or a maneuver node, not 'first'",
                One("exec_node_unreadable(claudia_options(lexicon(\"node\", \"first\"), exec_node_defaults(), \"auto\"):opt)"));
        }

        [Fact]
        public void A_lead_in_seconds_is_used_as_given()
        {
            Assert.Equal("580", One("exec_node_timing(500, 10, 100, 300, 600, 20):start_in"));
        }
    }
}
