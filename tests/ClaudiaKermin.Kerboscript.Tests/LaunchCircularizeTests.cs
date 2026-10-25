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
            return KosHarness.Run(CoreLibrary.Bundle(missionScript, 1, 10800)).ToArray();
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

        [Theory]
        [InlineData(0.9, 6.4, 9.81, 167.97, "0.374")]
        [InlineData(0.9, 20, 9.81, 100, "1")]
        [InlineData(0.05, 6.4, 9.81, 167.97, "0")]
        [InlineData(0.9, 6.4, 9.81, 0, "0")]
        public void In_thick_air_the_throttle_floor_keeps_thrust_at_least_equal_to_weight(double pressureRatio, double mass, double g, double thrust, string floor)
        {
            Assert.Equal(floor, One("round(launch_throttle_floor(" + pressureRatio + ", " + mass + ", " + g + ", " + thrust + "), 3)"));
        }

        // ---- launch: heading and the plane ----

        // The heading itself is KSLib's LAZcalc, which reads the ship; it is checked in game (launch_orbit).
        [Fact]
        public void The_heading_comes_from_the_bundled_lib_lazcalc_which_is_not_a_Building_Block()
        {
            var bundle = CoreLibrary.Bundle("", 1, 10800);
            Assert.Contains("FUNCTION LAZcalc_init {", bundle);
            Assert.DoesNotContain("@CLOBBERBUILTINS", bundle);
            Assert.DoesNotContain("function launch_azimuth", bundle);
            Assert.Contains("LAZcalc(laz)", CoreLibrary.Files.Single(f => f.Name == "launch").Source);
            Assert.DoesNotContain(CoreLibrary.Blocks, b => b.Name.StartsWith("lib_lazcalc"));
            Assert.DoesNotContain("LAZcalc", CoreLibrary.FullDocs() + CoreLibrary.Summaries());
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
