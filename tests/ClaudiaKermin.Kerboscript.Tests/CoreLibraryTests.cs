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
        // The block says so: burning stages even at throttle 0 (a dry stage); a coast never does.
        [InlineData("true", 0, "True")]
        [InlineData("true", 0.5, "True")]
        [InlineData("false", 0, "False")]
        [InlineData("false", 1, "False")]
        // Not said: burning means a throttle above 0, as before.
        [InlineData("\"unsaid\"", 0, "False")]
        [InlineData("\"unsaid\"", 0.3, "True")]
        public void The_tick_stages_while_the_block_is_burning_whatever_the_throttle(string burning, double tset, string expected)
        {
            Assert.Equal(expected, One("claudia_is_burning(" + burning + ", " + tset.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"));
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
        // Asked for at the start of the wait.
        [InlineData(0, 1, false, false, "warp")]
        // Warping: carry on.
        [InlineData(3000, 100, false, false, "none")]
        // At the warp's end: drop to 1x before the burn.
        [InlineData(5370, 100, false, false, "drop")]
        [InlineData(5380, 1, false, false, "none")]
        // Retry: KSP cut warp (1x again, not held, not cancelled): ask again.
        [InlineData(3000, 1, false, false, "warp")]
        // Cancel: the player lowered or stopped warp; never overridden for the rest of the wait.
        [InlineData(3000, 1, false, true, "none")]
        [InlineData(0, 1, false, true, "none")]
        // Hold: the mod holds warp at 1x (the Mission needs the player): drop, and don't warp while held.
        [InlineData(3000, 100, true, false, "drop")]
        [InlineData(3000, 1, true, false, "none")]
        // After the hold: warp again.
        [InlineData(3100, 1, false, false, "warp")]
        // Too close to the end to be worth warping.
        [InlineData(5366, 1, false, false, "none")]
        public void A_wait_asks_for_warp_whenever_it_is_at_1x_unless_held_or_cancelled(double now, double rate, bool held, bool cancelled, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_warp_step({0}, 5370, {1}, {2}, {3})", now, rate, Bool(held), Bool(cancelled))));
        }

        [Fact]
        public void A_three_orbit_wait_cut_twice_asks_again_and_is_at_1x_30_s_before_the_burn()
        {
            // The wait loop's steps over a 3-orbit wait (a 1800 s orbit), with a stand-in for KSP's warp:
            // a request raises the rate to 1000x until KSP cuts it (at t=1000 and t=2000) or the step drops it.
            var printed = Run(
                "local ut is 3 * 1800. local warp_end is claudia_warp_end(ut, 30). local rate is 1.\n"
                + "local warps is 0. local dropped_at is -1. local t is 0.\n"
                + "until t >= ut {\n"
                + "  if t = 1000 or t = 2000 { set rate to 1. }\n"
                + "  local action is claudia_warp_step(t, warp_end, rate, false, false).\n"
                + "  if action = \"warp\" { set rate to 1000. set warps to warps + 1. }\n"
                + "  if action = \"drop\" { set rate to 1. set dropped_at to t. }\n"
                + "  set t to t + 10.\n"
                + "}\n"
                + "print warps. print dropped_at. print ut - dropped_at >= 30. print rate.");
            Assert.Equal(new[] { "3", "5370", "True", "1" }, printed);
        }

        [Fact]
        public void A_cancel_stops_the_asking_for_the_rest_of_the_wait()
        {
            var printed = Run(
                "local warp_end is 5370. local rate is 1. local warps is 0. local cancelled is false. local t is 0.\n"
                + "until t >= 5400 {\n"
                + "  if t = 1000 { set rate to 1. set cancelled to true. }\n"
                + "  if claudia_warp_step(t, warp_end, rate, false, cancelled) = \"warp\" { set rate to 1000. set warps to warps + 1. }\n"
                + "  set t to t + 10.\n"
                + "}\n"
                + "print warps.");
            Assert.Equal(new[] { "1" }, printed);
        }

        // ---- Physics warp on a Coast (#339, ADR 0008) ----

        [Theory]
        // The q rule: 4x under 1 kPa, 3x under 5 kPa, 2x under 15 kPa, 1x from 15 kPa; each boundary.
        [InlineData(0, 0, "4")]
        [InlineData(999.9, 0, "4")]
        [InlineData(1000, 0, "3")]
        [InlineData(4999.9, 0, "3")]
        [InlineData(5000, 0, "2")]
        [InlineData(14999.9, 0, "2")]
        [InlineData(15000, 0, "1")]
        [InlineData(40000, 0, "1")]
        // At most 2x while steering is more than 5 degrees off; never raised by it.
        [InlineData(0, 5, "4")]
        [InlineData(0, 5.1, "2")]
        [InlineData(2000, 30, "2")]
        [InlineData(20000, 30, "1")]
        public void The_physics_rate_follows_dynamic_pressure_and_at_most_2x_off_the_steering(double qPa, double steerErr, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_q_rate({0}, {1})", qPa, steerErr)));
        }

        [Theory]
        // Rails wherever KSP allows it; physics only where it refuses and the mod allows physics warp (on a Coast).
        [InlineData(true, true, "rails")]
        [InlineData(true, false, "rails")]
        [InlineData(false, true, "physics")]
        // Off a Coast (or switched off) with rails refused: no warp at all.
        [InlineData(false, false, "none")]
        public void A_wait_chooses_rails_where_allowed_physics_on_a_coast_and_never_physics_off_one(bool railsOk, bool physicsOk, string expected)
        {
            Assert.Equal(expected, One("claudia_warp_mode(" + Bool(railsOk) + ", " + Bool(physicsOk) + ")"));
        }

        [Theory]
        // The rate to set this tick, or 0 for none. physics_end is 1000.
        // Start: ask for the rate wanted.
        [InlineData(0, 1, 4, false, false, "4")]
        // At it: nothing; thicker air: down; thinner: up.
        [InlineData(500, 4, 4, false, false, "0")]
        [InlineData(500, 4, 2, false, false, "2")]
        [InlineData(500, 2, 3, false, false, "3")]
        // Retry: cut to 1x, asked again.
        [InlineData(500, 1, 3, false, false, "3")]
        // Cancelled: never raised again, but still lowered for the air.
        [InlineData(500, 1, 4, false, true, "0")]
        [InlineData(500, 2, 4, false, true, "0")]
        [InlineData(500, 3, 2, false, true, "2")]
        // Held: back to 1x and not asked for.
        [InlineData(500, 3, 4, true, false, "1")]
        [InlineData(500, 1, 4, true, false, "0")]
        // At its end: 1x; under 5 s from it, not worth raising.
        [InlineData(1000, 4, 4, false, false, "1")]
        [InlineData(996, 1, 4, false, false, "0")]
        [InlineData(996, 2, 1, false, false, "1")]
        public void A_physics_warp_follows_the_rate_wanted_with_the_retry_hold_and_cancel_rules(double now, int rate, int want, bool held, bool cancelled, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_physics_step({0}, 1000, {1}, {2}, {3}, {4})", now, rate, want, Bool(held), Bool(cancelled))));
        }

        [Theory]
        // Physics warp is over lead s before ut, with no 30 s floor: 30 before a burn; lead 0 at the end of a coast where nothing acts: at the event.
        [InlineData(5400, 30, "5370")]
        [InlineData(5400, 0, "5400")]
        [InlineData(5400, 10, "5390")]
        public void Physics_warp_ends_the_lead_before_the_wait_ends(double ut, double lead, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_physics_end({0}, {1})", ut, lead)));
        }

        [Fact]
        public void A_coast_up_through_the_air_warps_faster_as_the_air_thins_and_is_at_1x_at_the_top()
        {
            // A stand-in launch coast: q falls from 20 kPa by 300 Pa a second; physics warp ends at the top (t = 100, lead 0).
            var printed = Run(
                "local rate is 1. local rates is list(). local t is 0.\n"
                + "until t > 100 {\n"
                + "  local want is claudia_q_rate(max(0, 20000 - 300 * t), 0).\n"
                + "  local next_rate is claudia_physics_step(t, claudia_physics_end(100, 0), rate, want, false, false).\n"
                + "  if next_rate > 0 { set rate to next_rate. rates:add(t + \":\" + next_rate). }\n"
                + "  set t to t + 1.\n"
                + "}\n"
                + "print rates:join(\",\"). print rate.");
            Assert.Equal(new[] { "17:2,51:3,64:4,100:1", "1" }, printed);
        }

        [Fact]
        public void The_default_stop_is_a_number_and_a_stop_condition_is_a_delegate()
        {
            // claudia_warp_to tells them apart with istype("Scalar") before calling the stop.
            var printed = Run("local d is { return true. }. local n is 0. print n:istype(\"Scalar\"). print d:istype(\"Scalar\"). print d:call().");
            Assert.Equal(new[] { "True", "False", "True" }, printed);
        }

        [Fact]
        public void A_stop_delegate_sets_the_blocks_own_locals()
        {
            // launch's coast runs its per-tick step as the wait's stop, which sets the block's state.
            var printed = Run("function f { local done is false. local n is 0. local step is { set n to n + 1. if n >= 3 { set done to true. } }.\n"
                + "local stop is { step:call(). return done. }. until stop:call() { } return n. }\nprint f().");
            Assert.Equal(new[] { "3" }, printed);
        }

        [Fact]
        public void There_is_one_wait_loop_taking_an_optional_stop_and_cap_and_land_coasts_through_it()
        {
            var bundle = CoreLibrary.Bundle("@lazyglobal off.\nprint 1.").Replace("\r\n", "\n");
            Assert.Contains("parameter ctx, ut, lead, stop is 0, cap is 0.", bundle);
            Assert.Contains("return claudia_warp_to(ctx, ut, 30, ground_near, cap).", bundle);
            // exec_node's wait (ascend's coast to apoapsis below the rails limit): physics warp ends 30 s or more before the burn
            // (claudia_burn_warp_lead, #405); a Burn Start over a day away is warped to a day out first, with no stop.
            Assert.Contains("claudia_warp_to(ctx, leg_ut, lead, off_burn)", bundle);
            Assert.Contains("local lead is claudia_burn_warp_lead(turned:aligned, turn_time).", bundle);
            Assert.Contains("claudia_warp_to(ctx, day_out, 30)", bundle);
            // launch's coast: to the top of the atmosphere or the hop, physics warp to the event itself (lead 0).
            Assert.Contains("claudia_warp_to(ctx, time:seconds + max(1, eta:apoapsis), 0, { fly_tick:call(). return done or not coasting. })", bundle);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(bundle, "timewarp:warpto\\("));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(bundle, "addons:claudia:waitstart\\("));
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
        public void Engines_burning_together_combine_their_isp_by_flow()
        {
            Assert.Equal(Rounded(150 / (100.0 / 300 + 50.0 / 200)), One("round(claudia_mean_isp(list(100, 50), list(300, 200)), 3)"));
            Assert.Equal("0", One("claudia_mean_isp(list(), list())"));
        }

        // A stage a burn uses, as stock's delta-v gives it: the delta-v it has, the time to burn it all, and its isp.
        private static string Stage(double mass, double dry, double thrust, double isp)
        {
            var ve = isp * 9.80665;
            var dv = ve * Math.Log(mass / dry);
            var duration = (mass - dry) * ve / thrust;
            return string.Format(CultureInfo.InvariantCulture, "lexicon(\"dv\", {0}, \"duration\", {1}, \"isp\", {2})", dv, duration, isp);
        }

        // A weak first stage (20 t to 14 t at 60 kN, isp 250), then a strong second with the first's tanks dropped (6 t to
        // 3 t at 200 kN, isp 320).
        private static readonly string TwoStages = "list(" + Stage(20, 14, 60, 250) + ", " + Stage(6, 3, 200, 320) + ")";

        [Fact]
        public void A_burn_within_one_stage_takes_the_rocket_equation_time()
        {
            Assert.Equal(Rounded(BurnTime(500, 10, 100, 300)), One("round(claudia_staged_burn_time(500, list(" + Stage(10, 5, 100, 300) + ")), 3)"));
        }

        [Fact]
        public void A_burn_past_a_stage_burns_it_all_then_the_next_stage_from_its_own_mass()
        {
            var firstDv = 250 * 9.80665 * Math.Log(20.0 / 14);
            var expected = (20.0 - 14) * 250 * 9.80665 / 60 + BurnTime(1200 - firstDv, 6, 200, 320);
            Assert.Equal(Rounded(expected), One("round(claudia_staged_burn_time(1200, " + TwoStages + "), 3)"));
        }

        [Fact]
        public void Stages_with_no_delta_v_are_skipped_and_a_burn_past_the_last_stage_runs_on_at_its_rate()
        {
            var empty = "lexicon(\"dv\", 0, \"duration\", 0, \"isp\", 0)";
            var dv = 300 * 9.80665 * Math.Log(2);
            var duration = 5.0 * 300 * 9.80665 / 100;
            Assert.Equal(Rounded(duration + 100 * duration / dv),
                One(string.Format(CultureInfo.InvariantCulture, "round(claudia_staged_burn_time({0}, list({1}, {2})), 3)", dv + 100, empty, Stage(10, 5, 100, 300))));
            Assert.Equal("-1", One("claudia_staged_burn_time(100, list(" + empty + "))"));
            Assert.Equal("-1", One("claudia_staged_burn_time(100, list())"));
        }

        [Theory]
        [InlineData(1.5, 0.2, 0, "True")]
        [InlineData(2.5, 0.2, 0, "False")]
        [InlineData(1.5, 0.6, 0, "False")]
        // Within the tolerance for 3 s while the target itself turns: Aligned whatever the rate.
        [InlineData(1.5, 0.6, 2.9, "False")]
        [InlineData(1.5, 0.6, 3, "True")]
        [InlineData(2.5, 0.2, 10, "False")]
        public void Aligned_is_within_2_degrees_and_turning_under_half_a_degree_a_second_or_within_for_3_s(double angle, double rate, double held, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_aligned({0}, {1}, {2}, claudia_rcs_defaults())", angle, rate, held)));
        }

        [Theory]
        [InlineData(4.5, 0.6, 0, "True")]
        [InlineData(4.5, 0.9, 0, "False")]
        [InlineData(5.5, 0.1, 0, "False")]
        public void Aligned_takes_its_thresholds_from_rcs_tolerance(double angle, double rate, double held, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture,
                "claudia_aligned({0}, {1}, {2}, claudia_options(lexicon(\"rcs_tolerance\", lexicon(\"aligned_deg\", 5, \"aligned_rate\", 0.8)), exec_node_defaults(), \"auto\"):opt:rcs_tolerance)", angle, rate, held)));
        }

        [Theory]
        // Not within: no hold. Coming within starts the hold now; staying within keeps its start.
        [InlineData(-1, false, 100, "-1")]
        [InlineData(90, false, 100, "-1")]
        [InlineData(-1, true, 100, "100")]
        [InlineData(90, true, 100, "90")]
        public void The_hold_within_tolerance_starts_when_the_error_comes_within_and_ends_when_it_leaves(double since, bool within, double now, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_within_ut({0}, {1}, {2})", since, within, now)));
        }

        // ---- RCS Use ----

        // One tick's state for claudia_rcs_decide: a craft with RCS off, steering, monoprop, no steering error and not turning, the gate
        // not due, wheels that turn in 40 s and no deadline; each case overrides what it needs.
        private static string Decide(string overrides, string tolerance = "claudia_rcs_defaults()")
        {
            return Run("local s is claudia_merge_options(lexicon(" + overrides + "), lexicon(\"rcs\", false, \"steering\", true, \"monoprop\", 30, \"translating\", false,\"error\", 0, "
                + "\"rate\", 0, \"held_s\", 0, \"gate\", false, \"wheels_s\", 40, \"left_s\", -1)).\n"
                + "local d is claudia_rcs_decide(s, " + tolerance + "). print d[\"on\"] + \"|\" + d:reason.").Single();
        }

        [Theory]
        // Over on_deg, the gate due: with a deadline the wheels can't make, turning; one they can, nothing changes.
        [InlineData("\"error\", 34, \"gate\", true, \"left_s\", 30", "True|turning")]
        [InlineData("\"error\", 34, \"gate\", true, \"left_s\", 60", "False|")]
        // With no deadline (launch, ascend, land's braking), crossing on_deg is enough.
        [InlineData("\"error\", 34, \"gate\", true", "True|falling behind")]
        // Wheels with no pitch or yaw torque, with or without a deadline.
        [InlineData("\"error\", 34, \"gate\", true, \"wheels_s\", -1", "True|no wheel torque")]
        [InlineData("\"error\", 34, \"gate\", true, \"wheels_s\", -1, \"left_s\", 600", "True|no wheel torque")]
        // Held off: at or under on_deg, or over it with the gate not due (it is checked once a second).
        [InlineData("\"error\", 10, \"gate\", true", "False|")]
        [InlineData("\"error\", 9, \"gate\", true, \"wheels_s\", -1", "False|")]
        [InlineData("\"error\", 34, \"gate\", false", "False|")]
        public void RCS_goes_on_over_on_deg_only_when_the_wheels_cannot_make_the_turn(string state, string expected)
        {
            Assert.Equal(expected, Decide(state));
        }

        [Theory]
        // Aligned by the rate, or by 3 s within aligned_deg while the target turns.
        [InlineData("\"rcs\", true, \"error\", 1.5, \"rate\", 0.2", "False|aligned")]
        [InlineData("\"rcs\", true, \"error\", 1.5, \"rate\", 0.9, \"held_s\", 3", "False|aligned")]
        // Between the thresholds, or within aligned_deg but still turning and not yet held 3 s: stays on.
        [InlineData("\"rcs\", true, \"error\", 6, \"rate\", 0.1", "True|")]
        [InlineData("\"rcs\", true, \"error\", 1.5, \"rate\", 0.9, \"held_s\", 2", "True|")]
        [InlineData("\"rcs\", true, \"error\", 34, \"rate\", 3, \"gate\", true", "True|")]
        public void RCS_goes_off_once_Aligned_and_stays_on_between_the_thresholds(string state, string expected)
        {
            Assert.Equal(expected, Decide(state));
        }

        [Theory]
        [InlineData("\"rcs\", true, \"steering\", false, \"error\", 34", "False|steering released")]
        [InlineData("\"steering\", false, \"error\", 34, \"gate\", true", "False|")]
        // No monopropellant: off, and never back on, whatever else says on.
        [InlineData("\"rcs\", true, \"monoprop\", 0, \"error\", 34", "False|no monoprop")]
        [InlineData("\"monoprop\", 0, \"error\", 34, \"gate\", true, \"wheels_s\", -1", "False|")]
        public void RCS_goes_off_with_steering_released_or_no_monoprop(string state, string expected)
        {
            Assert.Equal(expected, Decide(state));
        }

        [Theory]
        // Translating holds RCS on, reported once; whatever else says off (Aligned, steering released) is ignored.
        [InlineData("\"translating\", true", "True|translating")]
        [InlineData("\"translating\", true, \"steering\", false", "True|translating")]
        [InlineData("\"translating\", true, \"rcs\", true", "True|")]
        [InlineData("\"translating\", true, \"rcs\", true, \"error\", 0.5, \"rate\", 0.1, \"held_s\", 5", "True|")]
        [InlineData("\"translating\", true, \"rcs\", true, \"steering\", false", "True|")]
        // Off without monopropellant, even translating.
        [InlineData("\"translating\", true, \"rcs\", true, \"monoprop\", 0", "False|no monoprop")]
        // Cleared, the rule resumes: Aligned switches it off.
        [InlineData("\"rcs\", true, \"error\", 0.5, \"rate\", 0.1", "False|aligned")]
        public void Translating_holds_RCS_on_and_decides_nothing_else_until_cleared(string state, string expected)
        {
            Assert.Equal(expected, Decide(state));
        }

        [Fact]
        public void A_custom_rcs_tolerance_moves_the_thresholds()
        {
            const string tolerance = "claudia_options(lexicon(\"rcs_tolerance\", lexicon(\"on_deg\", 30, \"aligned_deg\", 5)), exec_node_defaults(), \"auto\"):opt:rcs_tolerance";
            Assert.Equal("False|", Decide("\"error\", 25, \"gate\", true", tolerance));
            Assert.Equal("True|falling behind", Decide("\"error\", 31, \"gate\", true", tolerance));
            Assert.Equal("False|aligned", Decide("\"rcs\", true, \"error\", 4, \"rate\", 0.2", tolerance));
        }

        [Fact]
        public void A_partial_rcs_tolerance_merges_with_the_defaults()
        {
            var printed = Run("local c is claudia_options(lexicon(\"rcs_tolerance\", lexicon(\"aligned_deg\", 1)), exec_node_defaults(), \"auto\"). print c:error. "
                + "local t is c:opt:rcs_tolerance. print t:on_deg. print t:aligned_deg. print t:aligned_rate.\n"
                + "local d is claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt:rcs_tolerance. print d:on_deg. print d:aligned_deg. print d:aligned_rate.");
            Assert.Equal(new[] { "", "10", "1", "0.5", "10", "2", "0.5" }, printed);
        }

        [Theory]
        [InlineData("5")]
        [InlineData("\"tight\"")]
        [InlineData("lexicon(\"on_dg\", 5)")]
        [InlineData("lexicon(\"on_deg\", \"5\")")]
        public void A_bad_rcs_tolerance_fails_the_block(string value)
        {
            Assert.Equal("rcs_tolerance must be a lexicon of on_deg, aligned_deg (degrees) and aligned_rate (deg/s), given as numbers",
                One("claudia_options(lexicon(\"rcs_tolerance\", " + value + "), exec_node_defaults(), \"auto\"):error"));
        }

        [Theory]
        // Checked when the error first crosses on_deg, then once a second while it stays over.
        [InlineData(34, false, 100, 50, "True")]
        [InlineData(34, true, 100, 99.5, "False")]
        [InlineData(34, true, 100, 99, "True")]
        [InlineData(8, false, 100, 50, "False")]
        public void The_on_gate_is_checked_at_the_crossing_then_once_a_second(double error, bool wasAbove, double now, double checkedUt, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_rcs_gate_due({0}, 10, {1}, {2}, {3})", error, wasAbove, now, checkedUt)));
        }

        [Theory]
        // On time, early, or within a second of the start (a tick, warp settling): not late.
        [InlineData(1000, 1000, "0")]
        [InlineData(1000.6, 1000, "0")]
        [InlineData(990, 1000, "0")]
        // The start has passed: the burn starts at once and says by how much.
        [InlineData(1012.34, 1000, "12.3")]
        public void A_burn_that_starts_after_its_start_reports_late_start_s(double now, double start, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_late_start({0}, {1})", now, start)));
        }

        [Theory]
        // On time, early, or within a second of the start: the node stays where it was planned (start plus the 30 s lead).
        [InlineData(1000, 1000, "1030")]
        [InlineData(990, 1000, "1030")]
        [InlineData(1000.6, 1000, "1030")]
        // #363: a burn still waiting to point at its node a minute after its start takes the node with it.
        [InlineData(1060, 1000, "1090")]
        public void A_burn_that_starts_late_moves_its_node_by_as_much(double now, double start, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "exec_node_due_ut({0}, {1}, 30)", now, start)));
        }

        [Theory]
        // Warp to a burn is over max(30 s, the turn time) before its start.
        [InlineData(1000, 20, "970")]
        [InlineData(1000, 75, "925")]
        public void Warp_to_a_burn_ends_the_turn_time_before_its_start(double start, double turn, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_warp_end({0}, {1})", start, turn)));
        }

        [Theory]
        // #405: a craft Aligned before the warp needs only 30 s at 1x before the Burn Start; one whose turn
        // timed out keeps its turn time (never under 30 s) to turn at 1x.
        [InlineData("true", 143.8, "30")]
        [InlineData("false", 143.8, "143.8")]
        [InlineData("false", 20, "30")]
        [InlineData("true", 20, "30")]
        public void The_warp_lead_before_a_burn_is_30_s_once_aligned(string aligned, double turn, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_burn_warp_lead({0}, {1})", aligned, turn)));
        }

        [Theory]
        // #405: a Burn Start over a day away is warped to a day before it first, with no turn (-1: none).
        [InlineData(0, 2 * 6 * 3600, 6, "21600")]
        [InlineData(0, 2 * 3600, 6, "-1")]
        [InlineData(0, 30 * 3600, 24, "21600")]
        [InlineData(1000, 1000 + 6 * 3600, 6, "-1")]
        [InlineData(1000, 1001 + 6 * 3600, 6, "1001")]
        public void A_burn_start_over_a_day_away_is_warped_to_a_day_out_first(double now, double start, double hoursPerDay, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_day_out_ut({0}, {1}, {2})", now, start, hoursPerDay)));
        }

        [Theory]
        // No SOI change, one behind us, or one too close to the burn to turn between: straight to the start.
        [InlineData(0, -1, 1000, 20, "1000")]
        [InlineData(500, 400, 1000, 20, "1000")]
        [InlineData(0, 975, 1000, 20, "1000")]
        // An SOI change well before the burn: warp to just past it, turn, warp on.
        [InlineData(0, 20, 1000, 20, "1000")]
        [InlineData(0, 400, 1000, 20, "401")]
        [InlineData(0, 400, 1000, 75, "401")]
        [InlineData(0, 925, 1000, 75, "1000")]
        public void A_warp_to_a_burn_goes_one_SOI_at_a_time(double now, double patch, double start, double lead, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "claudia_soi_leg_ut({0}, {1}, {2}, {3})", now, patch, start, lead)));
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
            var printed = Run("local t is claudia_burn_timing(500, list(" + Stage(10, 5, 100, 300) + "), 600, \"half_burn\"). print round(t:burn_time, 3). print round(t:lead, 3). print round(t:start_in, 3).");
            var lead = BurnTime(250, 10, 100, 300);
            Assert.Equal(new[] { Rounded(BurnTime(500, 10, 100, 300)), Rounded(lead), Rounded(600 - lead) }, printed);
        }

        [Fact]
        public void A_long_multi_stage_burn_starts_by_half_its_delta_v_not_half_its_time()
        {
            // The weak first stage burns most of the time for little delta-v, so half the delta-v takes well over half the
            // burn: "node minus half the burn" would start late.
            var printed = Run("local t is claudia_burn_timing(1200, " + TwoStages + ", 3600, \"half_burn\"). print t:lead > t:burn_time / 2 + 10. print round(t:lead, 3). print round(t:start_in, 3).");
            var lead = double.Parse(One("round(claudia_staged_burn_time(600, " + TwoStages + "), 3)"), CultureInfo.InvariantCulture);
            Assert.Equal(new[] { "True", Rounded(lead), Rounded(3600 - lead) }, printed);
        }

        [Fact]
        public void With_no_stage_to_burn_there_is_no_burn_time()
        {
            Assert.Equal("-1", One("claudia_burn_timing(500, list(), 600, \"half_burn\"):burn_time"));
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
            Assert.Equal("580", One("claudia_burn_timing(500, list(" + Stage(10, 5, 100, 300) + "), 600, 20):start_in"));
        }
    }
}
