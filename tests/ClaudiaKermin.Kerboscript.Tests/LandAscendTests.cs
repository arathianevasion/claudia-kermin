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
                + ", \"v_up\", " + p[4] + ", \"h\", " + p[5] + ", \"h_start\", 3000, \"a_max\", 60000 / 5400, \"g\", 1.63, \"radius\", 200000), 2, 1.1).\n"
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
      ""v_up"", vy, ""h"", y, ""h_start"", h_start, ""a_max"", a_max, ""g"", g, ""radius"", rad + y), 2, margin).
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
