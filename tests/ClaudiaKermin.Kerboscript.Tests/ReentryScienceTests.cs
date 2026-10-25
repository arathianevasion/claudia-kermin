// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The pure helpers of the reentry and science blocks, run on kOS.Safe through the real bundle.</summary>
    public class ReentryScienceTests
    {
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRadius = 600000;

        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        private static double Number(string expression)
        {
            return double.Parse(One(expression), CultureInfo.InvariantCulture);
        }

        private static string D(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        [Fact]
        public void The_blocks_are_in_the_bundle()
        {
            Assert.Contains(CoreLibrary.Blocks, b => b.Name == "reentry");
            Assert.Contains(CoreLibrary.Blocks, b => b.Name == "science");
        }

        [Fact]
        public void The_prompt_docs_name_every_spec_option_and_result()
        {
            var reentry = CoreLibrary.FullDocs(new[] { "reentry" });
            foreach (var name in new[] { "chute_mode", "jettison_stage", "hold", "max_g", "max_temp_pct", "geo", "splashed" })
                Assert.Contains(name, reentry);
            var science = CoreLibrary.FullDocs(new[] { "science" });
            foreach (var name in new[] { "experiments", "all_useful", "transmit", "min_ec_pct", "results", "Sandbox" })
                Assert.Contains(name, science);
            Assert.Contains("reentry(options)", CoreLibrary.Summaries());
            Assert.Contains("science(options)", CoreLibrary.Summaries());
        }

        // ---- reentry: options ----

        [Fact]
        public void Reentry_takes_the_spec_defaults_and_knows_only_its_options()
        {
            Assert.Equal("safe", One("claudia_options(lexicon(), reentry_defaults(), \"auto\"):opt:chute_mode"));
            Assert.Equal("auto", One("claudia_options(lexicon(), reentry_defaults(), \"auto\"):opt:jettison_stage"));
            Assert.Equal("retrograde", One("claudia_options(lexicon(), reentry_defaults(), \"auto\"):opt:hold"));
            Assert.Equal("unknown option chute", One("claudia_options(lexicon(\"chute\", 1), reentry_defaults(), \"auto\"):error"));
        }

        [Fact]
        public void Reentry_options_it_cannot_read_are_named()
        {
            Assert.Equal("", One("reentry_unreadable(reentry_defaults())"));
            Assert.Equal("", One("reentry_unreadable(claudia_merge_options(lexicon(\"jettison_stage\", 3, \"chute_mode\", \"none\", \"hold\", \"none\"), reentry_defaults()))"));
            Assert.StartsWith("chute_mode must be", One("reentry_unreadable(claudia_merge_options(lexicon(\"chute_mode\", \"early\"), reentry_defaults()))"));
            Assert.StartsWith("hold must be", One("reentry_unreadable(claudia_merge_options(lexicon(\"hold\", \"prograde\"), reentry_defaults()))"));
            Assert.StartsWith("jettison_stage must be", One("reentry_unreadable(claudia_merge_options(lexicon(\"jettison_stage\", \"soon\"), reentry_defaults()))"));
        }

        // ---- reentry: chutes ----

        [Theory]
        [InlineData("safe", false, 200, 9000, 0.3, "deploy")]    // slow enough, thick enough: open
        [InlineData("safe", false, 900, 20000, 0.2, "wait")]     // far too fast
        [InlineData("safe", false, 200, 30000, 0.01, "wait")]    // too thin for a chute to hold
        [InlineData("safe", false, 900, 1400, 0.9, "deploy")]    // last height: open regardless (the #41 failure was opening this late)
        [InlineData("safe", true, 20, 500, 1.0, "wait")]         // already open
        [InlineData("none", false, 20, 500, 1.0, "wait")]        // never asked to
        public void Chutes_open_by_speed_and_pressure_and_never_later_than_the_last_height(string mode, bool open, double speed, double agl, double pressure, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "reentry_chute_action(\"{0}\", {1}, {2}, {3}, {4})", mode, open.ToString().ToLowerInvariant(), speed, agl, pressure)));
        }

        [Fact]
        public void Chutes_open_high_up_as_soon_as_the_air_and_speed_allow()
        {
            // Decelerating through 250 m/s at 14 km, well above the 6000 m the failed script waited for.
            Assert.Equal("deploy", One("reentry_chute_action(\"safe\", false, 249, 14000, 0.05)"));
            Assert.Equal("wait", One("reentry_chute_action(\"safe\", false, 251, 14000, 0.05)"));
        }

        [Theory]
        [InlineData(false, 380, 9000, true)]
        [InlineData(false, 900, 20000, false)]
        [InlineData(false, 900, 2500, true)]
        [InlineData(true, 100, 100, false)]
        public void The_heat_shield_goes_once_the_heating_is_over_or_by_the_last_height(bool gone, double speed, double agl, bool due)
        {
            Assert.Equal(due ? "True" : "False", One(string.Format(CultureInfo.InvariantCulture, "reentry_shield_due({0}, {1}, {2})", gone.ToString().ToLowerInvariant(), speed, agl)));
        }

        [Fact]
        public void The_chute_speed_is_below_the_shield_speed_so_the_shield_goes_first()
        {
            Assert.True(Number("reentry_chute_max_speed()") < Number("reentry_shield_speed()"));
        }

        [Fact]
        public void A_touchdown_under_the_limit_is_fine_and_90_ms_is_not()
        {
            Assert.Equal("True", One("reentry_touchdown_ok(7.5)"));
            Assert.Equal("False", One("reentry_touchdown_ok(90)"));
        }

        // ---- reentry: measurements ----

        [Fact]
        public void A_craft_at_rest_on_the_ground_feels_its_own_weight()
        {
            // In free fall dv/dt = gravity: 0 g. At rest in a non-rotating frame dv/dt = 0: gravity alone, 1 g.
            var mu = D(KerbinMu);
            var r = D(KerbinRadius);
            var g = KerbinMu / (KerbinRadius * KerbinRadius);
            Assert.Equal(g / 9.80665, Number("reentry_proper_g(0, 0, 0, 1, " + r + ", 0, 0, " + mu + ")"), 4);
            Assert.Equal(0, Number("reentry_proper_g(" + D(-g) + ", 0, 0, 1, " + r + ", 0, 0, " + mu + ")"), 6);
        }

        [Fact]
        public void Braking_adds_to_gravity_in_the_g_felt()
        {
            // Falling straight down at 4 g of drag: dv/dt is up (+x) at 4 g less gravity's pull down.
            var mu = D(KerbinMu);
            var r = D(KerbinRadius);
            var g = KerbinMu / (KerbinRadius * KerbinRadius);
            var dv = 4 * 9.80665 - g;
            Assert.Equal(4, Number("reentry_proper_g(" + D(dv) + ", 0, 0, 1, " + r + ", 0, 0, " + mu + ")"), 4);
        }

        // ---- reentry: time to the atmosphere ----

        private static double TimeToRadius(double ecc, double sma, double trueAnomalyDegrees, double radius, double seconds)
        {
            // Independent check: advance the mean anomaly by `seconds`, solve Kepler's equation, read the radius.
            var ta = trueAnomalyDegrees * Math.PI / 180;
            var ea = 2 * Math.Atan2(Math.Sqrt(1 - ecc) * Math.Sin(ta / 2), Math.Sqrt(1 + ecc) * Math.Cos(ta / 2));
            var m = ea - ecc * Math.Sin(ea) + seconds * Math.Sqrt(KerbinMu / Math.Pow(sma, 3));
            var e = m;
            for (var i = 0; i < 50; i++) e -= (e - ecc * Math.Sin(e) - m) / (1 - ecc * Math.Cos(e));
            return sma * (1 - ecc * Math.Cos(e));
        }

        [Theory]
        [InlineData(180)]
        [InlineData(120)]
        [InlineData(90)]
        [InlineData(250)]
        public void The_entry_eta_lands_on_the_top_of_the_atmosphere(double trueAnomaly)
        {
            // Apoapsis 200 km, periapsis 30 km above Kerbin; the atmosphere tops out at 70 km.
            var rPe = KerbinRadius + 30000;
            var rAp = KerbinRadius + 200000;
            var sma = (rPe + rAp) / 2;
            var ecc = (rAp - rPe) / (rAp + rPe);
            var entry = KerbinRadius + 70000;
            var eta = Number(string.Format(CultureInfo.InvariantCulture, "reentry_entry_eta({0}, {1}, {2}, {3}, {4})", D(ecc), D(sma), D(KerbinMu), D(entry), D(trueAnomaly)));
            Assert.True(eta > 0, "eta " + eta);
            Assert.Equal(entry, TimeToRadius(ecc, sma, trueAnomaly, entry, eta), 0);
        }

        [Fact]
        public void The_entry_eta_is_zero_inside_the_atmosphere_and_minus_one_for_an_orbit_that_stays_out()
        {
            var rPe = KerbinRadius + 30000;
            var rAp = KerbinRadius + 200000;
            var sma = (rPe + rAp) / 2;
            var ecc = (rAp - rPe) / (rAp + rPe);
            Assert.Equal(0, Number(string.Format(CultureInfo.InvariantCulture, "reentry_entry_eta({0}, {1}, {2}, {3}, 0)", D(ecc), D(sma), D(KerbinMu), D(KerbinRadius + 70000))));

            var outPe = KerbinRadius + 100000;
            var outSma = (outPe + rAp) / 2;
            var outEcc = (rAp - outPe) / (rAp + outPe);
            Assert.Equal(-1, Number(string.Format(CultureInfo.InvariantCulture, "reentry_entry_eta({0}, {1}, {2}, {3}, 180)", D(outEcc), D(outSma), D(KerbinMu), D(KerbinRadius + 70000))));
            Assert.Equal(-1, Number(string.Format(CultureInfo.InvariantCulture, "reentry_entry_eta(1.2, {0}, {1}, {2}, 180)", D(outSma), D(KerbinMu), D(KerbinRadius + 70000))));
        }

        // ---- science ----

        [Fact]
        public void Science_takes_the_spec_defaults_and_knows_only_its_options()
        {
            Assert.Equal("all_useful", One("claudia_options(lexicon(), science_defaults(), 1800):opt:experiments"));
            Assert.Equal("auto", One("claudia_options(lexicon(), science_defaults(), 1800):opt:transmit"));
            Assert.Equal("10", One("claudia_options(lexicon(), science_defaults(), 1800):opt:min_ec_pct"));
            Assert.Equal("unknown option experiment", One("claudia_options(lexicon(\"experiment\", 1), science_defaults(), 1800):error"));
        }

        [Fact]
        public void Science_options_it_cannot_read_are_named()
        {
            Assert.Equal("", One("science_unreadable(science_defaults())"));
            Assert.Equal("", One("science_unreadable(claudia_merge_options(lexicon(\"experiments\", list(\"mysteryGoo\"), \"transmit\", \"keep\"), science_defaults()))"));
            Assert.StartsWith("transmit must be", One("science_unreadable(claudia_merge_options(lexicon(\"transmit\", \"yes\"), science_defaults()))"));
            Assert.StartsWith("experiments must be", One("science_unreadable(claudia_merge_options(lexicon(\"experiments\", 3), science_defaults()))"));
        }

        [Fact]
        public void All_useful_names_nothing_and_a_single_name_is_a_list()
        {
            Assert.Equal("0", One("science_names(\"all_useful\"):length"));
            Assert.Equal("1", One("science_names(\"temperatureScan\"):length"));
            Assert.Equal("2", One("science_names(list(\"a\", \"b\")):length"));
        }

        [Fact]
        public void A_name_matches_the_experiment_id_part_title_or_part_name()
        {
            Assert.Equal("True", One("science_matches(list(\"temperatureScan\"), \"temperatureScan\", \"2HOT Thermometer\", \"sensorThermometer\")"));
            Assert.Equal("True", One("science_matches(list(\"2HOT Thermometer\"), \"temperatureScan\", \"2HOT Thermometer\", \"sensorThermometer\")"));
            Assert.Equal("True", One("science_matches(list(\"sensorThermometer\"), \"temperatureScan\", \"2HOT Thermometer\", \"sensorThermometer\")"));
            Assert.Equal("False", One("science_matches(list(\"mysteryGoo\"), \"temperatureScan\", \"2HOT Thermometer\", \"sensorThermometer\")"));
        }

        // science_decision(explicit, rerunnable, has_data, inoperable, available, tracked, left)
        [Theory]
        [InlineData("false, true, false, false, true, true, 12", "run")]
        [InlineData("false, true, false, false, true, true, 0", "skip: no science left here")]
        [InlineData("false, true, false, false, true, false, -1", "run")]                                  // Sandbox: nothing tracked, run once
        [InlineData("true, true, false, false, true, true, 0", "run")]                                      // named: run whether or not science is left
        [InlineData("false, false, true, false, true, true, 30", "skip: holds data and is not rerunnable, so it was not overwritten")]
        [InlineData("true, false, true, false, true, true, 30", "skip: holds data and is not rerunnable, so it was not overwritten")]
        [InlineData("false, true, true, false, true, false, -1", "skip: holds data, so it was not overwritten")]  // Sandbox: a second run would be a repeat
        [InlineData("false, true, false, true, true, true, 30", "skip: inoperable")]
        [InlineData("true, true, false, false, false, true, 30", "skip: nothing to measure in this situation")]
        public void An_experiment_runs_only_when_it_is_useful_and_would_overwrite_nothing(string args, string expected)
        {
            Assert.Equal(expected, One("science_decision(" + args + ")"));
        }

        // science_transmit_choice(mode, returning, loses_value, can_send, ec_pct, min_ec_pct)
        [Theory]
        [InlineData("\"keep\", false, false, true, 90, 10", "keep: transmit is 'keep'")]
        [InlineData("\"transmit\", false, false, true, 90, 10", "transmit")]
        [InlineData("\"transmit\", true, true, true, 90, 10", "transmit")]                                 // asked to: transmit even when returning
        [InlineData("\"auto\", false, true, true, 90, 10", "transmit")]                                    // not coming home: nothing to lose
        [InlineData("\"auto\", true, false, true, 90, 10", "transmit")]                                    // coming home but transmitting loses nothing
        [InlineData("\"auto\", true, true, true, 90, 10", "keep: the craft is returning and transmitting would lose value")]
        [InlineData("\"transmit\", false, false, false, 90, 10", "keep: no antenna or no link")]
        [InlineData("\"transmit\", false, false, true, 9.9, 10", "keep: electric charge is under min_ec_pct")]
        [InlineData("\"auto\", false, false, true, 10, 10", "transmit")]
        public void Data_is_transmitted_or_kept_by_mode_return_trip_link_and_charge(string args, string expected)
        {
            Assert.Equal(expected, One("science_transmit_choice(" + args + ")"));
        }
    }
}
