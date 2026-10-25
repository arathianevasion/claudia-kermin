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
            Assert.Equal("target must be \"any\", LATLNG(lat, lng) or lexicon(\"lat\", x, \"lng\", y), not 'somewhere'",
                One("land_unreadable(claudia_options(lexicon(\"target\", \"somewhere\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("target must be \"any\", LATLNG(lat, lng) or lexicon(\"lat\", x, \"lng\", y), not a lexicon without lat and lng",
                One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"lat\", 1)), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_slope must be a number of degrees from 0 to 45",
                One("land_unreadable(claudia_options(lexicon(\"max_slope\", 60), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("touchdown_speed must be a speed above 0 m/s",
                One("land_unreadable(claudia_options(lexicon(\"touchdown_speed\", 0), land_defaults(), \"auto\"):opt)"));
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
            Assert.Equal("89.443", One("round(land_vh_target(1000, 4, 1), 3)"));
            Assert.Equal("63.246", One("round(land_vh_target(1000, 4, 2), 3)"));
            Assert.Equal("5", One("round(land_vh_target(10, 4, 1), 3)"));
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
