// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The pure helpers of the tune_pe Building Block, run on kOS.Safe through the real bundle. Flying it needs KSP: those are tests/blocks/tune_pe_*.</summary>
    public class TunePeTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        private static double Number(string expression)
        {
            return double.Parse(One(expression), CultureInfo.InvariantCulture);
        }

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("required|current|1000|50|auto", One("tune_pe_defaults():values:join(\"|\") + \"|\" + claudia_options(lexicon(), tune_pe_defaults(), \"auto\"):opt:max_duration"));
        }

        [Theory]
        [InlineData("lexicon(\"value\", 50000)", "")]
        [InlineData("lexicon(\"value\", 50000, \"patch\", \"next\", \"tolerance\", 500, \"max_dv\", 10)", "")]
        [InlineData("lexicon()", "value is required")]
        [InlineData("lexicon(\"value\", -5)", "value must be metres above 0, not -5")]
        [InlineData("lexicon(\"value\", \"high\")", "value must be metres above 0, not high")]
        [InlineData("lexicon(\"value\", 50000, \"patch\", \"later\")", "patch must be 'current' or 'next', not later")]
        [InlineData("lexicon(\"value\", 50000, \"tolerance\", 0)", "tolerance must be metres above 0, not 0")]
        [InlineData("lexicon(\"value\", 50000, \"max_dv\", \"lots\")", "max_dv must be m/s above 0, not lots")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("tune_pe_unreadable(claudia_options(" + options + ", tune_pe_defaults(), \"auto\"):opt)"));
        }

        [Fact]
        public void An_unknown_option_is_refused_by_the_shared_check()
        {
            Assert.Equal("unknown option colour", One("claudia_options(lexicon(\"colour\", 1), tune_pe_defaults(), \"auto\"):error"));
        }

        // ---- the burn point ----

        [Fact]
        public void The_current_patch_burns_at_the_apoapsis()
        {
            Assert.Equal("|3000", One("tune_pe_burn_point(\"current\", 0.3, 3000, 7000, false, 0):error + \"|\" + tune_pe_burn_point(\"current\", 0.3, 3000, 7000, false, 0):in"));
        }

        [Fact]
        public void An_apoapsis_under_two_minutes_off_waits_for_the_next_one()
        {
            Assert.Equal("7060", One("tune_pe_burn_point(\"current\", 0.3, 60, 7000, false, 0):in"));
        }

        [Fact]
        public void The_current_patch_refuses_an_escape_orbit()
        {
            Assert.Equal("the orbit escapes (ecc 1.2): no apoapsis to burn at", One("tune_pe_burn_point(\"current\", 1.2, 0, 0, true, 5000):error"));
        }

        [Fact]
        public void The_current_patch_refuses_an_apoapsis_after_the_sphere_of_influence_change()
        {
            Assert.Equal("the apoapsis comes after the sphere-of-influence change (in 4000 s): use patch next", One("tune_pe_burn_point(\"current\", 0.9, 9000, 20000, true, 4000):error"));
            Assert.Equal("", One("tune_pe_burn_point(\"current\", 0.9, 3000, 20000, true, 4000):error"));
        }

        [Fact]
        public void The_next_patch_burns_halfway_to_the_sphere_of_influence_change()
        {
            Assert.Equal("|2500", One("tune_pe_burn_point(\"next\", 0.9, 9000, 20000, true, 5000):error + \"|\" + tune_pe_burn_point(\"next\", 0.9, 9000, 20000, true, 5000):in"));
        }

        [Fact]
        public void The_next_patch_fails_without_one()
        {
            Assert.Equal("no next patch: the orbit stays in this sphere of influence", One("tune_pe_burn_point(\"next\", 0.1, 3000, 7000, false, 0):error"));
        }

        [Fact]
        public void The_next_patch_fails_when_the_change_is_too_close_to_fly_a_burn()
        {
            Assert.Equal("the sphere-of-influence change is too close (200 s) to plan and fly a burn", One("tune_pe_burn_point(\"next\", 0.9, 9000, 20000, true, 200):error"));
        }

        // ---- the burn ----

        [Fact]
        public void Lowering_the_periapsis_from_the_apoapsis_is_a_retrograde_burn_that_lands_on_the_new_orbit()
        {
            // 200 km x 1000 km Kerbin orbit, periapsis lowered to 100 km.
            var dv = Number("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 700000):prograde");
            Assert.InRange(dv, -60, -50);
            // The vis-viva check: speed at the apoapsis on the new orbit (sma 1.15e6) minus speed on the old (sma 1.2e6).
            var expected = System.Math.Sqrt(3.5316e12 * (2 / 1.6e6 - 1 / 1.15e6)) - System.Math.Sqrt(3.5316e12 * (2 / 1.6e6 - 1 / 1.2e6));
            Assert.Equal(expected, dv, 3);
        }

        [Fact]
        public void Raising_the_periapsis_is_a_prograde_burn_and_the_same_periapsis_costs_nothing()
        {
            Assert.True(Number("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 900000):prograde") > 0);
            Assert.Equal(0, Number("round(tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 800000):prograde, 6)"), 6);
        }

        [Fact]
        public void A_periapsis_above_the_apoapsis_is_refused()
        {
            Assert.Equal("value is above the apoapsis (1600000 m from the centre): a burn there can't lift the periapsis past it",
                One("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 1700000):error"));
        }

        // ---- the budget ----

        [Theory]
        [InlineData(-35.2, 50, "")]
        [InlineData(50, 50, "")]
        [InlineData(-61.04, 50, "needs 61 m/s, over max_dv 50")]
        [InlineData(12, 10, "needs 12 m/s, over max_dv 10")]
        public void A_burn_over_max_dv_fails_with_what_it_needs(double dv, double maxDv, string reason)
        {
            Assert.Equal(reason, One("tune_pe_over_budget(" + dv.ToString(CultureInfo.InvariantCulture) + ", " + maxDv + ")"));
        }
    }
}
