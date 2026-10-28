// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// land's Underhang (#427): the pure helpers that decide which parts are Underhang, what the
    /// lander alone needs, when the drop happens, how long it waits and where it lands clear of the
    /// dropped parts, on kOS.Safe through the real bundle.
    /// </summary>
    public class LandUnderhangTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string F(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        // ---- The Underhang test ----

        [Theory]
        // Feet at 2.0 m down (the lowest of the legs). A part reaching 2.2 m is at the margin, not past it.
        [InlineData("list(1.0, 2.2, 2.0)", "list(1.9, 2.0)", "")]
        // Just past the margin, and well past it (a spent stage's engine bell).
        [InlineData("list(1.0, 2.21, 2.0)", "list(1.9, 2.0)", "1")]
        [InlineData("list(5.5, 1.0, 4.0, 2.0)", "list(2.0)", "0,2")]
        // Nothing reaches past the feet.
        [InlineData("list(0.5, 1.5)", "list(2.0)", "")]
        // No landing legs: no Underhang, whatever hangs down.
        [InlineData("list(5.5, 9.0)", "list()", "")]
        public void The_Underhang_is_every_part_reaching_more_than_the_margin_past_the_legs_feet(string lows, string legLows, string expected)
        {
            var printed = Run(F("print land_underhang({0}, {1}, 0.2):join(\",\") + \"|\".", lows, legLows));
            Assert.Equal(expected, string.Join("", printed).TrimEnd('|'));
        }

        // ---- The lander-alone need ----

        [Fact]
        public void The_lander_alone_needs_a_free_fall_from_underhang_agl_plus_two_seconds_of_gravity_and_a_hover_margin_times_1_2()
        {
            // Mun, 2000 m: (sqrt(2 * 1.63 * 2000) + 2 * 1.63 + 10 * 1.63) * 1.2.
            Assert.Equal("120.368", One("round(land_lander_need(2000, 1.63), 3)"));
            // Minmus: (sqrt(2 * 0.49 * 2000) + 12 * 0.49) * 1.2.
            Assert.Equal("60.182", One("round(land_lander_need(2000, 0.49), 3)"));
            // From the ground up there is no fall, only the gap and the hover.
            Assert.Equal("14.4", One("round(land_lander_need(0, 1), 3)"));
        }

        [Fact]
        public void The_landers_own_delta_v_is_the_rocket_equation_on_its_wet_and_dry_mass()
        {
            // 320 s from 4 t to 2 t: 320 * 9.80665 * ln 2.
            Assert.Equal("2175", One("round(land_rocket_dv(320, 4, 2))"));
            // No engine, or nothing to burn.
            Assert.Equal("0", One("land_rocket_dv(0, 4, 2)"));
            Assert.Equal("0", One("land_rocket_dv(320, 2, 2)"));
        }

        [Theory]
        // An engine already lit counts whatever its stage.
        [InlineData("list(0)", "list(true)", 2, "True")]
        // An engine in a stage at or above stage_limit can be staged into.
        [InlineData("list(2)", "list(false)", 2, "True")]
        [InlineData("list(1, 0)", "list(false, false)", 2, "False")]
        // No lander engine at all.
        [InlineData("list()", "list()", 0, "False")]
        public void The_lander_needs_an_engine_lit_or_within_stage_limit(string stages, string ignited, int limit, string expected)
        {
            Assert.Equal(expected, One(F("land_lander_engine_ok({0}, {1}, {2})", stages, ignited, limit)));
        }

        // ---- The start check ----

        [Fact]
        public void The_start_check_names_the_Underhang_and_the_figures_when_the_lander_alone_falls_short()
        {
            Assert.Equal("Underhang (fuelTank, liquidEngine): the lander alone has about 90 m/s, 120 m/s needed to fly the last 2000 m once it is dropped",
                One("land_underhang_unflyable(list(\"fuelTank\", \"liquidEngine\"), 90.4, 120.37, true, 2000, 0)"));
        }

        [Fact]
        public void The_start_check_fails_with_no_lander_engine_within_stage_limit()
        {
            Assert.Equal("Underhang (liquidEngine): no lander engine at or above stage_limit 2 once it is dropped "
                + "(the lander alone has about 900 m/s, 120 m/s needed to fly the last 2000 m once it is dropped)",
                One("land_underhang_unflyable(list(\"liquidEngine\"), 900, 120, false, 2000, 2)"));
        }

        [Fact]
        public void The_start_check_passes_a_lander_that_can_fly_the_last_metres()
        {
            Assert.Equal("", One("land_underhang_unflyable(list(\"liquidEngine\"), 300, 120, true, 2000, 0)"));
        }

        // ---- The drop trigger ----

        [Theory]
        // The height crossed.
        [InlineData(1999, false, "True")]
        // Ran dry first, still high.
        [InlineData(5000, true, "True")]
        // Neither: at the height is not under it.
        [InlineData(2000, false, "False")]
        [InlineData(5000, false, "False")]
        public void The_Underhang_drops_under_underhang_agl_or_when_its_engines_run_dry(double radar, bool ranDry, string expected)
        {
            Assert.Equal(expected, One(F("land_drop_due({0}, 2000, {1})", radar, ranDry ? "true" : "false")));
        }

        [Theory]
        // Nothing has left yet: keeps waiting, up to 1 s.
        [InlineData(-1, 0.2, "False")]
        [InlineData(-1, 1, "True")]
        // 2 m clear of the craft's lowest point.
        [InlineData(1.9, 0.5, "False")]
        [InlineData(2, 0.5, "True")]
        public void The_drop_waits_until_the_parts_are_2_m_clear_or_for_1_s(double gap, double waited, string expected)
        {
            Assert.Equal(expected, One(F("land_drop_waited({0}, {1})", gap, waited)));
        }

        // ---- Landing clear of the Underhang ----

        [Fact]
        public void A_dropped_part_falls_on_at_the_crafts_speed_across_for_the_time_it_takes_to_hit()
        {
            // From 2000 m, 10 m/s down, at the Mun's 1.63 m/s^2: (sqrt(100 + 6520) - 10) / 1.63 s at 100 m/s.
            Assert.Equal("4378", One("round(land_fall_reach(2000, 10, 100, 1.63))"));
            Assert.Equal("0", One("land_fall_reach(2000, 10, 0, 1.63)"));
        }

        [Theory]
        [InlineData(50, "True")]
        [InlineData(49.9, "False")]
        [InlineData(0, "False")]
        public void A_touchdown_point_is_clear_of_the_Underhang_at_50_m_or_more(double dist, string expected)
        {
            Assert.Equal(expected, One(F("land_clear_of({0})", dist)));
        }

        [Theory]
        // Already clear: stays put.
        [InlineData(80, 300, "0")]
        // Moves the rest of the 50 m.
        [InlineData(20, 300, "30")]
        [InlineData(0, 300, "50")]
        // Never more than max_divert.
        [InlineData(0, 10, "10")]
        public void The_touchdown_point_moves_to_50_m_clear_within_max_divert(double dist, double maxDivert, string expected)
        {
            Assert.Equal(expected, One(F("land_clear_shift({0}, {1})", dist, maxDivert)));
        }

        // ---- No legs, or no Underhang: today's land ----

        [Fact]
        public void The_new_option_defaults_to_2000_m_and_is_checked()
        {
            Assert.Equal("2000", One("claudia_options(lexicon(), land_defaults(), \"auto\"):opt:underhang_agl"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"underhang_agl\", 1500), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("underhang_agl must be metres, 0 or more",
                One("land_unreadable(claudia_options(lexicon(\"underhang_agl\", -1), land_defaults(), \"auto\"):opt)"));
        }

        [Fact]
        public void Todays_results_are_unchanged_and_the_new_ones_start_at_their_defaults()
        {
            var printed = Run(
                "local x is land_extra().\n"
                + "for k in list(\"slope\", \"touchdown_speed_actual\", \"touchdown_h_speed\", \"lean\", \"reversals\", \"distance_to_target\", \"best_effort\",\n"
                + "  \"burn_start_ut\", \"braking_began_ut\", \"impact_ut\", \"biome\", \"staged_before_braking\", \"stage_left\") { print k + \"=\" + x[k]. }\n"
                + "print x:dropped_underhang:length. print x:underhang_kept:length. print x:underhang_drop_agl. print x:keys:length.");
            Assert.Equal(new[]
            {
                "slope=-1", "touchdown_speed_actual=-1", "touchdown_h_speed=-1", "lean=-1", "reversals=0", "distance_to_target=-1", "best_effort=False",
                "burn_start_ut=-1", "braking_began_ut=-1", "impact_ut=-1", "biome=", "staged_before_braking=False", "stage_left=-1",
                "0", "0", "-1", "16",
            }, printed);
        }

        [Fact]
        public void A_craft_with_no_Underhang_to_drop_gets_no_start_check_and_no_drop()
        {
            // land runs the start check and arms the drop only for a droppable Underhang: no legs or
            // nothing below them leaves land_underhang empty, so nothing is droppable.
            Assert.Equal("0", One("land_underhang(list(3, 4), list(), 0.2):length"));
            Assert.Equal("0", One("land_underhang(list(0.5, 1.5), list(2), 0.2):length"));
            var land = CoreLibrary.Files.Single(f => f.Name == "land").Source;
            Assert.Contains("if under:droppable:length > 0 {\n    local unflyable is land_underhang_unflyable(", land);
            Assert.Contains("local drop_pending is under:plan:decouplers:length > 0.", land);
        }

        // ---- Docs ----

        [Fact]
        public void The_land_docs_carry_the_option_the_results_and_the_Underhang_line()
        {
            var docs = CoreLibrary.FullDocs(new[] { "land" });
            foreach (var text in new[]
            {
                "underhang_agl [2000]", "dropped_underhang", "underhang_kept", "underhang_drop_agl",
                "Drops any Underhang (parts below the landing legs) at underhang_agl [2000] m above the ground during braking, or earlier if it runs dry, "
                + "then relights on the lander's engines and lands clear of where the Underhang falls. Fails before the deorbit burn if the lander alone can't fly the last underhang_agl m.",
            })
                Assert.Contains(text, docs);
        }
    }
}
