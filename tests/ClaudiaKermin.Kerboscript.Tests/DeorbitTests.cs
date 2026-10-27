// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// The pure helpers of the deorbit Building Block (#282), run on kOS.Safe through the real bundle: its options, the targets
    /// it reads, and the failures it gives before any burn. The burn search is the mod's (Core's DeorbitBurnTests); flying it
    /// needs KSP: those are tests/blocks/deorbit_*.
    /// </summary>
    public class DeorbitTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        private static string Unreadable(string options)
        {
            return One("deorbit_unreadable(claudia_options(" + options + ", deorbit_defaults(), \"auto\"):opt)");
        }

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("required|auto|10|none|auto", One("deorbit_defaults():values:join(\"|\") + \"|\" + claudia_options(lexicon(), deorbit_defaults(), \"auto\"):opt:max_duration"));
        }

        [Theory]
        [InlineData("lexicon(\"target\", \"ksc\")")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", -0.1, \"lng\", -74.5))")]
        [InlineData("lexicon(\"target\", \"ksc\", \"max_wait\", 3600, \"accuracy_km\", 25)")]
        [InlineData("lexicon(\"target\", \"ksc\", \"max_wait\", \"auto\", \"stage_limit\", 1, \"drop_empty\", \"tank\")")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", 1)")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", 0)")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", \"transfer\")")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", list(\"fuelTank.long\", \"transfer\"))")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", \"none\")")]
        public void Options_it_can_use_pass(string options)
        {
            Assert.Equal("", Unreadable(options));
        }

        [Theory]
        [InlineData("lexicon()", "target is required")]
        [InlineData("lexicon(\"target\", \"kerbin\")", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not 'kerbin'")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 1))", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a lexicon without lat and lng")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 1, \"lng\", \"east\"))", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a lexicon whose lat and lng aren't numbers")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 95, \"lng\", 0))", "target's lat must be from -90 to 90, not 95")]
        [InlineData("lexicon(\"target\", 3)", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a Scalar")]
        [InlineData("lexicon(\"target\", \"ksc\", \"max_wait\", \"soon\")", "max_wait must be 'auto' or seconds above 0, not 'soon'")]
        [InlineData("lexicon(\"target\", \"ksc\", \"max_wait\", 0)", "max_wait must be 'auto' or seconds above 0, not 0")]
        [InlineData("lexicon(\"target\", \"ksc\", \"accuracy_km\", -1)", "accuracy_km must be km above 0, not -1")]
        [InlineData("lexicon(\"target\", \"ksc\", \"accuracy_km\", \"close\")", "accuracy_km must be km above 0, not close")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", -1)", "separate must be 'none', a stage number or part names or tags, not -1")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", 1.5)", "separate must be 'none', a stage number or part names or tags, not 1.5")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", \"\")", "separate must be 'none', a stage number or part names or tags, not ''")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", list())", "separate must be 'none', a stage number or part names or tags, not an empty list")]
        [InlineData("lexicon(\"target\", \"ksc\", \"separate\", lexicon())", "separate must be 'none', a stage number or part names or tags, not a Lexicon")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, Unreadable(options));
        }

        [Fact]
        public void An_unknown_option_is_refused_by_the_shared_check()
        {
            Assert.Equal("unknown option chute_mode", One("claudia_options(lexicon(\"target\", \"ksc\", \"chute_mode\", \"safe\"), deorbit_defaults(), \"auto\"):error"));
        }

        // ---- the body and the craft ----

        [Fact]
        public void A_body_with_no_atmosphere_fails_with_body_has_no_atmosphere()
        {
            Assert.Equal("body has no atmosphere", One("deorbit_body_problem(false, \"ORBITING\")"));
        }

        [Theory]
        [InlineData("ORBITING", "")]
        [InlineData("SUB_ORBITAL", "")]
        [InlineData("LANDED", "the craft is landed, not in orbit: deorbit burns from orbit")]
        [InlineData("SPLASHED", "the craft is splashed, not in orbit: deorbit burns from orbit")]
        [InlineData("PRELAUNCH", "the craft is prelaunch, not in orbit: deorbit burns from orbit")]
        public void A_craft_with_an_atmosphere_below_must_be_off_the_ground(string situation, string reason)
        {
            Assert.Equal(reason, One("deorbit_body_problem(true, \"" + situation + "\")"));
        }

        // ---- the target ----

        [Fact]
        public void Ksc_is_the_launch_pad_on_Kerbin()
        {
            Assert.Equal("|-0.0972|-74.5577", One("deorbit_site(\"ksc\", \"Kerbin\"):values:join(\"|\")"));
        }

        [Fact]
        public void Ksc_round_another_body_fails()
        {
            Assert.Equal("ksc is on Kerbin, and the craft orbits Eve: give a LATLNG there", One("deorbit_site(\"ksc\", \"Eve\"):error"));
        }

        [Fact]
        public void A_lat_lng_lexicon_is_the_site()
        {
            Assert.Equal("|12.5|-30", One("deorbit_site(lexicon(\"lat\", 12.5, \"lng\", -30), \"Duna\"):values:join(\"|\")"));
        }

        [Fact]
        public void Max_wait_auto_is_one_turn_of_the_body()
        {
            Assert.Equal("21549.425", One("deorbit_max_wait(\"auto\", 21549.425)"));
            Assert.Equal("3600", One("deorbit_max_wait(3600, 21549.425)"));
        }

        [Fact]
        public void The_burn_search_is_asked_for_the_site_window_ground_accuracy_and_isp()
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(
                "local q is deorbit_query(lexicon(\"lat\", -0.0972, \"lng\", -74.5577), 1240, 21549.425, -150, 10, 320, list()).\n"
                + "print q:lat. print q:lng. print q:earliest_ut. print q:max_wait. print q:ground_alt. print q:accuracy_km. print q:isp. print q:leave_out:length.")).ToArray();
            // Water's ground is the sea, not the sea bed.
            Assert.Equal(new[] { "-0.0972", "-74.5577", "1240", "21549.425", "0", "10", "320", "0" }, printed);
        }

        [Fact]
        public void Parts_to_separate_are_left_out_of_the_burn_search_and_the_burn_does_not_lighten_what_stays()
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(
                "local q is deorbit_query(lexicon(\"lat\", 0, \"lng\", 0), 1240, 3600, 0, 10, 320, list(\"11\", \"12\")).\n"
                + "print q:leave_out:join(\",\"). print q:isp.")).ToArray();
            // The burn's propellant is in the stage it drops: the craft that re-enters weighs what it does now.
            Assert.Equal(new[] { "11,12", "0" }, printed);
        }

        // ---- after the burn ----

        [Theory]
        [InlineData("lexicon(\"error\", \"\", \"error_km\", 3.2)", 10, "")]
        [InlineData("lexicon(\"error\", \"\", \"error_km\", 10)", 10, "")]
        [InlineData("lexicon(\"error\", \"\", \"error_km\", 733.48)", 10, "the burn is flown, but the craft is predicted to land 733 km from the target, over accuracy_km 10: it is coming down, so call reentry")]
        [InlineData("lexicon(\"error\", \"the craft skips out of Kerbin's atmosphere and does not fall back in\")", 10, "the burn is flown, but its landing can't be predicted (the craft skips out of Kerbin's atmosphere and does not fall back in)")]
        public void Success_is_a_landing_predicted_within_accuracy_after_the_burn(string after, double accuracy, string reason)
        {
            Assert.Equal(reason, One("deorbit_after_burn(" + after + ", " + accuracy + ")"));
        }

        [Fact]
        public void The_burn_is_at_least_4_minutes_ahead()
        {
            Assert.Equal("240", One("deorbit_lead_s()"));
        }

        [Fact]
        public void The_predicted_landing_is_reported_to_four_places()
        {
            Assert.Equal("-0.0972|-74.5577", One("deorbit_geo(lexicon(\"error\", \"\", \"lat\", -0.097213, \"lng\", -74.557712)):values:join(\"|\")"));
        }

        [Theory]
        [InlineData(80, 500, "")]
        [InlineData(80, 0, "")]
        [InlineData(80, 50, "not enough delta-v: the burn needs 80 m/s, 50 m/s left")]
        public void A_burn_over_the_delta_v_left_fails_before_burning(double dv, double left, string reason)
        {
            Assert.Equal(reason, One("deorbit_short_of_dv(" + dv + ", " + left + ")"));
        }
    }
}
