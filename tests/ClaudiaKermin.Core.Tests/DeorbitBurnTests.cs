// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The deorbit burn search with the research's capsule and atmosphere (<see cref="ReentrySim"/>), from an 80 km circular
    /// equatorial Kerbin orbit that crosses longitude 0 at UT 0. A burn it chooses is flown again through the predictor alone,
    /// so what it reports is checked against an independent prediction.
    /// </summary>
    public sealed class DeorbitBurnTests
    {
        private const double KscLat = -0.0972;
        private const double KscLng = -74.5577;

        private static Conic Circular(double alt = 80000, double inclinationDeg = 0)
        {
            var r = ReentrySim.R + alt;
            var v = Math.Sqrt(ReentrySim.Mu / r);
            var i = inclinationDeg * Math.PI / 180;
            return Conic.FromState(ReentrySim.Mu, new Vec3(r, 0, 0), new Vec3(0, v * Math.Cos(i), v * Math.Sin(i)), 0);
        }

        private static DeorbitPlan Find(double lat, double lng, Conic orbit = null, double maxWait = ReentrySim.RotationPeriod, double atmosphere = ReentrySim.AtmTop)
        {
            var ask = new DeorbitAsk { TargetLatitude = lat, TargetLongitude = lng, EarliestUt = 120, MaxWait = maxWait, AccuracyKm = 10 };
            return DeorbitBurn.Find(ReentrySim.Kerbin(atmosphere), orbit ?? Circular(), ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame());
        }

        /// <summary>The plan's burn applied to the orbit and flown by the predictor alone.</summary>
        private static ReentryPrediction FlyAgain(Conic orbit, DeorbitPlan plan, out Conic after)
        {
            Vec3 r, v;
            orbit.State(plan.BurnUt, out r, out v);
            var up = Vec3.Cross(r, v).Normalized;
            after = Conic.FromState(ReentrySim.Mu, r, v + v.Normalized * plan.Prograde + up * plan.Normal, plan.BurnUt);
            var craft = new ReentrySim.Capsule();
            return ReentryPredictor.Predict(ReentrySim.Kerbin(), after, plan.BurnUt, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
        }

        private static double Km(double lat1, double lng1, double lat2, double lng2)
        {
            return LandingSiteSearch.ArcDegrees(lat1, lng1, lat2, lng2) * Math.PI / 180 * ReentrySim.R / 1000;
        }

        [Fact]
        public void It_finds_a_retrograde_burn_that_lands_on_KSC_within_a_rotation()
        {
            var orbit = Circular();
            var plan = Find(KscLat, KscLng);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            Assert.InRange(plan.BurnUt, 120, 120 + ReentrySim.RotationPeriod);
            Assert.True(plan.Prograde < 0);
            Assert.InRange(Math.Abs(plan.Normal), 0, 5);
            Assert.Equal(Math.Sqrt(plan.Prograde * plan.Prograde + plan.Normal * plan.Normal), plan.DeltaV, 6);

            Conic after;
            var again = FlyAgain(orbit, plan, out after);
            Assert.InRange(Km(again.Latitude, again.Longitude, plan.Landing.Latitude, plan.Landing.Longitude), 0, 0.01);
            Assert.InRange(Km(again.Latitude, again.Longitude, KscLat, KscLng), 0, 0.5);
            // The steep entry the research recommends: periapsis at 30% of the atmosphere, 21 km at Kerbin.
            Assert.InRange(after.PeR - ReentrySim.R, 20900, 21100);
        }

        [Fact]
        public void A_target_beside_the_ground_track_is_reached_with_a_normal_part()
        {
            var plan = Find(1, KscLng);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            // North of a prograde equatorial orbit: along its angular momentum.
            Assert.InRange(plan.Normal, 1, DeorbitBurn.MaxNormal);
        }

        [Fact]
        public void An_inclined_orbit_lands_on_its_target_too()
        {
            var plan = Find(KscLat, KscLng, Circular(inclinationDeg: 6));
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
        }

        [Fact]
        public void A_target_too_far_off_the_ground_track_fails_rather_than_burns_and_says_where_the_best_lands()
        {
            var plan = Find(20, KscLng);
            Assert.Contains("over accuracy_km 10", plan.Error);
            Assert.Contains("too far off the ground track", plan.Error);
            Assert.InRange(plan.ErrorKm, 100, 2500);
            Assert.NotNull(plan.Landing);
            Assert.Null(plan.Landing.Error);
        }

        [Fact]
        public void A_short_max_wait_fails_when_no_burn_in_it_reaches_the_target()
        {
            // Burns in the first 10 minutes land between about 125 and 230 degrees east; 270 (-90) is out of reach.
            var plan = Find(0, -90, maxWait: 600);
            Assert.Contains("the best burn in the next 0.2 h lands", plan.Error);
            Assert.Contains("allow a longer max_wait", plan.Error);
            Assert.InRange(plan.BurnUt, 120, 720);
        }

        [Fact]
        public void An_airless_body_fails_with_body_has_no_atmosphere()
        {
            Assert.Equal("body has no atmosphere", Find(KscLat, KscLng, atmosphere: 0).Error);
        }

        [Fact]
        public void An_escape_orbit_fails()
        {
            var r = ReentrySim.R + 80000;
            var escape = Conic.FromState(ReentrySim.Mu, new Vec3(r, 0, 0), new Vec3(0, 1.5 * Math.Sqrt(2 * ReentrySim.Mu / r), 0), 0);
            Assert.Equal("the orbit escapes Kerbin: deorbit burns from a closed orbit, so capture first", Find(KscLat, KscLng, escape).Error);
        }

        [Fact]
        public void A_periapsis_already_in_the_atmosphere_fails_and_says_to_fly_reentry()
        {
            Assert.Equal("the periapsis (30000 m) is already inside Kerbin's atmosphere: fly reentry", Find(KscLat, KscLng, ReentrySim.AfterBurn()).Error);
        }

        [Fact]
        public void The_engines_isp_lightens_the_craft_after_the_burn_and_the_search_allows_for_it()
        {
            var orbit = Circular();
            var ask = new DeorbitAsk { TargetLatitude = KscLat, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod, Isp = 300 };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), orbit, ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), ReentrySim.Frame());
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            // Flown at the full mass, the same burn goes long: a lighter craft slows sooner.
            Conic after;
            var heavy = FlyAgain(orbit, plan, out after);
            Assert.InRange(Km(heavy.Latitude, heavy.Longitude, KscLat, KscLng), 0.5, 20);
        }
    }
}
