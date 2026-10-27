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
        public void In_a_mirrored_frame_the_normal_part_keeps_its_real_sign()
        {
            // The same orbit and target seen in a frame with y flipped: the node's normal is the real angular momentum, so its sign holds.
            var plain = Find(1, KscLng);
            Vec3 r, v;
            Circular().State(0, out r, out v);
            var flipped = Conic.FromState(ReentrySim.Mu, new Vec3(r.X, -r.Y, r.Z), new Vec3(v.X, -v.Y, v.Z), 0);
            var mirrored = new BodyFrame { LonOffset = 0, Handed = -1, Ut = 0, RotationPeriod = ReentrySim.RotationPeriod };
            var ask = new DeorbitAsk { TargetLatitude = 1, TargetLongitude = KscLng, EarliestUt = 120, MaxWait = ReentrySim.RotationPeriod };
            var plan = DeorbitBurn.Find(ReentrySim.Kerbin(), flipped, ask, new ReentrySim.Atmosphere(), new ReentrySim.Capsule(), mirrored);
            Assert.Null(plan.Error);
            Assert.InRange(plan.ErrorKm, 0, 0.5);
            Assert.InRange(plan.Normal, plain.Normal - 0.5, plain.Normal + 0.5);
            Assert.InRange(plan.BurnUt, plain.BurnUt - 1, plain.BurnUt + 1);
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

        // ---- the block-internal calls: ADDONS:CLAUDIA:DEORBITBURN and REENTRYIMPACT ----

        /// <summary>A sphere whose surface frame is the orbit frame at the call (x under longitude 0, z north).</summary>
        private sealed class Sphere : PlanningBodyBase
        {
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        /// <summary>The 80 km circular equatorial orbit as a Game Snapshot at UT 0, round Kerbin (or the airless Mun).</summary>
        private static Missions.GameSnapshot Snapshot(string body = "Kerbin", double pe = 80000)
        {
            var kerbin = ReentrySim.Kerbin();
            var radius = body == "Kerbin" ? ReentrySim.R : 200000;
            var snapshot = new Missions.GameSnapshot { Ut = 0, Situation = "ORBITING", Body = body };
            var sma = (2 * radius + 80000 + pe) / 2;
            snapshot.Orbit = new Missions.SnapshotOrbit
            {
                SemiMajorAxis = sma, Eccentricity = (radius + 80000 - (radius + pe)) / (2 * sma), Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 180,
                MeanAnomalyAtEpoch = Math.PI, Epoch = 0, Body = body, Periapsis = pe, Apoapsis = 80000,
            };
            snapshot.Bodies.Add(new Missions.SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(kerbin);
            snapshot.Bodies.Add(new Missions.SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000 });
            return snapshot;
        }

        private static System.Collections.Generic.Dictionary<string, object> Ask(params object[] pairs)
        {
            var ask = new System.Collections.Generic.Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) ask[(string)pairs[i]] = pairs[i + 1];
            return ask;
        }

        [Fact]
        public void The_block_call_gives_the_burn_as_node_components_and_its_landing()
        {
            var found = BlockCalls.DeorbitBurn(Ask("lat", KscLat, "lng", KscLng, "earliest_ut", 120.0, "max_wait", 21549.425, "ground_alt", 0.0, "accuracy_km", 10.0),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", found["error"]);
            Assert.True((double)found["prograde"] < 0);
            Assert.Equal(0.0, found["radial"]);
            Assert.InRange((double)found["burn_ut"], 120, 120 + 21549.425);
            Assert.InRange((double)found["error_km"], 0, 0.5);
            Assert.InRange((double)found["lat"], KscLat - 0.01, KscLat + 0.01);
            Assert.InRange((double)found["lng"], KscLng - 0.01, KscLng + 0.01);
            Assert.True((double)found["landing_ut"] > (double)found["burn_ut"]);
            Assert.InRange((double)found["dv"], 30, 150);
            // Every value is a double or a string, as KosValues turns into kOS values.
            foreach (var value in found.Values) Assert.True(value is double || value is string, value.GetType().Name);
        }

        [Fact]
        public void The_block_call_over_accuracy_gives_the_reason_and_the_best_landing()
        {
            var found = BlockCalls.DeorbitBurn(Ask("lat", 20.0, "lng", KscLng, "earliest_ut", 120.0), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Contains("over accuracy_km 10", (string)found["error"]);
            Assert.True((double)found["error_km"] > 100);
            Assert.True(found.ContainsKey("lat"));
        }

        [Fact]
        public void The_block_calls_on_an_airless_body_say_body_has_no_atmosphere()
        {
            var deorbit = BlockCalls.DeorbitBurn(Ask("lat", 0.0, "lng", 0.0), Snapshot("Mun"), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("body has no atmosphere", deorbit["error"]);
            Assert.Single(deorbit);
            var impact = BlockCalls.ReentryImpact(Ask(), Snapshot("Mun"), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("body has no atmosphere", impact["error"]);
        }

        [Fact]
        public void The_impact_call_predicts_the_orbit_as_it_is_and_the_distance_to_a_target()
        {
            // The research's burn, already flown: 80 km by 30 km, from the apoapsis on the x axis.
            var flown = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", flown["error"]);
            Assert.InRange(ReentrySim.Km((double)flown["lng"]), 1095, 1099);
            Assert.InRange((double)flown["error_km"], 1090, 1100);
            var noTarget = BlockCalls.ReentryImpact(Ask(), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal(-1.0, noTarget["error_km"]);
        }

        [Fact]
        public void The_impact_call_given_an_orbit_predicts_that_one_not_the_crafts()
        {
            // The craft sits in a safe 80 km orbit (#283): return asks for the path it has planned, kOS's elements in degrees.
            var sma = (2 * ReentrySim.R + 80000 + 30000) / 2;
            var ecc = (ReentrySim.R + 80000 - (ReentrySim.R + 30000)) / (2 * sma);
            var planned = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0, "orbit_sma", sma, "orbit_ecc", ecc, "orbit_inc", 0.0,
                "orbit_lan", 0.0, "orbit_argpe", 180.0, "orbit_mna", 180.0, "orbit_epoch", 0.0, "orbit_body", "Kerbin"),
                Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            var asFlown = BlockCalls.ReentryImpact(Ask("lat", 0.0, "lng", 0.0, "ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", planned["error"]);
            Assert.Equal((double)asFlown["lng"], (double)planned["lng"], 6);
            Assert.Equal((double)asFlown["error_km"], (double)planned["error_km"], 6);
        }

        [Fact]
        public void The_impact_call_falls_from_from_ut_not_from_an_earlier_pass_of_the_same_orbit()
        {
            // The 80 km by 30 km orbit is at apoapsis at UT 0 and 1800ish: a path that begins then falls on its next pass.
            var now = BlockCalls.ReentryImpact(Ask("ground_alt", 0.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            var later = BlockCalls.ReentryImpact(Ask("ground_alt", 0.0, "from_ut", 2500.0), Snapshot(pe: 30000), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", later["error"]);
            Assert.True((double)later["landing_ut"] > 2500);
            Assert.True((double)now["landing_ut"] < 2500);
        }

        [Fact]
        public void The_impact_call_given_an_orbit_round_a_body_with_no_air_says_so()
        {
            var mun = BlockCalls.ReentryImpact(Ask("orbit_sma", 230000.0, "orbit_ecc", 0.0, "orbit_body", "Mun"), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("body has no atmosphere", mun["error"]);
        }

        [Fact]
        public void The_impact_call_on_an_orbit_above_the_air_says_so()
        {
            var high = BlockCalls.ReentryImpact(Ask(), Snapshot(), new Sphere(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("the orbit never reaches Kerbin's atmosphere (periapsis 80000 m)", high["error"]);
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
