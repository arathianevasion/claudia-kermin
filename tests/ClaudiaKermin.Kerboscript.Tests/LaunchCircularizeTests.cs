// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The pure helpers of the launch and circularize Building Blocks, run on kOS.Safe through the real bundle.</summary>
    public class LaunchCircularizeTests
    {
        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        // ---- launch: options ----

        [Fact]
        public void Launch_options_take_the_spec_defaults()
        {
            Assert.Equal(new[] { "now", "auto", "0", "lib_lazcalc", "60", "0.45", "5", "none", "body", "body", "body", "auto", "" },
                Run("local c is claudia_options(lexicon(), launch_defaults(), \"auto\"). local o is c:opt.\n"
                    + "print o:launch_at. print o:target_ap. print o:target_inc. print o:heading. print o:turn_start_speed. print o:turn_shape.\n"
                    + "print o:max_aoa. print o:throttle_max_q. print o:g. print o:atm_height. print o:surface_pressure. print o:max_duration. print c:error."));
        }

        [Theory]
        [InlineData("lexicon()", "")]
        [InlineData("lexicon(\"launch_at\", 1000, \"target_ap\", 80000, \"heading\", 90, \"throttle_max_q\", 20000, \"g\", 9.81, \"atm_height\", 70000, \"surface_pressure\", 1)", "")]
        [InlineData("lexicon(\"launch_at\", \"later\")", "launch_at must be 'now', a time (UT s) or a body or vessel to launch into the plane of, not 'later'")]
        [InlineData("lexicon(\"target_ap\", \"high\")", "target_ap must be 'auto' or m, not 'high'")]
        [InlineData("lexicon(\"heading\", \"east\")", "heading must be 'lib_lazcalc' or degrees, not 'east'")]
        [InlineData("lexicon(\"throttle_max_q\", \"off\")", "throttle_max_q must be 'none' or Pa, not 'off'")]
        [InlineData("lexicon(\"g\", \"kerbin\")", "g must be 'body' or a number, not 'kerbin'")]
        public void A_launch_option_string_it_does_not_know_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("launch_unreadable(claudia_options(" + options + ", launch_defaults(), \"auto\"):opt)"));
        }

        // ---- launch: the pitch program ----

        [Theory]
        [InlineData(500, "90")]
        [InlineData(1000, "90")]
        [InlineData(18250, "41.77")]
        [InlineData(70000, "0")]
        [InlineData(75000, "0")]
        public void The_pitch_falls_from_vertical_at_the_turn_start_to_flat_at_the_turn_end(double altitude, string pitch)
        {
            Assert.Equal(pitch, One("round(launch_pitch(" + altitude + ", 1000, 70000, 0.45), 2)"));
        }

        [Theory]
        [InlineData(11000, 12000, 0.5, "True")]
        [InlineData(10900, 12000, 0.5, "True")]
        [InlineData(10700, 12000, 0.5, "False")]
        [InlineData(14000, 12000, 0.05, "False")]
        [InlineData(30000, -1, 0.5, "False")]
        public void The_turn_waits_while_throttle_max_q_eases_the_throttle_in_thick_air(double q, double maxQ, double pressureRatio, string held)
        {
            Assert.Equal(held, One("launch_turn_held(" + q + ", " + maxQ + ", " + pressureRatio + ")"));
        }

        // ---- launch: the throttle ----

        [Theory]
        [InlineData(50000, 0, -1, 0, "1")]
        [InlineData(79000, 0, -1, 0, "0.5")]
        [InlineData(79000, 0, -1, 0.7, "0.7")]
        [InlineData(80000, 0, -1, 0.7, "0")]
        [InlineData(81000, 0, -1, 0.7, "0")]
        [InlineData(50000, 15000, 20000, 0, "1")]
        [InlineData(50000, 20000, 20000, 0, "0.5")]
        [InlineData(50000, 22000, 20000, 0, "0.05")]
        [InlineData(50000, 22000, 20000, 0.6, "0.6")]
        [InlineData(50000, 90000, -1, 0, "1")]
        public void The_throttle_eases_to_the_target_ap_and_under_max_q_but_only_the_target_ap_cuts_it_off(double ap, double q, double maxQ, double floor, string throttle)
        {
            Assert.Equal(throttle, One("round(launch_throttle(" + ap + ", 80000, 2000, " + q + ", " + maxQ + ", " + floor + "), 3)"));
        }

        // #234: no trickle just under the target; the cutoff band (half the top-up band) is inside it.
        [Theory]
        [InlineData(79999, 0, -1, 0, "0")]
        [InlineData(79900, 0, -1, 0, "0")]
        [InlineData(79800, 0, -1, 0, "0")]
        [InlineData(79700, 0, -1, 0, "0.15")]
        [InlineData(79000, 20000, 20000, 0, "0.5")]
        public void The_throttle_cuts_off_within_the_cutoff_band_of_the_target_ap_instead_of_trickling(double ap, double q, double maxQ, double floor, string throttle)
        {
            Assert.Equal(throttle, One("round(launch_throttle(" + ap + ", 80000, 2000, " + q + ", " + maxQ + ", " + floor + "), 3)"));
        }

        [Theory]
        [InlineData(80000, "200")]
        [InlineData(10000, "50")]
        public void The_cutoff_band_is_half_the_top_up_band(double targetAp, string cutoff)
        {
            Assert.Equal(cutoff, One("launch_cutoff_tol(" + targetAp + ")"));
            Assert.Equal("True", One("launch_cutoff_tol(" + targetAp + ") < launch_topup_tol(" + targetAp + ")"));
        }

        [Theory]
        [InlineData(0, "True")]
        [InlineData(0.02, "False")]
        [InlineData(0.05, "True")]
        [InlineData(1, "True")]
        public void Stock_delta_v_is_trusted_at_zero_throttle_or_from_five_percent(double throttle, string trusted)
        {
            Assert.Equal(trusted, One("claudia_dv_trusted(" + throttle + ")"));
        }

        [Theory]
        [InlineData(0.9, 6.4, 9.81, 167.97, "0.374")]
        [InlineData(0.9, 20, 9.81, 100, "1")]
        [InlineData(0.05, 6.4, 9.81, 167.97, "0")]
        [InlineData(0.9, 6.4, 9.81, 0, "0")]
        public void In_thick_air_the_throttle_floor_keeps_thrust_at_least_equal_to_weight(double pressureRatio, double mass, double g, double thrust, string floor)
        {
            Assert.Equal(floor, One("round(launch_throttle_floor(" + pressureRatio + ", " + mass + ", " + g + ", " + thrust + "), 3)"));
        }

        // ---- launch: the ascent, flown offline (launch_ascent_sim.ks) ----

        // launch_no_flip's ascent (target_ap 60 km, throttle_max_q 12 kPa) in the offline sim, with the
        // turn hold on or off: the result lines by their first word, and the whole printout.
        private static (System.Collections.Generic.Dictionary<string, string> Result, string Track) Ascent(bool hold, double dragArea, double maxQ = 12000, double targetAp = 60000, bool thin = true, double thrustScale = 1, double dry = 2.4)
        {
            var sim = System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "launch_ascent_sim.ks"));
            var lines = Run(sim + "\nsim_ascent(lexicon(\"target_ap\", " + targetAp + ", \"max_q\", " + maxQ
                + ", \"turn_start_speed\", 60, \"turn_shape\", 0.45, \"max_aoa\", 5, \"drag_area\", " + dragArea + ", \"hold\", " + (hold ? "true" : "false") + ", \"thin\", " + (thin ? "true" : "false") + ", \"thrust_scale\", " + thrustScale + ", \"dry\", " + dry + ")).");
            var result = lines.Where(l => !l.StartsWith("track ")).ToDictionary(l => l.Split(' ')[0], l => l.Substring(l.IndexOf(' ') + 1));
            return (result, string.Join("\n", lines));
        }

        // #235: the pitch program turned a q-limited craft over by altitude while its thrust, held to about
        // its weight, couldn't hold the path up; the path fell over and the craft stalled near 10 km.
        [Theory]
        [InlineData(0.5)]
        [InlineData(1.0)]
        public void Without_the_turn_hold_a_q_limited_craft_stalls_low_in_the_climb(double dragArea)
        {
            var (result, track) = Ascent(false, dragArea);
            Assert.True(result["stalled"].StartsWith("True"), track);
            var at = double.Parse(result["stalled"].Split(' ').Last());
            Assert.InRange(at, 8000, 14000);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public void With_the_turn_hold_a_q_limited_craft_climbs_to_its_cutoff_without_leaving_the_airflow(double dragArea)
        {
            var (result, track) = Ascent(true, dragArea);
            Assert.True(result["stalled"].StartsWith("False"), track);
            Assert.True(double.Parse(result["coast_ap"]) >= 59700, track);
            Assert.True(double.Parse(result["max_q"]) < 15000, track);
            Assert.True(double.Parse(result["min_twr"]) > 0.95, track);
            // The nose never runs ahead of (below) the prograde by more than max_aoa.
            Assert.True(double.Parse(result["max_lead"]) <= 5, track);
        }

        [Fact]
        public void Without_throttle_max_q_the_turn_hold_changes_nothing()
        {
            Assert.Equal(Ascent(false, 0.5, -1, 80000).Track, Ascent(true, 0.5, -1, 80000).Track);
        }

        // #236: in thin air the pitch is held no lower than 3 degrees under the surface-prograde pitch.
        [Theory]
        [InlineData(30, 60, 100, 3, "57")]
        [InlineData(58, 60, 100, 3, "58")]
        [InlineData(70, 60, 100, 3, "70")]
        [InlineData(30, 60, 500, 3, "57")]
        [InlineData(30, 60, 501, 3, "30")]
        [InlineData(30, 60, 20000, 3, "30")]
        public void In_thin_air_the_pitch_is_held_within_a_few_degrees_of_the_prograde(double pitch, double prograde, double q, double lead, string held)
        {
            Assert.Equal(held, One("launch_thin_pitch(" + pitch + ", " + prograde + ", " + q + ", " + lead + ")"));
        }

        // A slow (70% thrust) craft to a 400 km apoapsis is still burning above 40 km, in the thin air. It has more to burn
        // (2 t dry) since the sim's Coriolis (#370) costs it what it had spare.
        [Fact]
        public void Without_the_thin_air_cap_the_nose_runs_well_below_the_prograde_up_high_and_with_it_stays_within_a_few_degrees()
        {
            var (free, freeTrack) = Ascent(true, 0.5, 12000, 400000, false, 0.7, 2.0);
            Assert.True(double.Parse(free["max_thin_lead"]) > 20, freeTrack);
            var (capped, track) = Ascent(true, 0.5, 12000, 400000, true, 0.7, 2.0);
            Assert.True(double.Parse(capped["max_thin_lead"]) <= 3.1, track);
            Assert.True(capped["stalled"].StartsWith("False"), track);
            Assert.True(double.Parse(capped["coast_ap"]) >= 397000, track);
        }

        // ---- launch: heading and the plane ----

        // The heading itself is KSLib's LAZcalc, which reads the ship; it is checked in game (launch_orbit).
        [Fact]
        public void The_heading_comes_from_the_bundled_lib_lazcalc_which_is_not_a_Building_Block()
        {
            var bundle = CoreLibrary.Bundle("");
            Assert.Contains("FUNCTION LAZcalc_init {", bundle);
            Assert.DoesNotContain("@CLOBBERBUILTINS", bundle);
            Assert.DoesNotContain("function launch_azimuth", bundle);
            Assert.Contains("LAZcalc(laz)", CoreLibrary.Files.Single(f => f.Name == "launch").Source);
            Assert.DoesNotContain(CoreLibrary.Blocks, b => b.Name.StartsWith("lib_lazcalc"));
            Assert.DoesNotContain("LAZcalc", CoreLibrary.FullDocs() + CoreLibrary.Summaries());
        }

        // LAZcalc_azimuth is the patched lib_lazcalc's arithmetic without the ship: Kerbin, 80 km orbit,
        // on the equator (latitude 0); the list is what LAZcalc_init builds: inclination, latitude,
        // equatorial speed, orbital speed, node, node epsilon (m/s of north speed that switches the node).
        [Theory]
        [InlineData(45, "Ascending", 0, 0, "41.7")]
        [InlineData(45, "Descending", 0, 0, "138.3")]
        [InlineData(135, "Ascending", 0, 0, "312.1")]
        [InlineData(135, "Descending", 0, 0, "227.9")]
        [InlineData(45, "Descending", 10, 50, "41.7")]
        [InlineData(45, "Ascending", 10, -50, "138.3")]
        [InlineData(45, "Descending", 10, -3, "138.3")]
        public void A_northward_and_a_southward_launch_both_get_a_heading(double inc, string node, double epsilon, double northSpeed, string azimuth)
        {
            var data = "list(" + inc + ", 0, 174.94, 2278.9, \"" + node + "\", " + epsilon + ")";
            Assert.Equal(azimuth, One("round(LAZcalc_azimuth(" + data + ", 0, " + northSpeed + "), 1)"));
        }

        [Theory]
        [InlineData(0, 40, 30, 100, 21549.425, "3591.6", "True")]
        [InlineData(0, 150, 30, 100, 21549.425, "7781.74", "False")]
        [InlineData(20, 0, 40, 0, 360, "25.71", "True")]
        [InlineData(-0.0972, 40, 0, 100, 21549.425, "0", "True")]
        [InlineData(10, 40, 5, 100, 21549.425, "-1", "True")]
        public void The_wait_for_a_plane_is_until_the_site_next_passes_under_it(double lat, double siteLng, double inc, double lan, double period, string wait, string north)
        {
            var printed = Run("local w is launch_plane_wait(" + lat + ", " + siteLng + ", " + inc + ", " + lan + ", " + period + "). print round(w:wait, 2). print w:north.");
            Assert.Equal(wait, RoundedLike(printed[0], wait));
            Assert.Equal(north, printed[1]);
        }

        // LAZcalc_init puts a red message on the HUD for an inclination the latitude can't reach, so
        // launch hands it one just inside what the latitude allows, keeping the sign (negative: southward).
        [Theory]
        [InlineData(0, -0.0972, "0.0982")]
        [InlineData(-0.05, -0.0972, "-0.0982")]
        [InlineData(0, 0, "0.001")]
        [InlineData(6, -0.0972, "6")]
        [InlineData(-6, 28.5, "-28.501")]
        [InlineData(179.99, 28.5, "151.499")]
        [InlineData(-180, -0.0972, "-179.9018")]
        public void The_inclination_handed_to_lib_lazcalc_is_one_the_latitude_can_reach(double inc, double lat, string clamped)
        {
            Assert.Equal(clamped, One("round(launch_laz_inc(" + inc + ", " + lat + "), 4)"));
        }

        // ---- launch: angle off the airflow ----

        [Theory]
        [InlineData(5, 100, 8000, "5")]
        [InlineData(5, 8000, 8000, "5")]
        [InlineData(5, 16000, 8000, "2.5")]
        [InlineData(5, 32000, 8000, "1.25")]
        [InlineData(5, 400000, 8000, "0.5")]
        [InlineData(0, 16000, 8000, "0")]
        public void The_allowed_angle_off_the_airflow_falls_with_dynamic_pressure_above_the_reference(double maxAoa, double q, double qRef, string allowed)
        {
            Assert.Equal(allowed, One("round(launch_allowed_aoa(" + maxAoa + ", " + q + ", " + qRef + "), 3)"));
        }

        // ---- launch: losing control ----

        [Theory]
        [InlineData("true", 0.374, 12345.6, 153.2, 150.4, 9642.7,
            "lost control: 153 degrees off the ascent path at 9643 m, burning at 37% throttle, q 12346 Pa, 150 degrees off the airflow")]
        [InlineData("false", 0, 31000, 40.6, 39.5, 4860.2,
            "lost control: 41 degrees off the ascent path at 4860 m, coasting at 0% throttle, q 31000 Pa, 40 degrees off the airflow")]
        [InlineData("false", 1, 20000, 57, 52, 9303,
            "lost control: 57 degrees off the ascent path at 9303 m, coasting at 100% throttle, q 20000 Pa, 52 degrees off the airflow")]
        public void The_lost_control_reason_says_whether_it_was_burning_or_coasting_with_throttle_q_and_the_angle_off_the_airflow(
            string burning, double throttle, double q, double offAim, double offFlow, double altitude, string reason)
        {
            Assert.Equal(reason, One("launch_lost_control(" + burning + ", " + throttle + ", " + q + ", " + offAim + ", " + offFlow + ", " + altitude + ")"));
        }

        // ---- circularize ----

        // A 70 x 100 km Kerbin orbit: sma 685 km, ecc 15/685.
        private const string Kerbin = "3.5316e12";
        private const string Sma = "685000";
        private const string Ecc = "(15000 / 685000)";

        [Theory]
        [InlineData("700000", "true", "24.7", "0")]
        [InlineData("670000", "true", "-25", "0")]
        [InlineData("685000", "true", "-0.5", "-49.7")]
        [InlineData("685000", "false", "-0.5", "49.7")]
        public void The_burn_turns_the_velocity_at_that_point_into_circular_speed_along_the_horizon(string r, string climbing, string prograde, string radial)
        {
            var printed = Run("local b is circularize_burn(" + Kerbin + ", " + r + ", " + Sma + ", " + Ecc + ", " + climbing + "). print round(b:prograde, 1). print round(b:radial, 1).");
            Assert.Equal(new[] { prograde, radial }, printed);
        }

        [Theory]
        [InlineData("670000", "0")]
        [InlineData("685000", "91.25")]
        [InlineData("700000", "180")]
        [InlineData("710000", "-1")]
        [InlineData("660000", "-1")]
        public void The_orbit_passes_an_altitude_at_one_true_anomaly_each_way_or_never(string r, string anomaly)
        {
            Assert.Equal(anomaly, One("round(circularize_anomaly(" + r + ", " + Sma + ", " + Ecc + "), 2)"));
        }

        [Theory]
        [InlineData(0, 90, 0, "1000")]
        [InlineData(90, 0, 0, "3000")]
        [InlineData(0, 90, 0.1, "872.9")]
        public void The_time_to_a_true_anomaly_follows_keplers_equation(double now, double target, double ecc, string seconds)
        {
            Assert.Equal(seconds, One("round(circularize_time_to(" + now + ", " + target + ", " + ecc + ", 4000), 1)"));
        }

        [Fact]
        public void Circularize_takes_ap_pe_or_an_altitude()
        {
            Assert.Equal("", One("circularize_unreadable(\"ap\")"));
            Assert.Equal("", One("circularize_unreadable(\"pe\")"));
            Assert.Equal("", One("circularize_unreadable(90000)"));
            Assert.Equal("at must be 'ap', 'pe' or an altitude in m, not 'apo'", One("circularize_unreadable(\"apo\")"));
        }

        [Fact]
        public void Circularize_options_take_the_spec_defaults()
        {
            Assert.Equal(new[] { "ap", "0.01", "auto", "" },
                Run("local c is claudia_options(lexicon(), circularize_defaults(), \"auto\"). print c:opt:at. print c:opt:tolerance. print c:opt:max_duration. print c:error."));
        }

        /// <summary>printed, rounded to as many decimals as expected has.</summary>
        private static string RoundedLike(string printed, string expected)
        {
            var decimals = expected.Contains(".") ? expected.Split('.')[1].Length : 0;
            return System.Math.Round(double.Parse(printed, System.Globalization.CultureInfo.InvariantCulture), decimals).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
