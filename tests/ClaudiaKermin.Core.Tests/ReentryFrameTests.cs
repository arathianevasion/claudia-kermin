// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The deorbit_kerbin flight of #326, rebuilt from its KSP.log: DEORBITBURN at the start (UT about 100) landed the burn at
    /// UT 4280 (prograde -52.28, normal -6.75 m/s) on -0.0972, -74.5604 at UT 5055, 0.03 km from KSC; after the burn
    /// REENTRYIMPACT put the same fall on -0.0967, -4.5156 at UT 5056, 733.48 km from KSC. Same latitude, same landing time,
    /// 70.04 degrees of longitude apart: Kerbin's turn between the two calls (69.87 degrees in 4183 s) and 0.17 degrees (2 km)
    /// of burn error. (exec_node flies a node out, so the burn was the planned one; the block's dv_used, its own thrust
    /// integral, reads 50.7 m/s against 52.7, but 2 m/s short would be some 20 km by the research, not 2.) Drag can't move a
    /// landing by the body's turn between two calls; the frame the predictor reads the ground in can.
    /// <para>
    /// Stock (CelestialBody.CBUpdate, decompiled) in its rotating reference frame, below a body's inverse-rotation altitude as
    /// a craft in low orbit is, stops turning the body's BodyFrame and turns Planetarium.Zup instead, by InverseRotAngle. An
    /// orbit's elements give inertial vectors, which stock takes into the game through Zup (Orbit.Init: Zup.WorldToLocal)
    /// before BodyFrame reads latitude and longitude. The adapter's SurfaceUnder skipped Zup, so the frame it gave was off by
    /// InverseRotAngle, which grows with the body's turn. <see cref="StockFrames"/> models that.
    /// </para>
    /// </summary>
    public sealed class ReentryFrameTests
    {
        private const double KscLat = -0.0972;
        private const double KscLng = -74.5577;
        private const double FirstCallUt = 100;
        private const double BurnUt = 4280;
        private const double SecondCallUt = 4283;
        private const double Prograde = -52.28;
        private const double Normal = -6.75;

        /// <summary>
        /// A body as stock places it in the rotating reference frame: its BodyFrame stopped at <see cref="FrozenAngle"/> degrees,
        /// the inertial frame turning under it with the body (InverseRotAngle = the body's rotation angle less that). With
        /// <see cref="ThroughZup"/> a point is taken through Zup first, as stock does; without, it is read as the adapter read it.
        /// </summary>
        private sealed class StockFrames : PlanningBodyBase
        {
            public double FrozenAngle;
            public double InitialRotation = 90;
            public bool ThroughZup;
            public double Now;

            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                var inverseRot = InitialRotation + 360 * Now / ReentrySim.RotationPeriod - FrozenAngle;
                longitude = LandingSiteSearch.Wrap180(Math.Atan2(y, x) * 180 / Math.PI - (ThroughZup ? inverseRot : 0) - FrozenAngle);
                return true;
            }
        }

        /// <summary>The 80 km circular equatorial orbit after the logged burn.</summary>
        private static Conic AfterBurn()
        {
            var r0 = ReentrySim.R + 80000;
            var orbit = Conic.FromState(ReentrySim.Mu, new Vec3(r0, 0, 0), new Vec3(0, Math.Sqrt(ReentrySim.Mu / r0), 0), 0);
            Vec3 r, v;
            orbit.State(BurnUt, out r, out v);
            var up = Vec3.Cross(r, v).Normalized;
            return Conic.FromState(ReentrySim.Mu, r, v + v.Normalized * Prograde + up * Normal, BurnUt);
        }

        /// <summary>The landing of the burn as a block call made at <paramref name="callUt"/> predicts it.</summary>
        private static ReentryPrediction Predict(StockFrames body, double callUt)
        {
            body.Now = callUt;
            string error;
            var frame = BodyFrame.Read(ReentrySim.Kerbin(), body, callUt, out error);
            Assert.Null(error);
            var craft = new ReentrySim.Capsule();
            return ReentryPredictor.Predict(ReentrySim.Kerbin(), AfterBurn(), BurnUt, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
        }

        private static double Km(ReentryPrediction p, double lat, double lng)
        {
            return LandingSiteSearch.ArcDegrees(p.Latitude, p.Longitude, lat, lng) * Math.PI / 180 * ReentrySim.R / 1000;
        }

        /// <summary>The adapter's old reading, with the frozen angle that put the planned landing where the log has it (-74.5604).</summary>
        private static StockFrames AsLogged()
        {
            var planned = Predict(new StockFrames(), FirstCallUt);
            return new StockFrames { FrozenAngle = LandingSiteSearch.Wrap180(planned.Longitude + 74.5604) };
        }

        [Fact]
        public void Read_without_Zup_the_burn_planned_onto_KSC_is_predicted_733_km_from_it_once_flown()
        {
            var body = AsLogged();
            var planned = Predict(body, FirstCallUt);
            var flown = Predict(body, SecondCallUt);
            Assert.InRange(planned.Longitude, -74.5605, -74.5603);
            // The logged second prediction: -4.5156, 733.48 km from KSC, with the same landing time and latitude.
            Assert.InRange(flown.Longitude, -5, -4.4);
            Assert.InRange(Km(flown, KscLat, KscLng), 725, 740);
            Assert.Equal(planned.LandingUt, flown.LandingUt, 6);
            Assert.Equal(planned.Latitude, flown.Latitude, 6);
        }

        [Fact]
        public void The_gap_is_the_bodys_turn_between_the_calls_not_the_fall()
        {
            var body = AsLogged();
            var early = Predict(body, FirstCallUt);
            var late = Predict(body, SecondCallUt);
            // Rotation sign: a sign error would give twice the turn over each fall (165 and 26 degrees), not one turn between calls.
            Assert.Equal(360 * (SecondCallUt - FirstCallUt) / ReentrySim.RotationPeriod, LandingSiteSearch.Wrap180(late.Longitude - early.Longitude), 6);
        }

        [Fact]
        public void Through_Zup_both_calls_agree()
        {
            var body = new StockFrames { FrozenAngle = AsLogged().FrozenAngle, ThroughZup = true };
            var planned = Predict(body, FirstCallUt);
            var flown = Predict(body, SecondCallUt);
            Assert.Equal(planned.Longitude, flown.Longitude, 6);
            Assert.Equal(planned.Latitude, flown.Latitude, 6);
        }
    }
}
