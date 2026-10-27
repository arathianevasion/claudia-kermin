// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The reentry predictor against the reentry research's simulation cases (#247): its capsule, atmosphere and burn
    /// (<see cref="ReentrySim"/>), and the landing points it published, downrange of the burn point in the ground frame. The
    /// research rounded to the km, and integrated the vacuum leg where the predictor follows the Kepler orbit, so the
    /// tolerance is a km or two.
    /// </summary>
    public sealed class ReentryPredictorTests
    {
        private static ReentryPrediction Fly(double pe = 30000, ReentrySim.Capsule craft = null, double step = ReentryPredictor.Step)
        {
            craft = craft ?? new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(pe: pe), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft,
                ReentrySim.Frame(), step);
            Assert.Null(landed.Error);
            return landed;
        }

        private static double Downrange(ReentryPrediction landed)
        {
            return ReentrySim.Km(landed.Longitude);
        }

        [Fact]
        public void The_reference_case_lands_1097_km_downrange_of_the_burn_270_s_to_entry_and_430_s_after()
        {
            var landed = Fly();
            Assert.InRange(Downrange(landed), 1095, 1099);
            Assert.Equal(0, landed.Latitude, 6);
            Assert.InRange(landed.EntryUt, 260, 280);
            Assert.InRange(landed.LandingUt - landed.EntryUt, 420, 440);
            // "Peak about 2.5 g in this atmosphere."
            Assert.InRange(landed.PeakG, 2, 3);
        }

        [Theory]
        [InlineData(10000, 927)]
        [InlineData(20000, 1002)]
        [InlineData(30000, 1097)]
        [InlineData(40000, 1225)]
        [InlineData(50000, 1414)]
        public void The_periapsis_sweep_lands_where_the_research_has_it(double pe, double km)
        {
            Assert.InRange(Downrange(Fly(pe)), km - 2, km + 2);
        }

        [Fact]
        public void The_landing_is_634_km_short_of_the_vacuum_periapsis_point()
        {
            var orbit = ReentrySim.AfterBurn();
            var peUt = orbit.NextPeriapsis(0).Value;
            double lat, lng;
            ReentrySim.Frame().Ground(orbit.Position(peUt), peUt, out lat, out lng);
            Assert.InRange(ReentrySim.Km(lng) - Downrange(Fly()), 631, 637);
        }

        [Theory]
        [InlineData(0.9, 13)]
        [InlineData(1.1, -12)]
        [InlineData(0.8, 28)]
        [InlineData(1.2, -23)]
        public void A_drag_error_moves_the_landing_as_the_research_found(double scale, double km)
        {
            var shift = Downrange(Fly(craft: new ReentrySim.Capsule { Scale = scale })) - Downrange(Fly());
            Assert.InRange(shift, km - 1.5, km + 1.5);
        }

        [Fact]
        public void A_km_of_periapsis_moves_the_landing_11_km_at_a_30_km_periapsis()
        {
            Assert.InRange(Downrange(Fly(31000)) - Downrange(Fly()), 10, 12);
        }

        [Fact]
        public void One_hypersonic_drag_area_lands_within_2_km_of_the_full_model_and_the_subsonic_one_far_off()
        {
            var full = Downrange(Fly());
            Assert.InRange(Downrange(Fly(craft: new ReentrySim.Capsule { FixedMach = 8 })) - full, 0, 4);
            Assert.InRange(Downrange(Fly(craft: new ReentrySim.Capsule { FixedMach = 0.5 })) - full, 200, 230);
        }

        [Fact]
        public void A_2_s_step_is_within_50_m_of_a_quarter_second_step_and_5_s_within_100_m()
        {
            var fine = Downrange(Fly(step: 0.25));
            Assert.InRange(Downrange(Fly()) - fine, -0.05, 0.05);
            Assert.InRange(Downrange(Fly(step: 5)) - fine, -0.1, 0.1);
        }

        [Fact]
        public void A_prediction_asks_the_air_a_few_hundred_times()
        {
            var air = new ReentrySim.Atmosphere();
            var craft = new ReentrySim.Capsule();
            ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 0, craft.MassKg, air, craft, ReentrySim.Frame());
            Assert.InRange(air.Calls, 400, 1200);
        }

        [Fact]
        public void Higher_ground_is_reached_sooner_and_short_of_sea_level()
        {
            var craft = new ReentrySim.Capsule();
            var high = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 2000, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            var sea = Fly();
            Assert.True(high.LandingUt < sea.LandingUt);
            Assert.InRange(Downrange(sea) - Downrange(high), 0, 3);
        }

        [Fact]
        public void A_heavier_craft_flies_farther()
        {
            var craft = new ReentrySim.Capsule();
            var heavy = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 0, craft.MassKg * 1.1, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            // Mass is the other half of the ballistic coefficient: as drag x0.9 or so.
            Assert.InRange(ReentrySim.Km(heavy.Longitude) - Downrange(Fly()), 8, 16);
        }

        [Fact]
        public void An_orbit_above_the_atmosphere_never_lands()
        {
            var craft = new ReentrySim.Capsule();
            var high = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(pe: 75000), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Equal("the orbit never reaches Kerbin's atmosphere (periapsis 75000 m)", high.Error);
        }

        [Fact]
        public void An_airless_body_has_no_prediction()
        {
            var craft = new ReentrySim.Capsule();
            var airless = ReentryPredictor.Predict(ReentrySim.Kerbin(0), ReentrySim.AfterBurn(), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Equal("Kerbin has no atmosphere", airless.Error);
        }

        [Fact]
        public void A_craft_already_in_the_atmosphere_is_flown_from_where_it_is()
        {
            var orbit = ReentrySim.AfterBurn();
            var entry = Fly().EntryUt;
            var craft = new ReentrySim.Capsule();
            var inside = ReentryPredictor.Predict(ReentrySim.Kerbin(), orbit, entry + 1, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Null(inside.Error);
            Assert.Equal(entry + 1, inside.EntryUt, 6);
            Assert.InRange(ReentrySim.Km(inside.Longitude) - Downrange(Fly()), -1, 1);
        }

        [Fact]
        public void A_body_turning_the_other_way_in_the_frame_lands_at_the_same_ground_point()
        {
            // Mirror the frame: the orbit's y and the body's turn flip, so the ground track is the same.
            var mirrored = new BodyFrame { LonOffset = 0, Handed = -1, Ut = 0, RotationPeriod = ReentrySim.RotationPeriod };
            var after = ReentrySim.AfterBurn();
            Vec3 r, v;
            after.State(0, out r, out v);
            var flipped = Conic.FromState(ReentrySim.Mu, new Vec3(r.X, -r.Y, r.Z), new Vec3(v.X, -v.Y, v.Z), 0);
            var craft = new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(ReentrySim.Kerbin(), flipped, 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, mirrored);
            Assert.InRange(ReentrySim.Km(landed.Longitude) - Downrange(Fly()), -0.01, 0.01);
        }
    }
}
