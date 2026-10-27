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
    /// The transfer and return blocks' pure helpers and the bundled RSVP, run on kOS.Safe
    /// through the real bundle. Flying them needs KSP: those are the block tests in tests/blocks.
    /// </summary>
    public class TransferAndReturnTests
    {
        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        private static double Number(string expression)
        {
            return double.Parse(One(expression), CultureInfo.InvariantCulture);
        }

        // ---- RSVP ----

        [Fact]
        public void Rsvp_loads_from_the_bundle_with_every_function_it_exports_but_compile_to()
        {
            var printed = Run("claudia_rsvp(). print rsvp:length. print rsvp:haskey(\"goto\"). print rsvp:haskey(\"line_search\"). print rsvp:haskey(\"time_at_periapsis\"). print rsvp:haskey(\"compile_to\").");
            Assert.Equal(new[] { "31", "True", "True", "True", "False" }, printed);
        }

        [Fact]
        public void Rsvp_loads_once_however_often_a_block_asks_for_it()
        {
            // A second load would add every export again, which a lexicon refuses.
            Assert.Equal("31", One("claudia_rsvp():length + 0 * claudia_rsvp():length"));
        }

        // ---- transfer options ----

        private static string Unreadable(string options)
        {
            return One("transfer_to_unreadable(claudia_options(lexicon(" + options + "), transfer_to_defaults(), \"auto\"):opt)");
        }

        [Fact]
        public void Transfer_takes_the_spec_options_and_defaults()
        {
            var printed = Run("local o is claudia_options(lexicon(), transfer_to_defaults(), \"auto\"). print o:error. print o:opt:window. print o:opt:correction. print o:opt:max_duration.");
            Assert.Equal(new[] { "", "next", "True", "auto" }, printed);
        }

        [Fact]
        public void Transfer_needs_a_target_and_a_target_periapsis()
        {
            Assert.Equal("target is required", Unreadable(""));
            Assert.Equal("target_pe is required", Unreadable("\"target\", \"Mun\""));
            Assert.Equal("", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000"));
        }

        [Fact]
        public void Transfer_refuses_option_values_it_cannot_read()
        {
            Assert.Equal("target must be a body or its name", Unreadable("\"target\", 5, \"target_pe\", 30000"));
            Assert.Equal("target_pe must be metres above 0, not -5", Unreadable("\"target\", \"Mun\", \"target_pe\", -5"));
            Assert.Equal("window must be 'next' or a time (UT seconds), not 'later'", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"window\", \"later\""));
            Assert.Equal("correction must be true or false", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"correction\", \"yes\""));
            Assert.Equal("target_pe must be metres above 0, not 30km", Unreadable("\"target\", \"Mun\", \"target_pe\", \"30km\""));
            Assert.Equal("", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"window\", 5000, \"correction\", false"));
        }

        [Fact]
        public void An_rsvp_failure_gives_its_problems_as_the_reason()
        {
            Assert.Equal("rsvp found no transfer: Existing maneuver node already exists; Option 'search_duration' is '-1', expected positive number",
                One("transfer_to_rsvp_reason(lexicon(\"success\", false, \"problems\", lexicon(102, \"Existing maneuver node already exists\", 4, \"Option 'search_duration' is '-1', expected positive number\")))"));
            Assert.Equal("rsvp found no transfer", One("transfer_to_rsvp_reason(lexicon(\"success\", false))"));
        }

        // ---- Departure span ----

        [Theory]
        [InlineData("\"next\"", "1300")]
        [InlineData("2000", "2000")]
        [InlineData("1100", "1300")]
        public void The_departure_is_searched_from_five_minutes_ahead_or_from_the_window(string window, string earliest)
        {
            Assert.Equal(earliest, One("claudia_earliest(1000, " + window + ")"));
        }

        [Theory]
        [InlineData(2, 3, "6")]
        [InlineData(3, 2, "6")]
        [InlineData(5, 5, "5")]
        public void The_next_window_comes_round_once_a_synodic_period(double a, double b, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "round(transfer_to_synodic({0}, {1}), 6)", a, b)));
        }

        private static string[] RsvpOptions(double craftPeriod, double targetPeriod)
        {
            return Run(string.Format(CultureInfo.InvariantCulture,
                "local o is transfer_to_rsvp_options(1300, 30000, {0}, {1}). print o:keys:length. print o:create_maneuver_nodes. print o:final_orbit_periapsis. print o:final_orbit_type. print o:earliest_departure. print round(o:search_duration, 3). print round(o:search_interval, 3). print o:haskey(\"max_time_of_flight\").",
                craftPeriod, targetPeriod));
        }

        // #228: a craft in a 100 km Kerbin orbit (1960 s) against the Mun (138984 s). A window comes round
        // every 1988 s. One synodic period covers every phase of the Mun against the craft, so RSVP finds
        // the next window, not the cheapest of the 70 in a Mun orbit (60,000 s away in-game). The interval
        // seeds about 100 departures 20 s apart: the default seeded three, which found 1649 m/s against
        // about 860.
        [Fact]
        public void The_options_passed_to_rsvp_search_the_next_window_densely_and_capture_at_target_pe()
        {
            Assert.Equal(new[] { "6", "first", "30000", "circular", "1300", "1988.036", "19.88", "False" }, RsvpOptions(1960, 138984));
        }

        [Fact]
        public void The_search_is_the_same_whichever_orbit_is_the_craft()
        {
            Assert.Equal(RsvpOptions(1960, 138984), RsvpOptions(138984, 1960));
        }

        [Fact]
        public void Equal_periods_search_one_period_rather_than_forever()
        {
            Assert.Equal(new[] { "6", "first", "30000", "circular", "1300", "5000", "50", "False" }, RsvpOptions(5000, 5000));
        }

        // ---- Coordinate descent ----

        [Fact]
        public void Descent_finds_the_minimum_of_each_coordinate()
        {
            var printed = Run("local d is claudia_descend({ parameter x. return abs(x[0] - 3) + abs(x[1] + 2). }, list(0, 0), list(1, 1), 6, 0). print d:x[0]. print d:x[1]. print d:cost.");
            Assert.Equal(new[] { "3", "-2", "0" }, printed);
        }

        [Fact]
        public void Descent_halves_its_steps_to_close_in_between_them()
        {
            Assert.True(Number("abs(claudia_descend({ parameter x. return abs(x[0] - 0.3). }, list(0), list(1), 10, 0):x[0] - 0.3)") < 0.01);
        }

        [Fact]
        public void Descent_stops_once_the_cost_is_good_enough()
        {
            var printed = Run("local d is claudia_descend({ parameter x. return abs(x[0] - 3). }, list(0), list(1), 10, 1.5). print d:x[0]. print d:cost.");
            Assert.Equal(new[] { "2", "1" }, printed);
        }

        // ---- return ----

        [Fact]
        public void Return_options_take_the_spec_defaults()
        {
            var printed = Run("local o is claudia_options(lexicon(), return_defaults(), \"auto\"). print o:error. print o:opt:target_pe. print o:opt:window. print o:opt:correction.");
            Assert.Equal(new[] { "", "35000", "next", "True" }, printed);
            Assert.Equal("", One("return_unreadable(claudia_options(lexicon(), return_defaults(), \"auto\"):opt)"));
            Assert.Equal("target_pe must be metres above 0, not 0", One("return_unreadable(claudia_options(lexicon(\"target_pe\", 0), return_defaults(), \"auto\"):opt)"));
        }

        private static string ReturnUnreadable(string options)
        {
            return One("return_unreadable(claudia_options(" + options + ", return_defaults(), \"auto\"):opt)");
        }

        [Fact]
        public void Return_takes_no_target_by_default()
        {
            Assert.Equal("none|False", One("claudia_options(lexicon(), return_defaults(), \"auto\"):opt:target + \"|\" + return_aims(claudia_options(lexicon(), return_defaults(), \"auto\"):opt)"));
        }

        [Theory]
        [InlineData("lexicon(\"target\", \"ksc\")")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", -0.1, \"lng\", -74.5))")]
        [InlineData("lexicon(\"target\", \"ksc\", \"target_pe\", 40000, \"correction\", false)")]
        [InlineData("lexicon(\"target\", \"none\")")]
        public void Return_takes_the_targets_deorbit_takes(string options)
        {
            Assert.Equal("", ReturnUnreadable(options));
        }

        [Theory]
        [InlineData("lexicon(\"target\", \"kerbin\")", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not 'kerbin'")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 1))", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a lexicon without lat and lng")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 95, \"lng\", 0))", "target's lat must be from -90 to 90, not 95")]
        [InlineData("lexicon(\"target\", 3)", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a Scalar")]
        public void Return_refuses_a_target_it_cannot_read(string options, string reason)
        {
            Assert.Equal(reason, ReturnUnreadable(options));
        }

        [Fact]
        public void Return_asks_the_predictor_for_the_path_it_means_with_its_elements_and_the_target()
        {
            var stub = "local kerbin is lexicon(\"name\", \"Kerbin\"). local orb is lexicon(\"semimajoraxis\", 9e6, \"eccentricity\", 0.9, \"inclination\", 2, \"lan\", 3, \"argumentofperiapsis\", 4, \"meananomalyatepoch\", 5, \"epoch\", 6, \"body\", kerbin). ";
            var asked = Run(stub + "local q is return_impact_query(orb, 777, lexicon(\"lat\", -0.1, \"lng\", -74.5), -20). print q:keys:length. print q:from_ut. print q:lat. print q:lng. print q:ground_alt. print q:orbit_sma. print q:orbit_ecc. print q:orbit_inc. print q:orbit_lan. print q:orbit_argpe. print q:orbit_mna. print q:orbit_epoch. print q:orbit_body.");
            Assert.Equal(new[] { "12", "777", "-0.1", "-74.5", "0", "9000000", "0.9", "2", "3", "4", "5", "6", "Kerbin" }, asked);
        }

        [Fact]
        public void An_aimed_correction_lowers_the_km_to_the_target_and_otherwise_draws_the_path_into_the_air()
        {
            var patch = "local p is lexicon(\"orbit\", lexicon(\"periapsis\", 80000), \"soi_ut\", 0). ";
            Assert.Equal("12.5", Run(patch + "print claudia_aim_cost({ parameter o, u. return 12.5. }, p, 35000).").Single());
            // No landing: worse than any real one, and nearer to the target periapsis is better.
            Assert.Equal("10045", Run(patch + "print claudia_aim_cost({ parameter o, u. return -1. }, p, 35000).").Single());
            // No path to Kerbin at all.
            Assert.Equal("100000", One("claudia_aim_cost({ parameter o, u. return 1. }, \"none\", 35000)"));
        }

        // A worked example: from a 30 km Mun orbit to a 35 km Kerbin periapsis (stock body values).
        private const string MunReturn = "return_ejection(3.5316e12, 12000000, 635000, 6.5138398e10, 230000)";

        [Fact]
        public void A_mun_return_ejects_at_about_307_ms_and_132_degrees_before_the_asymptote()
        {
            Assert.InRange(Number(MunReturn + ":v_inf"), 370, 371);
            Assert.InRange(Number(MunReturn + ":dv"), 306, 307.5);
            Assert.InRange(Number(MunReturn + ":eject_angle"), 132, 132.7);
        }

        [Fact]
        public void Aiming_at_the_moons_own_distance_needs_only_escape_and_ejects_straight_back()
        {
            const double mu = 6.5138398e10, r0 = 230000;
            var printed = Run("local e is return_ejection(3.5316e12, 12000000, 12000000, " + mu.ToString("R", CultureInfo.InvariantCulture) + ", " + r0 + "). print round(e:v_inf, 3). print round(e:eject_angle, 3). print round(e:dv, 2).");
            Assert.Equal("0", printed[0]);
            Assert.Equal("180", printed[1]);
            Assert.Equal(Math.Round(Math.Sqrt(2 * mu / r0) - Math.Sqrt(mu / r0), 2).ToString(CultureInfo.InvariantCulture), printed[2]);
        }

        [Fact]
        public void The_time_from_a_radius_down_to_periapsis_follows_keplers_equation()
        {
            // Ellipse a = 10,000 km, e = 0.5, at r = a: E = 90 degrees, M = pi/2 - e.
            Assert.InRange(Number("return_time_to_pe(10000000, 0.5, 3.5316e12, 10000000)"), 18016, 18021);
            // Hyperbola a = -10,000 km, e = 1.5, at r = 12,500 km: cosh H = 1.5.
            Assert.InRange(Number("return_time_to_pe(-10000000, 1.5, 3.5316e12, 12500000)"), 12023, 12028);
        }

        [Fact]
        public void A_radius_the_orbit_never_comes_down_to_takes_no_time()
        {
            // Periapsis is at 5,000 km; the atmosphere top at 4,000 km is never reached.
            Assert.Equal("0", One("return_time_to_pe(10000000, 0.5, 3.5316e12, 4000000)"));
        }
    }
}
