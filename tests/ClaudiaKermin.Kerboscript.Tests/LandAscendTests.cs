// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// The land and ascend blocks' pure helpers on kOS.Safe, through the real bundle. Expected
    /// values are worked examples (Mun figures: mu 6.5138398e10, radius 200 km, day 138984.48 s).
    /// </summary>
    public class LandAscendTests
    {
        private const string MunMu = "6.5138398e10";

        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        // ---- land: options ----

        [Fact]
        public void Land_options_take_the_spec_defaults()
        {
            var printed = Run(
                "local o is claudia_options(lexicon(), land_defaults(), \"auto\"):opt.\n"
                + "print o:target. print o:daylight. print o:max_slope. print o:max_divert.\n"
                + "print o:touchdown_speed. print o:gear_alt. print o:burn_margin. print o:max_duration.");
            Assert.Equal(new[] { "any", "True", "8", "300", "2", "1000", "1.1", "auto" }, printed);
        }

        // ---- land: daylight past the old one-orbit cap (#122, ADR 0004) ----

        [Fact]
        public void Land_looks_a_whole_day_of_the_body_ahead_for_a_daylight_pass()
        {
            // The Mun's day is 138984 s, about 70 of a low orbit's 2000 s: far past the old one-orbit cap.
            Assert.Equal("166781.376", One("round(land_daylight_horizon(138984.48), 3)"));
            Assert.Equal("True", One("land_daylight_horizon(138984.48) > 138984.48"));
        }

        [Theory]
        [InlineData(0, 30, "True")]
        [InlineData(20000, 5, "True")]
        [InlineData(20001, 30, "False")]
        [InlineData(0, 4.9, "False")]
        [InlineData(0, -40, "False")]
        public void A_daylight_pass_is_within_20_km_of_the_track_with_the_sun_5_degrees_up(double cross, double sun, string expected)
        {
            Assert.Equal(expected, One(string.Format(System.Globalization.CultureInfo.InvariantCulture, "land_lit_near({0}, {1})", cross, sun)));
        }

        [Fact]
        public void Land_rejects_an_unknown_option_by_name()
        {
            Assert.Equal("unknown option max_slop", One("claudia_options(lexicon(\"max_slop\", 5), land_defaults(), \"auto\"):error"));
        }

        [Fact]
        public void Land_reads_a_target_as_any_or_a_lexicon_of_lat_and_lng()
        {
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"target\", \"any\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"lat\", 1, \"lng\", 2)), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("target must be \"any\", LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or lexicon(\"biome\", name), not 'somewhere'",
                One("land_unreadable(claudia_options(lexicon(\"target\", \"somewhere\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("target must be \"any\", LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or lexicon(\"biome\", name), not a lexicon without lat and lng",
                One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"lat\", 1)), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_slope must be a number of degrees from 0 to 45",
                One("land_unreadable(claudia_options(lexicon(\"max_slope\", 60), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("touchdown_speed must be a speed above 0 m/s",
                One("land_unreadable(claudia_options(lexicon(\"touchdown_speed\", 0), land_defaults(), \"auto\"):opt)"));
        }

        // ---- land: a biome target (#281) ----

        private static string Unreadable(string target)
        {
            return One("land_unreadable(claudia_options(lexicon(\"target\", " + target + "), land_defaults(), \"auto\"):opt)");
        }

        [Theory]
        [InlineData("lexicon(\"biome\", \"Midlands\")")]
        [InlineData("lexicon(\"BIOME\", \"Northern Basin\")")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", lexicon(\"lat\", 2, \"lng\", -30))")]
        public void Land_reads_a_biome_target_with_an_optional_near_point(string target)
        {
            Assert.Equal("", Unreadable(target));
            Assert.Equal("True", One("land_is_biome(" + target + ")"));
        }

        [Theory]
        [InlineData("lexicon(\"biome\", \"\")", "target's biome must be a biome's name, e.g. \"Midlands\"")]
        [InlineData("lexicon(\"biome\", \"  \")", "target's biome must be a biome's name, e.g. \"Midlands\"")]
        [InlineData("lexicon(\"biome\", 3)", "target's biome must be a biome's name, e.g. \"Midlands\"")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"lat\", 1)", "a biome target takes only biome and near, not lat")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", lexicon(\"lat\", 1))", "target's near must be LATLNG(lat, lng) or lexicon(\"lat\", x, \"lng\", y) with numbers")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", lexicon(\"lat\", 1, \"lng\", \"west\"))", "target's near must be LATLNG(lat, lng) or lexicon(\"lat\", x, \"lng\", y) with numbers")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", \"ksc\")", "target's near must be LATLNG(lat, lng) or a lat/lng lexicon, not a String")]
        public void Land_says_why_a_biome_target_is_unreadable(string target, string expected)
        {
            Assert.Equal(expected, Unreadable(target));
        }

        [Fact]
        public void A_biome_targets_other_options_are_still_read()
        {
            Assert.Equal("max_slope must be a number of degrees from 0 to 45",
                One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"biome\", \"Midlands\"), \"max_slope\", 60), land_defaults(), \"auto\"):opt)"));
        }

        [Theory]
        [InlineData("\"any\"")]
        [InlineData("lexicon(\"lat\", 1, \"lng\", 2)")]
        public void A_site_or_any_is_not_a_biome_target(string target)
        {
            Assert.Equal("False", One("land_is_biome(" + target + ")"));
        }

        [Fact]
        public void A_biome_target_asks_the_site_search_for_its_biome_daylight_and_slope_and_leaves_the_window_to_its_default()
        {
            // No within_hours: the search looks as far as find_landing_site does by default (the time cap), not 3 orbits.
            var printed = Run(
                "local q is land_site_query(lexicon(\"biome\", \"Midlands\"), true, 8).\n"
                + "print q:biome. print q:daylight. print q:max_slope_deg. print q:haskey(\"within_hours\"). print q:haskey(\"near\").");
            Assert.Equal(new[] { "Midlands", "True", "8", "False", "False" }, printed);
        }

        [Fact]
        public void The_pass_search_starts_an_orbit_before_a_far_pass_and_two_minutes_on_for_a_near_or_no_one()
        {
            // A 2000 s orbit, now 1000: a pass at 13000 is searched from an orbit before it, 11000.
            Assert.Equal("11000", One("land_search_start(1000, 13000, 2000)"));
            // No pass given (-1), or one inside the first orbit: now + 120.
            Assert.Equal("1120", One("land_search_start(1000, -1, 2000)"));
            Assert.Equal("1120", One("land_search_start(1000, 2500, 2000)"));
        }

        [Fact]
        public void A_biome_targets_near_point_goes_to_the_site_search_as_latitude_and_longitude()
        {
            var printed = Run(
                "local q is land_site_query(lexicon(\"biome\", \"Flats\", \"near\", lexicon(\"lat\", 2.5, \"lng\", -30)), false, 5).\n"
                + "print q:near:latitude. print q:near:longitude. print q:daylight.");
            Assert.Equal(new[] { "2.5", "-30", "False" }, printed);
        }

        [Fact]
        public void A_max_slope_of_0_asks_the_site_search_for_the_flattest_it_takes()
        {
            // land allows max_slope 0; the search wants more than 0.
            Assert.Equal("0.1", One("land_site_query(lexicon(\"biome\", \"Flats\"), false, 0):max_slope_deg"));
        }

        [Fact]
        public void A_Mission_Script_function_named_like_a_library_function_replaces_it()
        {
            // Why the Script Check refuses such a function (ADR 0007): the Mission Script's wins, inside the blocks too.
            Assert.Equal(new[] { "999" }, Run("function land_wrap_lng { parameter x. return 999. }\nprint land_wrap_lng(10)."));
        }

        [Theory]
        [InlineData("Midlands", "midlands", "True")]
        [InlineData("NorthernBasin", "Northern Basin", "True")]
        [InlineData("Highlands", "Midlands", "False")]
        [InlineData("", "Midlands", "False")]
        public void Biome_names_match_ignoring_case_and_spaces(string landed, string asked, string expected)
        {
            Assert.Equal(expected, One("land_same_biome(\"" + landed + "\", \"" + asked + "\")"));
        }

        // ---- land: geometry and orbits ----

        [Fact]
        public void Land_measures_distance_along_the_surface()
        {
            Assert.Equal("314159.265", One("round(land_surface_distance(0, 0, 0, 90, 200000), 3)"));
            Assert.Equal("314159.265", One("round(land_surface_distance(-45, 10, 45, 10, 200000), 3)"));
        }

        [Fact]
        public void Land_knows_where_a_site_will_have_turned_to()
        {
            Assert.Equal("19.3248", One("round(land_site_lng(10, 3600, 138984.48), 4)"));
            Assert.Equal("-171.6752", One("round(land_site_lng(179, 3600, 138984.48), 4)"));
            Assert.Equal("170", One("land_wrap_lng(-190)"));
        }

        [Fact]
        public void Land_plans_the_deorbit_burn_by_vis_viva()
        {
            // From a 30 km circular Mun orbit to a 3 km periapsis.
            Assert.Equal("-16.859", One("round(land_deorbit_dv(" + MunMu + ", 230000, sqrt(" + MunMu + " / 230000), 203000), 3)"));
            Assert.Equal("1239.99", One("round(land_transfer_time(" + MunMu + ", 230000, 203000), 2)"));
            Assert.Equal("583.856", One("round(land_periapsis_speed(" + MunMu + ", 230000, 203000), 3)"));
        }

        // #214: land reports when the orbit after its deorbit burn meets the ground, so the block test
        // can check braking starts before it. From the apoapsis of the 230 km by 203 km descent orbit:
        // the ground at 201 km is never reached; 210 km comes before periapsis, half the 1240 s orbit away.
        [Fact]
        public void The_fall_time_to_a_radius_is_shared_and_is_none_for_ground_under_periapsis()
        {
            const string Descent = "0.06235565819861432, 216500, " + MunMu;
            Assert.Equal("-1", One("claudia_fall_eta(" + Descent + ", 201000, 180)"));
            var eta = double.Parse(One("claudia_fall_eta(" + Descent + ", 210000, 180)"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(eta, 800, 1239);
            Assert.Equal(One("reentry_entry_eta(" + Descent + ", 210000, 180)"), One("claudia_fall_eta(" + Descent + ", 210000, 180)"));
        }

        [Fact]
        public void Land_documents_its_burn_start_and_impact_times()
        {
            var land = CoreLibrary.FullDocs(new[] { "land" });
            Assert.Contains("burn_start_ut", land);
            Assert.Contains("impact_ut", land);
        }

        // ---- land: descent guidance ----

        [Fact]
        public void Land_starts_braking_at_the_stopping_distance_with_margin()
        {
            Assert.Equal("27500", One("round(land_stop_distance(500, 5, 1.1), 3)"));
        }

        [Fact]
        public void Land_works_out_the_time_to_impact_from_height_and_downward_speed()
        {
            // Dropped from 100 m at 1.62 m/s^2: sqrt(2 * 100 / 1.62).
            Assert.Equal("11.111", One("round(land_fall_eta(100, 0, 1.62), 3)"));
            // Already moving down: 100 = 20 t + 0.81 t^2.
            Assert.Equal("4.264", One("round(land_fall_eta(100, 20, 1.62), 3)"));
            // Climbing away still comes back down, later.
            Assert.Equal("True", One("land_fall_eta(100, -10, 1.62) > land_fall_eta(100, 0, 1.62)"));
            Assert.Equal("0", One("land_fall_eta(0, 5, 1.62)"));
            Assert.Equal("0", One("land_fall_eta(-5, 5, 1.62)"));
        }

        [Theory]
        // Nominal coast: shallow descent, ground far off.
        [InlineData(80, 20, "False")]
        // Over-burned deorbit: 200 m/s down needs 55 s to stop (with margin), ground in 25 s.
        [InlineData(25, 200, "True")]
        // Same fall, but the ground is further than the braking time plus the pad.
        [InlineData(120, 200, "False")]
        // Rising: nothing to stop, but the pad still applies.
        [InlineData(5, -10, "True")]
        [InlineData(0, 0, "True")]
        public void Land_ends_the_coast_when_the_ground_comes_inside_the_braking_time(double eta, double down, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "land_brake_now({0}, {1}, 4, 1.1)", eta, down)));
        }

        [Theory]
        // Braking is due at a time to impact of 100 / 4 * 1.1 + 30 = 57.5 s (v_down 100, accel 4, margin 1.1):
        // 4x until 60 s before that, 2x from there, 1x from 30 s before.
        [InlineData(300, 4)]
        [InlineData(120, 4)]
        [InlineData(117, 2)]
        [InlineData(90, 2)]
        [InlineData(87, 1)]
        [InlineData(57, 1)]
        public void Land_warps_at_4x_then_2x_from_60_s_and_1x_from_30_s_before_braking(double eta, int expected)
        {
            Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), One(string.Format(CultureInfo.InvariantCulture, "land_warp_cap({0}, 100, 4, 1.1)", eta)));
        }

        [Fact]
        public void Land_is_at_1x_once_braking_is_due()
        {
            Assert.Equal("1", One("land_warp_cap(10, 100, 4, 1.1)"));
        }

        [Fact]
        public void Land_fails_after_the_deorbit_burn_when_periapsis_ends_well_under_the_plan()
        {
            // Planned 3200 m; 1500 m of slack.
            Assert.Equal("True", One("land_periapsis_ok(3100, 3200)"));
            Assert.Equal("True", One("land_periapsis_ok(1700, 3200)"));
            Assert.Equal("False", One("land_periapsis_ok(1600, 3200)"));
            Assert.Equal("False", One("land_periapsis_ok(-4000, 3200)"));
            // Higher than planned is fine for this check.
            Assert.Equal("True", One("land_periapsis_ok(9000, 3200)"));
        }

        [Fact]
        public void Land_closes_on_the_site_along_a_stopping_curve_then_gently()
        {
            // sqrt(2 a s + (2 a)^2) - 2 a, a = 4 / margin: the stopping curve, eased onto the site.
            Assert.Equal("81.8", One("round(land_vh_target(1000, 4, 1), 3)"));
            Assert.Equal("59.372", One("round(land_vh_target(1000, 4, 2), 3)"));
            Assert.Equal("4", One("round(land_vh_target(10, 4, 1), 3)"));
            // About half the distance per second at the last few metres.
            Assert.Equal("0.485", One("round(land_vh_target(1, 4, 1), 3)"));
            Assert.Equal("0", One("land_vh_target(0, 4, 1)"));
        }

        [Fact]
        public void Land_descends_no_faster_than_it_can_stop_and_touches_down_at_touchdown_speed()
        {
            // Far above the approach height: limited by what it can stop from (2 m/s^2 over 1000 m).
            Assert.Equal("-63.246", One("round(land_vv_target(1000, 0, 2, 2), 3)"));
            // At the ground: the touchdown speed.
            Assert.Equal("-2", One("land_vv_target(1, 0, 2, 2)"));
            Assert.Equal("-2", One("land_vv_target(100, 90, 2, 2)"));
            // Below the approach height (terrain rising): climbs, at most 20 m/s.
            Assert.Equal("20", One("land_vv_target(500, 1000, 2, 2)"));
            Assert.Equal("5", One("land_vv_target(950, 1000, 2, 2)"));
            // However high and however hard it can stop: never over 100 m/s down (#365's ran to 240).
            Assert.Equal("-100", One("land_vv_target(20000, 0, 5, 2)"));
        }

        [Fact]
        public void Land_glides_down_towards_the_site()
        {
            Assert.Equal("3000", One("land_approach_height(40000, 3000)"));
            Assert.Equal("1500", One("land_approach_height(10000, 3000)"));
            // Levels at 100 m for the last few hundred metres, and comes down only over the site.
            Assert.Equal("100", One("land_approach_height(600, 3000)"));
            Assert.Equal("100", One("land_approach_height(30, 3000)"));
            Assert.Equal("0", One("land_approach_height(20, 3000)"));
        }

        [Fact]
        public void Land_keeps_upright_near_the_ground()
        {
            Assert.Equal("5", One("land_max_tilt(0)"));
            Assert.Equal("25", One("land_max_tilt(10)"));
            Assert.Equal("90", One("land_max_tilt(50)"));
        }

        [Fact]
        public void Land_gives_vertical_acceleration_first()
        {
            Assert.Equal(new[] { "6", "8" }, Run("local s is land_split_accel(10, 6, 10). print s:v. print round(s:h, 3)."));
            Assert.Equal(new[] { "10", "0" }, Run("local s is land_split_accel(10, 12, 5). print s:v. print round(s:h, 3)."));
            Assert.Equal(new[] { "0", "4" }, Run("local s is land_split_accel(10, -3, 4). print s:v. print round(s:h, 3)."));
        }

        // ---- land: braking once (#233) ----

        [Fact]
        public void Land_takes_an_overshoot_tolerance_in_metres()
        {
            Assert.Equal("500", One("claudia_options(lexicon(), land_defaults(), \"auto\"):opt:max_overshoot"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"max_overshoot\", 50), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_overshoot must be metres from 0 to 10000",
                One("land_unreadable(claudia_options(lexicon(\"max_overshoot\", -1), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_overshoot must be metres from 0 to 10000",
                One("land_unreadable(claudia_options(lexicon(\"max_overshoot\", \"near\"), land_defaults(), \"auto\"):opt)"));
            var land = CoreLibrary.FullDocs(new[] { "land" });
            foreach (var field in new[] { "max_overshoot [500]", "reversals", "touchdown_h_speed", "lean" })
            {
                Assert.Contains(field, land);
            }
        }

        [Fact]
        public void Land_brakes_along_its_stopping_curve_and_eases_onto_the_site()
        {
            // On land_vh_target's curve (4 m/s^2 at margin 1, 1000 m out: 81.8 m/s): the curve's own
            // deceleration, 4 v / (v + 8), never over the 4 m/s^2 it plans for.
            Assert.Equal("-3.644", One("round(land_axis_accel(1000, land_vh_target(1000, 4, 1), 4, 1), 3)"));
            // Easing off near the site: 10 m out at 4 m/s.
            Assert.Equal("-1.333", One("round(land_axis_accel(10, 4, 4, 1), 3)"));
            // Too fast to stop on the site: everything it has; it lands long rather than turning back.
            Assert.Equal("-4", One("round(land_axis_accel(1000, 200, 4, 1), 3)"));
            // Stopped short: closes on the site.
            Assert.Equal("4", One("round(land_axis_accel(1000, 0, 4, 1), 3)"));
        }

        [Fact]
        public void Land_only_stops_once_past_the_site()
        {
            // Past the site and still moving on: brakes, never pushes back towards the site.
            Assert.Equal("-4", One("round(land_axis_accel(-50, 20, 4, 1), 3)"));
            // Slowly (over 4 s) as it nears a stop, so a lagging turn doesn't carry it through zero.
            Assert.Equal("-0.25", One("round(land_axis_accel(-50, 1, 4, 1), 3)"));
            Assert.Equal("0", One("land_axis_accel(-50, 0, 4, 1)"));
            // Drifting back towards it: pushed the way it was going until stopped.
            Assert.Equal("0.125", One("round(land_axis_accel(-50, -0.5, 4, 1), 3)"));
        }

        [Fact]
        public void Land_closes_across_its_path_from_either_side()
        {
            Assert.Equal(One("land_axis_accel(300, 0, 2, 1.1)"), One("land_cross_accel(300, 0, 2, 1.1)"));
            Assert.Equal(One("-land_axis_accel(300, 0, 2, 1.1)"), One("land_cross_accel(-300, 0, 2, 1.1)"));
            // Moving away from the site across the path: turns that motion round.
            Assert.Equal("True", One("land_cross_accel(-300, 5, 2, 1.1) < 0"));
        }

        [Fact]
        public void Land_counts_a_reversal_only_past_1_m_per_s_of_jitter()
        {
            Assert.Equal("False", One("land_is_reversed(-0.9, false)"));
            Assert.Equal("True", One("land_is_reversed(-1.1, false)"));
            // Once reversed it stays so until moving ahead again, so one swing back counts once.
            Assert.Equal("True", One("land_is_reversed(-0.5, true)"));
            Assert.Equal("False", One("land_is_reversed(0, true)"));
        }

        [Fact]
        public void Land_turns_back_only_when_stopped_beyond_max_overshoot()
        {
            Assert.Equal("False", One("land_turn_back(-400, 0.5, 500)"));
            Assert.Equal("False", One("land_turn_back(-600, 5, 500)"));
            Assert.Equal("True", One("land_turn_back(-600, 0.5, 500)"));
            Assert.Equal("False", One("land_turn_back(600, 0, 500)"));
        }

        [Fact]
        public void Land_guidance_comes_straight_down_once_stopped_and_stays_upright_near_the_ground()
        {
            // Stopped 200 m long of the site (inside max_overshoot): no horizontal push, descending.
            var stopped = Run(Guide("-200, 0, 0, 0, -5, 80"));
            Assert.Equal("0", stopped[0]);
            Assert.Equal("0", stopped[1]);
            // 3 m up, still drifting 1 m/s sideways: at most 11 degrees of lean to null it.
            var low = Run(Guide("0, 0, 1, 0, -2, 3") + " print round(arctan2(abs(w:al), w:up), 3) <= 11.");
            Assert.Equal("True", low[3]);
        }

        // land_guide for one state (s, c, v_al, v_cr, v_up, h) of the reference lander (60 kN, 5.4 t) at
        // the Mun, braking having begun 3000 m up; prints al, cr and up rounded, and leaves it in w.
        private static string Guide(string state)
        {
            var p = state.Split(',');
            return "local w is land_guide(lexicon(\"s\", " + p[0] + ", \"c\", " + p[1] + ", \"v_al\", " + p[2] + ", \"v_cr\", " + p[3]
                + ", \"v_up\", " + p[4] + ", \"h\", " + p[5] + ", \"h_start\", 3000, \"v_top\", 575, \"a_max\", 60000 / 5400, \"g\", 1.63, \"radius\", 200000), 2, 1.1).\n"
                + "print round(w:al, 3). print round(w:cr, 3). print round(w:up, 3).";
        }

        // ---- land: the powered descent, simulated (#233) ----

        // land_descend's loop on a point mass over flat Mun ground, from where braking starts: start_frac
        // times the distance at which land_vh_target falls to its speed, start_c m to one side, 3000 m up,
        // 575 m/s across the ground and 5 m/s down (about 3 m/s on the 30 by 3 km descent orbit there).
        // Each 0.05 s tick, land_guide's acceleration reaches the craft through a first-order lag (lag s,
        // standing in for the turn and the throttle cut while turning), capped at the engine's thrust
        // (thrust N, 345 s Isp, 5.4 t to start: the reference lander has 60 kN). Reversals and turning
        // back are counted as land_descend counts them. Prints reversals, sideways and downward speed at
        // touchdown, m past the site along the path and across it, delta-v used and seconds flown.
        private const string Simulation = @"
function sim {
  parameter start_frac, start_c, lag, thrust, max_overshoot.
  local mu is 6.5138398e10. local rad is 200000. local margin is 1.1. local m is 5400.
  local x is 0. local y is 3000. local z is 0. local vx is 575. local vy is -5. local vz is 0.
  local ax is 0. local ay is 0. local az is 0.
  local a_brake is sqrt(max(0.01, (0.95 * thrust / m) ^ 2 - (mu / (rad + y) ^ 2) ^ 2)).
  // Where land_vh_target(site_x, a_brake, margin) = vx, so land_descend starts braking.
  local a_ok is a_brake / margin.
  local site_x is start_frac * (vx ^ 2 / (2 * a_ok) + 2 * vx).
  local site_z is start_c.
  local h_start is max(500, y).
  local dt is 0.05. local t is 0. local used is 0.
  local sign is 1. local reversals is 0. local reversed is false.
  local last_h is 0. local last_down is 0.
  until y <= 0 or t > 900 {
    local g is mu / (rad + y) ^ 2.
    local a_max is thrust / m.
    if land_turn_back((site_x - x) * sign, sqrt(vx ^ 2 + vz ^ 2), max_overshoot) {
      set sign to -sign.
      set reversals to reversals + 1.
      set reversed to false.
    }
    local was is reversed.
    set reversed to land_is_reversed(vx * sign, was).
    if reversed and not was {
      set reversals to reversals + 1.
    }
    local w is land_guide(lexicon(""s"", (site_x - x) * sign, ""c"", (site_z - z) * sign, ""v_al"", vx * sign, ""v_cr"", vz * sign,
      ""v_up"", vy, ""h"", y, ""h_start"", h_start, ""v_top"", 575, ""a_max"", a_max, ""g"", g, ""radius"", rad + y), 2, margin).
    local k is min(1, dt / lag).
    set ax to ax + (w:al * sign - ax) * k.
    set az to az + (w:cr * sign - az) * k.
    set ay to ay + (w:up - ay) * k.
    local a_mag is sqrt(ax ^ 2 + ay ^ 2 + az ^ 2).
    if a_mag > a_max {
      set ax to ax * a_max / a_mag. set ay to ay * a_max / a_mag. set az to az * a_max / a_mag.
      set a_mag to a_max.
    }
    set last_h to sqrt(vx ^ 2 + vz ^ 2).
    set last_down to -vy.
    set vx to vx + ax * dt. set vz to vz + az * dt.
    set vy to vy + (ay - g + (vx ^ 2 + vz ^ 2) / (rad + y)) * dt.
    set x to x + vx * dt. set y to y + vy * dt. set z to z + vz * dt.
    set m to m - m * a_mag / (345 * 9.80665) * dt.
    set used to used + a_mag * dt.
    set t to t + dt.
  }
  print reversals. print round(last_h, 3). print round(last_down, 3).
  print round(x - site_x, 1). print round(z - site_z, 1). print round(used). print round(t).
}
";

        private sealed class Flight
        {
            public int Reversals;
            public double SidewaysAtTouchdown, DownAtTouchdown, Long, Across, DvUsed, Seconds;
        }

        private static Flight Fly(double startFrac, double startC = 0, double lag = 1, double thrust = 60000, double maxOvershoot = 500)
        {
            var printed = Run(Simulation + string.Format(CultureInfo.InvariantCulture, "sim({0}, {1}, {2}, {3}, {4}).", startFrac, startC, lag, thrust, maxOvershoot));
            double D(int i) => double.Parse(printed[i], CultureInfo.InvariantCulture);
            return new Flight
            {
                Reversals = int.Parse(printed[0], CultureInfo.InvariantCulture),
                SidewaysAtTouchdown = D(1), DownAtTouchdown = D(2), Long = D(3), Across = D(4), DvUsed = D(5), Seconds = D(6),
            };
        }

        private static void AssertSettlesOnTheSite(Flight f)
        {
            Assert.Equal(0, f.Reversals);
            // The block tests' touchdown_h_speed bound.
            Assert.InRange(f.SidewaysAtTouchdown, 0, 0.5);
            Assert.InRange(f.DownAtTouchdown, 1, 3);
            Assert.InRange(f.Long, -10, 10);
            Assert.InRange(f.Across, -10, 10);
        }

        [Fact]
        public void The_descent_brakes_once_onto_the_site_and_touches_down_upright()
        {
            // Before #233 the same flight swung back over the site 4 times, touched down at 0.7 m/s
            // sideways and used 1012 m/s.
            var f = Fly(1);
            AssertSettlesOnTheSite(f);
            // The descent's share of the block tests' 900 m/s dv_used bound: the 575 m/s across the
            // ground plus holding the craft up and bringing it down; about 710 m/s.
            Assert.InRange(f.DvUsed, 575, 800);
        }

        [Fact]
        public void The_descent_brakes_once_with_a_slow_turn_or_a_weak_engine()
        {
            AssertSettlesOnTheSite(Fly(1, lag: 2));
            // Thrust-to-weight 3.4 at the Mun, half the reference lander's.
            AssertSettlesOnTheSite(Fly(1, thrust: 30000));
        }

        [Fact]
        public void The_descent_takes_in_a_site_moved_to_one_side()
        {
            // FLATSPOT may move the site up to max_divert (300 m) as braking starts.
            AssertSettlesOnTheSite(Fly(1, startC: 300));
            AssertSettlesOnTheSite(Fly(1, startC: -300, lag: 2, thrust: 30000));
        }

        [Fact]
        public void A_late_descent_lands_long_rather_than_turning_back()
        {
            // Braking starts at 82% of the distance it should: it stops about 200 m past the site,
            // inside max_overshoot, and comes straight down there.
            var f = Fly(0.82);
            Assert.Equal(0, f.Reversals);
            Assert.InRange(f.Long, 10, 500);
            Assert.InRange(f.SidewaysAtTouchdown, 0, 0.5);
        }

        [Fact]
        public void A_descent_stopped_beyond_max_overshoot_turns_back_once()
        {
            foreach (var f in new[] { Fly(0.82, maxOvershoot: 50), Fly(0.8) })
            {
                Assert.Equal(1, f.Reversals);
                Assert.InRange(f.Long, -50, 50);
                Assert.InRange(f.SidewaysAtTouchdown, 0, 0.5);
            }
        }

        // ---- land: the powered descent over terrain (#365) ----

        // land_descend's loop on a point mass along one line over a terrain profile (a list of
        // list(x, height) corners, flat beyond the ends), braking from x = 0 at y m above sea level,
        // 575 m/s along and 5 m/s down, with the reference lander (60 kN, 5.4 t, lag 1 s), the site at
        // site_x. It steers by land_clearance over the terrain at land_lookahead's distances, as
        // land_descend does, and holds to the speed braking began at (v_top). Prints whether it hit the
        // ground hard (over 5 m/s), the fastest descent, the m past the site and the seconds flown.
        private const string TerrainSimulation = @"
function terr {
  parameter prof, x.
  if x <= prof[0][0] { return prof[0][1]. }
  for i in range(1, prof:length) {
    if x <= prof[i][0] {
      return prof[i - 1][1] + (x - prof[i - 1][0]) / (prof[i][0] - prof[i - 1][0]) * (prof[i][1] - prof[i - 1][1]).
    }
  }
  return prof[prof:length - 1][1].
}
function tsim {
  parameter prof, site_x, y.
  local mu is 6.5138398e10. local rad is 200000. local margin is 1.1. local m is 5400. local thrust is 60000.
  local x is 0. local vx is 575. local vy is -5. local ax is 0. local ay is 0.
  local h_start is -1. local v_top is vx.
  local dt is 0.05. local t is 0. local fastest_down is 0. local h is y - terr(prof, x).
  until h <= 0 or t > 900 {
    local g is mu / (rad + y) ^ 2.
    local heights is list().
    for d in land_lookahead(abs(vx), max(0, site_x - x)) {
      heights:add(terr(prof, x + d)).
    }
    set h to land_clearance(y - terr(prof, x), y, heights).
    if h_start < 0 { set h_start to max(500, h). }
    local w is land_guide(lexicon(""s"", site_x - x, ""c"", 0, ""v_al"", vx, ""v_cr"", 0, ""v_up"", vy, ""h"", h, ""h_start"", h_start,
      ""v_top"", v_top, ""a_max"", thrust / m, ""g"", g, ""radius"", rad + y), 2, margin).
    local k is min(1, dt / 1).
    set ax to ax + (w:al - ax) * k.
    set ay to ay + (w:up - ay) * k.
    local a_mag is sqrt(ax ^ 2 + ay ^ 2).
    if a_mag > thrust / m {
      set ax to ax * thrust / m / a_mag. set ay to ay * thrust / m / a_mag. set a_mag to thrust / m.
    }
    set vx to vx + ax * dt.
    set vy to vy + (ay - g + vx ^ 2 / (rad + y)) * dt.
    set x to x + vx * dt. set y to y + vy * dt.
    set m to m - m * a_mag / (345 * 9.80665) * dt.
    set fastest_down to max(fastest_down, -vy).
    set t to t + dt.
    set h to y - terr(prof, x).
  }
  print sqrt(vx ^ 2 + vy ^ 2) > 5. print round(fastest_down). print round(x - site_x). print round(t).
}
";

        private static string[] FlyOver(string profile, double siteX, double startY)
        {
            return Run(TerrainSimulation + string.Format(CultureInfo.InvariantCulture, "tsim({0}, {1}, {2}).", profile, siteX, startY));
        }

        [Fact]
        public void Land_steers_by_its_height_above_the_highest_ground_ahead()
        {
            // 1000 m up over ground at 0, a 600 m hill ahead: 400 m clear.
            Assert.Equal("400", One("land_clearance(1000, 1000, list(0, 200, 600, 100))"));
            // Ground ahead lower than under it: the radar height.
            Assert.Equal("1000", One("land_clearance(1000, 1500, list(-500, 100))"));
            // A minute ahead at 100 m/s, but no farther than where it stops.
            Assert.Equal(new[] { "23", "50", "6000", "700" }, Run(
                "local d is land_lookahead(100, 1e9). print d:length. print d[0]. print d[d:length - 1]. print land_lookahead(100, 700)[22]."));
        }

        [Fact]
        public void The_descent_burns_in_every_phase_but_the_fall()
        {
            Assert.Equal("False", One("land_burning(\"fall\")"));
            Assert.Equal("True", One("land_burning(\"braking\")"));
            Assert.Equal("True", One("land_burning(\"approach\")"));
            Assert.Equal("True", One("land_burning(\"terminal\")"));
            // A stage run dry while braking: throttle 0, but still burning.
            Assert.Equal("True", One("land_burning(land_phase(true, 43, 2231))"));
            Assert.Equal("False", One("land_burning(land_phase(false, 575, -1))"));
        }

        // ---- land: braking for a craft that turns slowly (#380) ----

        [Fact]
        public void Land_times_a_turn_by_turntimes_model_and_counts_the_last_5_degrees_as_pointed()
        {
            // Mun Shot v2's TURNTIME: 190 degrees in 102.5 s at up to 1.9 deg/s, so 2.5 s to reach it.
            Assert.Equal("102.5", One("round(land_turn_seconds(195, 1.9, 102.5), 3)"));
            Assert.Equal("5.132", One("round(land_turn_seconds(10, 1.9, 102.5), 3)"));
            // 2 degrees to go never reaches the top rate: 2 sqrt(2 / 0.76).
            Assert.Equal("3.244", One("round(land_turn_seconds(7, 1.9, 102.5), 3)"));
            // A turn that peaks under kOS's cap (1 deg/s^2: 190 degrees in 27.568 s, peaking at 13.784 deg/s).
            Assert.Equal("27.568", One("round(land_turn_seconds(195, 13.784, 27.568), 3)"));
            Assert.Equal("0", One("land_turn_seconds(5, 1.9, 102.5)"));
            Assert.Equal("0", One("land_turn_seconds(0, 1.9, 102.5)"));
            // No TURNTIME (no torque): no allowance.
            Assert.Equal("0", One("land_turn_seconds(90, 0, 0)"));
        }

        [Fact]
        public void The_braking_start_distance_grows_with_the_turn_left_and_is_todays_when_pointed()
        {
            // With no turn left it is where land_vh_target falls to the speed, as before #380.
            Assert.Equal("568", One("round(land_vh_target(land_brake_distance(568, 10, 1.1, 0), 10, 1.1), 3)"));
            Assert.Equal("True", One("land_brake_distance(568, 10, 1.1, land_turn_seconds(0, 1.9, 102.5)) = land_brake_distance(568, 10, 1.1, 0)"));
            // 20 s still to turn at 568 m/s: 11.36 km farther out; more turn, farther still.
            Assert.Equal("11360", One("round(land_brake_distance(568, 10, 1.1, 20) - land_brake_distance(568, 10, 1.1, 0), 3)"));
            Assert.Equal("True", One("land_brake_distance(568, 10, 1.1, land_turn_seconds(90, 1.9, 102.5)) > land_brake_distance(568, 10, 1.1, land_turn_seconds(30, 1.9, 102.5))"));
            // The rule: just outside the distance it waits, just inside it brakes; a turn left brakes it sooner.
            var d = "land_brake_distance(568, 10, 1.1, 0)";
            Assert.Equal("False", One("land_brake_due(" + d + " + 1, 1e5, 568, 10, 3000, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(" + d + " - 1, 1e5, 568, 10, 3000, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(" + d + " + 1, 1e5, 568, 10, 3000, 10, 1.1, 1)"));
            // The ground coming up, or the site behind, brakes it whatever the distance.
            Assert.Equal("True", One("land_brake_due(1e6, 1e6, 568, 10, 1400, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(1e6, 1e6, 568, 80, 3000, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(1e6, -1, 568, 10, 3000, 10, 1.1, 0)"));
        }

        // For the reference lander (60 kN, 5.4 t) at the Mun, 575 m/s along, v_up m/s up, 3000 m up, the
        // site s m along and 100 m across: land_fall_guide, and land_guide's first command from the same
        // state where land_brake_distance starts braking, with v_top (the speed braking began at) v_top.
        // Prints the fall's al and up, the degrees between the two, and land_guide's al.
        private static double[] FallAndFirst(string s, double vUp, string vTop = "575")
        {
            var state = "\"c\", 100, \"v_al\", 575, \"v_cr\", 0, \"v_up\", " + vUp.ToString(CultureInfo.InvariantCulture)
                + ", \"h\", 3000, \"a_max\", 60000 / 5400, \"g\", 1.63, \"radius\", 200000";
            return Run("local start is land_brake_distance(575, land_brake_accel(60000 / 5400, 1.63), 1.1, 0).\n"
                + "local f is land_fall_guide(lexicon(\"s\", " + s + ", " + state + "), 2, 1.1).\n"
                + "local w is land_guide(lexicon(\"s\", start, \"h_start\", 3000, \"v_top\", " + vTop + ", " + state + "), 2, 1.1).\n"
                + "local dot is (f:al * w:al + f:cr * w:cr + f:up * w:up) / sqrt((f:al ^ 2 + f:cr ^ 2 + f:up ^ 2) * (w:al ^ 2 + w:cr ^ 2 + w:up ^ 2)).\n"
                + "print f:al. print f:up. print arccos(min(1, dot)). print w:al.")
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        [Fact]
        public void The_fall_steers_along_the_first_braking_command()
        {
            // Falling 30 m/s at the braking start: what land_guide first asks for from the same state.
            var at = FallAndFirst("start", -30);
            Assert.InRange(at[2], 0, 0.01);
            // Braking: back along the path and held up, not surface retrograde.
            Assert.True(at[0] < -5);
            Assert.True(at[1] > 1);
            // Farther out, the same: the command braking will begin with, not one for a site it isn't
            // braking for yet.
            var far = FallAndFirst("start * 3", -30);
            Assert.InRange(far[2], 0, 0.01);
            Assert.Equal(at[0], far[0], 6);
            // Falling 5 m/s, land_guide's first command holds the speed braking began at (v_top) until its
            // stopping curve falls under it, a moment on (0 along): the fall points along the braking that
            // comes then, land_guide's from the same state on its curve.
            Assert.Equal(0, FallAndFirst("start", -5)[3], 3);
            var slow = FallAndFirst("start", -5, "1e9");
            Assert.InRange(slow[2], 0, 0.01);
            Assert.True(slow[0] < -5);
        }

        [Fact]
        public void Braking_that_starts_early_for_the_turn_flies_by_the_site_that_much_nearer()
        {
            Assert.Equal("8000", One("land_turning_s(10000, 500, 4)"));
            Assert.Equal("10000", One("land_turning_s(10000, 500, 0)"));
            Assert.Equal("1", One("land_turning_s(100, 500, 4)"));
            Assert.Equal("0.5", One("land_turning_s(0.5, 500, 4)"));
            Assert.Equal("-50", One("land_turning_s(-50, 500, 4)"));
        }

        [Fact]
        public void The_coast_ends_the_turn_and_30_s_before_braking_and_never_under_90_s()
        {
            // Mun Shot v2: a 112.5 s turn.
            Assert.Equal("857.5", One("land_coast_end(1000, 112.5)"));
            Assert.Equal("True", One("land_coast_end(1000, 112.5) <= 1000 - 112.5 - 30"));
            // A quick turn: the 90 s as before #380.
            Assert.Equal("910", One("land_coast_end(1000, 30)"));
        }

        // A fall at 575 m/s over a terrain profile (as for tsim) to a site at 60 km, periapsis 3000 m up
        // over it at 104 s: y = 3000 + 0.2 (104 - t)^2. Prints the braking start land_brake_estimate gives
        // from samples every 5 s, the UT land_brake_due fires checking every 0.1 s by land_descend's
        // own clearance (land_lookahead, land_clearance), and the start the coast used before #380
        // (periapsis less the stopping time at periapsis).
        private const string FallEstimate = @"
function fall_at {
  parameter prof, t.
  local x is 575 * t.
  return lexicon(""ut"", t, ""x"", x, ""dist"", abs(60000 - x), ""ahead"", 60000 - x, ""v_h"", 575, ""v_down"", 0.4 * (104 - t),
    ""alt"", 3000 + 0.2 * (104 - t) ^ 2, ""ground"", terr(prof, x)).
}
function estimate {
  parameter prof.
  local a_brake is land_brake_accel(60000 / 5400, 1.63).
  local samples is list().
  local t is 0.
  until t > 180 { samples:add(fall_at(prof, t)). set t to t + 5. }
  print round(land_brake_estimate(samples, a_brake, 1.1), 1).
  set t to 0.
  until t > 180 {
    local p is fall_at(prof, t).
    local heights is list().
    for d in land_lookahead(p:v_h, p:dist) { heights:add(terr(prof, p:x + d)). }
    local h is land_clearance(p:alt - p:ground, p:alt, heights).
    if land_brake_due(p:dist, p:ahead, p:v_h, p:v_down, h, a_brake, 1.1, 0) { break. }
    set t to t + 0.1.
  }
  print round(t, 1).
  print round(104 - 575 * 1.1 / (2 * a_brake), 1).
}
";

        private static double[] Estimate(string profile)
        {
            return Run(TerrainSimulation + FallEstimate + "estimate(" + profile + ").")
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        [Fact]
        public void The_coast_plans_on_braking_no_later_than_it_starts_over_high_ground()
        {
            // A 2500 m plateau from 30 km on, under the craft's 3000 m periapsis: the ground comes inside
            // 1500 m some 40 s before the periapsis estimate, which was too late.
            var high = Estimate("list(list(0, 0), list(20000, 0), list(30000, 2500), list(90000, 2500))");
            Assert.InRange(high[0], high[1] - 5.1, high[1]);
            Assert.True(high[2] > high[1] + 30);
            // Flat ground: it brakes for the site, and the estimate is still no later.
            var flat = Estimate("list(list(0, 0), list(90000, 0))");
            Assert.InRange(flat[0], flat[1] - 5.1, flat[1]);
        }

        [Fact]
        public void The_descent_traces_a_line_every_2_s_and_at_each_phase_change()
        {
            Assert.Equal("fall", One("land_phase(false, 575, -1)"));
            Assert.Equal("braking", One("land_phase(true, 200, 450)"));
            Assert.Equal("approach", One("land_phase(true, 20, 100)"));
            Assert.Equal("terminal", One("land_phase(true, 0.5, 0)"));
            Assert.Equal("False", One("land_trace_due(\"braking\", \"braking\", 101.9, 100)"));
            Assert.Equal("True", One("land_trace_due(\"braking\", \"braking\", 102, 100)"));
            Assert.Equal("True", One("land_trace_due(\"approach\", \"braking\", 100.1, 100)"));
        }

        [Fact]
        public void Braking_far_out_holds_its_speed_rather_than_speeding_up_to_the_stopping_curve()
        {
            // 60 km out the curve allows about 1070 m/s; at 575 it would push ahead, held to 575 it doesn't.
            Assert.Equal("True", One("land_axis_accel(60000, 575, 10, 1.1) > 1"));
            Assert.Equal("0", One("round(land_axis_accel(60000, 575, 10, 1.1, 575), 3)"));
            // Slower than v_top it still closes on the site.
            Assert.Equal("True", One("land_axis_accel(60000, 0, 10, 1.1, 50) > 1"));
        }

        [Fact]
        public void A_descent_braking_far_out_climbs_over_a_rim_ahead_rather_than_flying_into_it()
        {
            // Braking began 60 km short, 1400 m up (land_descend's h < 1500), with a 3 km rim rising over
            // 8 km between it and the site: it cruises no faster than it was going and clears the rim.
            var f = FlyOver("list(list(0, 0), list(40000, 0), list(48000, 3000), list(50000, 0))", 60000, 1400);
            Assert.Equal("False", f[0]);
            Assert.InRange(double.Parse(f[2], CultureInfo.InvariantCulture), -20, 500);
        }

        [Fact]
        public void A_descent_into_a_crater_holds_its_height_above_the_far_rim_and_never_falls_fast()
        {
            // Over the near rim, the floor 3 km down, the site on the floor just short of a 2 km far rim.
            var f = FlyOver("list(list(0, 1500), list(3000, -1500), list(18500, -1500), list(21000, 500))", 18000, 4500);
            Assert.Equal("False", f[0]);
            Assert.InRange(double.Parse(f[1], CultureInfo.InvariantCulture), 0, 100);
            Assert.InRange(double.Parse(f[2], CultureInfo.InvariantCulture), -20, 500);
        }

        // ---- ascend ----

        [Fact]
        public void Ascend_options_take_the_spec_defaults()
        {
            var printed = Run("local o is claudia_options(lexicon(), ascend_defaults(), 900):opt.\nprint o:target_alt. print o:target_inc. print o:max_duration.");
            Assert.Equal(new[] { "auto", "0", "900" }, printed);
            Assert.Equal("unknown option target_ap", One("claudia_options(lexicon(\"target_ap\", 5), ascend_defaults(), 900):error"));
            Assert.Equal("", One("ascend_unreadable(claudia_options(lexicon(\"target_inc\", 90), ascend_defaults(), 900):opt)"));
            Assert.Equal("target_inc must be degrees from 0 to 180", One("ascend_unreadable(claudia_options(lexicon(\"target_inc\", 200), ascend_defaults(), 900):opt)"));
            Assert.Equal("target_inc must be degrees from 0 to 180", One("ascend_unreadable(claudia_options(lexicon(\"target_inc\", \"polar\"), ascend_defaults(), 900):opt)"));
        }

        [Fact]
        public void Ascend_aims_5_km_above_the_highest_terrain_unless_told_and_never_below_it()
        {
            Assert.Equal("12061", One("ascend_target_alt(\"auto\", 7061)"));
            Assert.Equal("20000", One("ascend_target_alt(20000, 7061)"));
            Assert.Equal("", One("ascend_check_alt(12061, 7061, 2429559, \"Mun\")"));
            Assert.Equal("target_alt 5000 m is not above the highest terrain, 7061 m", One("ascend_check_alt(5000, 7061, 2429559, \"Mun\")"));
            Assert.Equal("target_alt 3000000 m is outside the Mun's sphere of influence", One("ascend_check_alt(3000000, 7061, 2429559, \"Mun\")"));
            Assert.Equal("target_alt must be \"auto\" or metres, not 'high'", One("ascend_check_alt(\"high\", 7061, 2429559, \"Mun\")"));
        }

        [Fact]
        public void Ascend_heads_for_the_target_inclination()
        {
            Assert.Equal("90", One("round(ascend_azimuth(0, 0, 550, 0), 3)"));
            Assert.Equal("0", One("round(ascend_azimuth(90, 0, 550, 0), 3)"));
            Assert.Equal("45", One("round(ascend_azimuth(45, 0, 550, 0), 3)"));
            Assert.Equal("270", One("round(ascend_azimuth(180, 0, 550, 0), 3)"));
            // The ground already moves east at 10 m/s, so less of the burn goes east.
            Assert.Equal("44.254", One("round(ascend_azimuth(45, 0, 550, 10), 3)"));
            // An inclination below the site's latitude can't be reached: due east, the nearest.
            Assert.Equal("90", One("round(ascend_azimuth(10, 30, 550, 0), 3)"));
        }

        [Fact]
        public void Ascend_climbs_at_half_the_speed_that_would_coast_it_up_to_the_target()
        {
            // Coasting up 10 km at 1.6 m/s^2 takes 178.885 m/s; it climbs at half that.
            Assert.Equal("89.443", One("round(ascend_vv_target(2000, 12000, 1.6), 3)"));
            Assert.Equal("0", One("ascend_vv_target(13000, 12000, 1.6)"));
        }

        [Fact]
        public void Ascend_pitches_to_hold_the_climb_rate()
        {
            Assert.Equal("90", One("round(ascend_pitch(10, 1.6, 0, 100), 3)"));
            Assert.Equal("9.207", One("round(ascend_pitch(10, 1.6, 100, 100), 3)"));
            Assert.Equal("0", One("round(ascend_pitch(10, 1.6, 150, 100), 3)"));
        }

        [Fact]
        public void Ascend_circularizes_at_apoapsis_by_vis_viva()
        {
            Assert.Equal("15.381", One("round(ascend_circularize_dv(" + MunMu + ", 212000, 201000), 3)"));
        }
    }
}
