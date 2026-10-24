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
            return KosHarness.Run(CoreLibrary.Bundle(missionScript, 1, 10800)).ToArray();
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
            Assert.Null(KosHarness.CompileError(CoreLibrary.Bundle("@lazyglobal off.\nprint 1.", 1, 10800)));
        }

        [Fact]
        public void A_mission_script_can_call_the_library_and_read_the_wait_caps()
        {
            var bundle = CoreLibrary.Bundle("@lazyglobal off.\nprint claudia_config():wait_cap_orbits.\nprint claudia_config():wait_cap_ground_s.", 1.5, 7200);
            Assert.Equal(new[] { "1.5", "7200" }, KosHarness.Run(bundle));
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
        [InlineData(3, 0, false, false, "none")]
        [InlineData(3, 0, true, false, "stage")]
        [InlineData(3, 2, true, false, "stage")]
        [InlineData(2, 2, true, false, "out_of_fuel")]
        [InlineData(1, 2, true, false, "out_of_fuel")]
        [InlineData(0, 0, true, false, "out_of_fuel")]
        [InlineData(3, 0, true, true, "blocked")]
        public void Staging_happens_on_flameout_down_to_the_limit_and_never_into_chutes(int stage, int limit, bool needs, bool unsafeNext, string expected)
        {
            Assert.Equal(expected, One("claudia_stage_action(" + stage + ", " + limit + ", " + Bool(needs) + ", " + Bool(unsafeNext) + ")"));
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        // ---- Waits ----

        [Theory]
        [InlineData(false, 1800, 1, 10800, "1800")]
        [InlineData(false, 1800, 2, 10800, "3600")]
        [InlineData(true, 1800, 1, 10800, "10800")]
        [InlineData(false, 0, 1, 10800, "10800")]
        public void Waits_are_capped_at_one_orbit_or_three_hours_on_the_ground(bool onGround, double period, double orbits, double ground, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_wait_cap({0}, {1}, {2}, {3})", Bool(onGround), period, orbits, ground)));
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
        public void A_lead_in_seconds_is_used_as_given()
        {
            Assert.Equal("580", One("exec_node_timing(500, 10, 100, 300, 600, 20):start_in"));
        }
    }
}
