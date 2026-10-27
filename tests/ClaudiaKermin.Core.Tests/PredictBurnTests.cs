// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>predict_burn</c> through the Planning Tools entry point, on hand-built Game Snapshots with the stock Kerbin system's
    /// constants, checked against vis-viva worked by hand and against the patch transitions the geometry forces.
    /// </summary>
    public sealed class PredictBurnTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double MunSma = 12000000;
        private const double KerbinSoi = 84159286.48;
        private const double G0 = 9.80665;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>The craft round <paramref name="body"/> in a circle <paramref name="altitude"/> up (or an ellipse to <paramref name="apAltitude"/>), equatorial, at angle <paramref name="phase"/> at UT 0; the Mun at <paramref name="munPhase"/>.</summary>
        private static GameSnapshot Snapshot(string body = "Kerbin", double altitude = 100000, double? apAltitude = null, double phase = 0, double munPhase = Math.PI)
        {
            var radius = body == "Mun" ? MunR : KerbinR;
            var rp = radius + altitude;
            var ra = radius + (apAltitude ?? altitude);
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = body };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = phase, Epoch = 0, Body = body, Periapsis = altitude, Apoapsis = apAltitude ?? altitude,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = KerbinSoi,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = MunSma,
                MeanAnomalyAtEpoch = munPhase,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
            });
            return snapshot;
        }

        private JsonElement Run(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        private static string Burn(string at, double prograde, double normal = 0, double radial = 0)
        {
            return "{\"at\":" + at + ",\"dv\":{\"prograde\":" + N(prograde) + ",\"normal\":" + N(normal) + ",\"radial\":" + N(radial) + "}}";
        }

        private static string N(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static JsonElement[] Patches(JsonElement result)
        {
            return result.GetProperty("patches").EnumerateArray().ToArray();
        }

        /// <summary>Vis-viva: the semi-major axis from radius and speed.</summary>
        private static double Sma(double gm, double r, double v)
        {
            return 1 / (2 / r - v * v / gm);
        }

        // ---- Vis-viva ----

        [Fact]
        public void A_prograde_burn_from_a_circular_orbit_raises_the_far_side_to_the_vis_viva_apoapsis()
        {
            var result = Run(Snapshot(), "{\"burns\":[" + Burn("\"in 60\"", 100) + "]}");
            var r = KerbinR + 100000;
            var a = Sma(KerbinGm, r, Math.Sqrt(KerbinGm / r) + 100);
            var patch = Patches(result).Single();
            Assert.Equal("Kerbin", patch.GetProperty("body").GetString());
            Assert.Equal(2 * a - r - KerbinR, patch.GetProperty("ap").GetDouble(), 0);
            Assert.Equal(100000, patch.GetProperty("pe").GetDouble(), 0);
            Assert.Equal("stable", patch.GetProperty("ends").GetString());
            Assert.Equal(60, patch.GetProperty("start_ut").GetDouble(), 1);
            Assert.Equal("in 60 s", patch.GetProperty("start_in").GetString());
            // A stable patch ends one period later, as stock's does.
            Assert.Equal(60 + 2 * Math.PI * Math.Sqrt(a * a * a / KerbinGm), patch.GetProperty("end_ut").GetDouble(), 0);
            Assert.Contains("impulsive", result.GetProperty("model").GetString());
        }

        [Fact]
        public void A_burn_at_apoapsis_of_the_circularizing_speed_leaves_a_circle_at_the_apoapsis()
        {
            var snapshot = Snapshot(altitude: 100000, apAltitude: 500000);
            double rp = KerbinR + 100000, ra = KerbinR + 500000, a = (rp + ra) / 2;
            var vAp = Math.Sqrt(KerbinGm * (2 / ra - 1 / a));
            var result = Run(snapshot, "{\"burns\":[" + Burn("\"ap\"", Math.Sqrt(KerbinGm / ra) - vAp) + "]}");
            var patch = Patches(result).Single();
            Assert.Equal(500000, patch.GetProperty("pe").GetDouble(), 0);
            Assert.Equal(500000, patch.GetProperty("ap").GetDouble(), 0);
            Assert.True(patch.GetProperty("eccentricity").GetDouble() < 1e-4);
            // From periapsis at UT 0, apoapsis is half a period on.
            var burn = result.GetProperty("burns")[0];
            Assert.Equal(Math.PI * Math.Sqrt(a * a * a / KerbinGm), burn.GetProperty("at_ut").GetDouble(), 0);
        }

        [Fact]
        public void A_normal_burn_on_an_equatorial_circle_tilts_it_by_the_angle_of_the_burn_to_the_velocity()
        {
            var v = Math.Sqrt(KerbinGm / (KerbinR + 100000));
            var result = Run(Snapshot(), "{\"burns\":[" + Burn("10", 0, 200) + "]}");
            Assert.Equal(Math.Atan(200 / v) * 180 / Math.PI, Patches(result)[0].GetProperty("inclination").GetDouble(), 2);
        }

        [Fact]
        public void Two_burns_hohmann_style_raise_then_circularize_and_the_second_is_at_the_first_burns_apoapsis()
        {
            double r1 = KerbinR + 100000, r2 = KerbinR + 400000, at = (r1 + r2) / 2;
            var first = Math.Sqrt(KerbinGm * (2 / r1 - 1 / at)) - Math.Sqrt(KerbinGm / r1);
            var second = Math.Sqrt(KerbinGm / r2) - Math.Sqrt(KerbinGm * (2 / r2 - 1 / at));
            var result = Run(Snapshot(), "{\"burns\":[" + Burn("\"in 100\"", first) + "," + Burn("\"ap\"", second) + "]}");
            var burns = result.GetProperty("burns").EnumerateArray().ToArray();
            Assert.Equal(400000, burns[0].GetProperty("ap").GetDouble(), 0);
            Assert.Equal(100 + Math.PI * Math.Sqrt(at * at * at / KerbinGm), burns[1].GetProperty("at_ut").GetDouble(), 0);
            var patch = Patches(result).Single();
            Assert.Equal(400000, patch.GetProperty("pe").GetDouble(), 0);
            Assert.Equal(400000, patch.GetProperty("ap").GetDouble(), 0);
        }

        // ---- Patch ends ----

        [Fact]
        public void A_burn_past_escape_speed_ends_in_an_escape_at_the_soi_edge_and_the_next_patch_is_round_the_sun()
        {
            // The Mun is a quarter turn behind the craft, out of the way of the escape.
            var result = Run(Snapshot(munPhase: -Math.PI / 2), "{\"burns\":[" + Burn("0", 1000) + "]}");
            var patches = Patches(result);
            Assert.Equal("escape", patches[0].GetProperty("ends").GetString());
            Assert.Equal(JsonValueKind.Null, patches[0].GetProperty("ap").ValueKind);
            Assert.Equal(100000, patches[0].GetProperty("pe").GetDouble(), 0);
            Assert.True(patches[0].GetProperty("eccentricity").GetDouble() > 1);
            Assert.Equal("Sun", patches[1].GetProperty("body").GetString());
            Assert.Equal(patches[0].GetProperty("end_ut").GetDouble(), patches[1].GetProperty("start_ut").GetDouble(), 1);

            // The hyperbola's own vis-viva time to the SOI edge.
            double r = KerbinR + 100000, v = Math.Sqrt(KerbinGm / r) + 1000;
            var a = Sma(KerbinGm, r, v);
            var e = r * v * v / KerbinGm - 1;
            var nu = Math.Acos((a * (1 - e * e) / KerbinSoi - 1) / e);
            var h = 2 * Math.Atanh(Math.Sqrt((e - 1) / (e + 1)) * Math.Tan(nu / 2));
            var t = (e * Math.Sinh(h) - h) / Math.Sqrt(KerbinGm / -(a * a * a));
            Assert.Equal(t, patches[0].GetProperty("end_ut").GetDouble(), 0);
        }

        [Fact]
        public void A_hohmann_burn_timed_for_the_mun_meets_it_and_the_next_patch_is_round_the_mun()
        {
            double r1 = KerbinR + 100000, at = (r1 + MunSma) / 2;
            var dv = Math.Sqrt(KerbinGm * (2 / r1 - 1 / at)) - Math.Sqrt(KerbinGm / r1);
            var transfer = Math.PI * Math.Sqrt(at * at * at / KerbinGm);
            var craftRate = Math.Sqrt(KerbinGm / (r1 * r1 * r1));
            var munRate = Math.Sqrt(KerbinGm / (MunSma * MunSma * MunSma));
            // At the burn (UT 60) the craft is at angle 0; the Mun is where it will be half a transfer orbit later, less a little so it isn't hit dead on.
            var munPhase = Math.PI - munRate * transfer - munRate * 60 + 0.02;
            var snapshot = Snapshot(phase: -craftRate * 60, munPhase: munPhase);
            var result = Run(snapshot, "{\"burns\":[" + Burn("\"in 60\"", dv) + "],\"target\":\"Mun\"}");
            var patches = Patches(result);
            Assert.Equal("encounter Mun", patches[0].GetProperty("ends").GetString());
            Assert.Equal("Mun", patches[1].GetProperty("body").GetString());
            // The craft meets the Mun's SOI before it reaches the transfer orbit's apoapsis.
            Assert.True(patches[0].GetProperty("end_ut").GetDouble() < 60 + transfer);
            var closest = result.GetProperty("closest_approach");
            Assert.Equal("Mun", closest.GetProperty("body").GetString());
            Assert.True(closest.GetProperty("distance").GetDouble() < 2429559.1);
            Assert.True(closest.GetProperty("at_ut").GetDouble() >= patches[1].GetProperty("start_ut").GetDouble());
        }

        [Fact]
        public void Without_the_mun_in_the_way_the_closest_approach_is_the_nearest_point_of_the_kerbin_orbit()
        {
            // A 240 km apoapsis never comes near the Mun, which starts opposite the craft: the closest approach is near the
            // apoapsis, a little over the Mun's orbit radius less the apoapsis radius.
            var result = Run(Snapshot(), "{\"burns\":[" + Burn("0", 100) + "],\"target\":\"Mun\"}");
            var closest = result.GetProperty("closest_approach");
            var apR = KerbinR + Patches(result)[0].GetProperty("ap").GetDouble();
            Assert.InRange(closest.GetProperty("distance").GetDouble(), MunSma - apR - 1, MunSma - apR + 1000);
            Assert.Equal(closest.GetProperty("distance").GetDouble() - MunR, closest.GetProperty("altitude").GetDouble(), 0);
            // Apoapsis is half an orbit on.
            Assert.InRange(closest.GetProperty("at_ut").GetDouble() - Patches(result)[0].GetProperty("end_ut").GetDouble() / 2, -60, 60);
        }

        [Fact]
        public void A_retrograde_burn_low_over_the_airless_mun_ends_in_an_impact_where_the_orbit_meets_the_surface()
        {
            var result = Run(Snapshot("Mun", 30000), "{\"burns\":[" + Burn("0", -100) + "]}");
            var patch = Patches(result).Single();
            Assert.Equal("Mun", patch.GetProperty("body").GetString());
            Assert.Equal("impact", patch.GetProperty("ends").GetString());
            Assert.True(patch.GetProperty("pe").GetDouble() < 0);
            // From apoapsis (the burn point) down to the surface radius takes less than half the orbit.
            double r = MunR + 30000, a = Sma(MunGm, r, Math.Sqrt(MunGm / r) - 100);
            var end = patch.GetProperty("end_ut").GetDouble();
            Assert.True(end > 0 && end < Math.PI * Math.Sqrt(a * a * a / MunGm));
            var e = r / a - 1;
            var nu = Math.Acos((a * (1 - e * e) / MunR - 1) / e);
            var ea = 2 * Math.Atan(Math.Sqrt((1 - e) / (1 + e)) * Math.Tan(nu / 2));
            var fromPe = (ea - e * Math.Sin(ea)) / Math.Sqrt(MunGm / (a * a * a));
            Assert.Equal(Math.PI * Math.Sqrt(a * a * a / MunGm) - fromPe, end, 0);
        }

        [Fact]
        public void A_retrograde_burn_over_kerbin_ends_at_the_top_of_the_atmosphere_and_predicts_nothing_after()
        {
            var result = Run(Snapshot(), "{\"burns\":[" + Burn("0", -100) + "]}");
            var patch = Patches(result).Single();
            Assert.Equal("atmosphere", patch.GetProperty("ends").GetString());
            double r = KerbinR + 100000, a = Sma(KerbinGm, r, Math.Sqrt(KerbinGm / r) - 100);
            var e = r / a - 1;
            var nu = Math.Acos((a * (1 - e * e) / (KerbinR + 70000) - 1) / e);
            var ea = 2 * Math.Atan(Math.Sqrt((1 - e) / (1 + e)) * Math.Tan(nu / 2));
            var fromPe = (ea - e * Math.Sin(ea)) / Math.Sqrt(KerbinGm / (a * a * a));
            Assert.Equal(Math.PI * Math.Sqrt(a * a * a / KerbinGm) - fromPe, patch.GetProperty("end_ut").GetDouble(), 0);
        }

        [Fact]
        public void An_atmosphere_patch_end_uses_the_bodys_own_atmosphere_height()
        {
            // The same burn round a Kerbin given Duna's 50 km atmosphere ends lower down.
            var snapshot = Snapshot();
            snapshot.Bodies.Single(b => b.Name == "Kerbin").AtmosphereHeight = 50000;
            var high = Run(Snapshot(), "{\"burns\":[" + Burn("0", -100) + "]}");
            var low = Run(snapshot, "{\"burns\":[" + Burn("0", -100) + "]}");
            Assert.Equal("atmosphere", Patches(low)[0].GetProperty("ends").GetString());
            Assert.True(Patches(low)[0].GetProperty("end_ut").GetDouble() > Patches(high)[0].GetProperty("end_ut").GetDouble());
        }

        // ---- Burn time ----

        [Fact]
        public void Each_burn_gets_a_rocket_equation_burn_time_from_the_stages_and_a_long_burn_is_flagged()
        {
            var snapshot = Snapshot();
            snapshot.DeltaVSimulator = true;
            snapshot.Stages.Add(new SnapshotStage { Number = 0, StartMassT = 10, EndMassT = 5, ThrustVacKn = 100, DeltaVVac = 300 * G0 * Math.Log(2) });
            var result = Run(snapshot, "{\"burns\":[" + Burn("0", 100) + "," + Burn("\"ap\"", 1500) + "]}");
            var burns = result.GetProperty("burns").EnumerateArray().ToArray();
            var exhaust = 300 * G0;
            var m1 = 10000 / Math.Exp(100 / exhaust);
            Assert.Equal((10000 - m1) * exhaust / 100000, burns[0].GetProperty("burn_time").GetDouble(), 1);
            Assert.False(burns[0].TryGetProperty("long_burn", out _));
            // The second burn starts from the mass the first left.
            var m2 = m1 / Math.Exp(1500 / exhaust);
            Assert.Equal((m1 - m2) * exhaust / 100000, burns[1].GetProperty("burn_time").GetDouble(), 1);
            Assert.True(burns[1].GetProperty("long_burn").GetBoolean());
            Assert.Contains("less accurate", result.GetProperty("note").GetString());
        }

        [Fact]
        public void Without_stage_figures_the_burn_time_is_null_with_a_reason()
        {
            var burn = Run(Snapshot(), "{\"burns\":[" + Burn("0", 100) + "]}").GetProperty("burns")[0];
            Assert.Equal(JsonValueKind.Null, burn.GetProperty("burn_time").ValueKind);
            Assert.False(string.IsNullOrEmpty(burn.GetProperty("burn_time_note").GetString()));
        }

        // ---- Only reads ----

        [Fact]
        public void The_snapshot_orbit_is_copied_never_changed()
        {
            var snapshot = Snapshot();
            var before = snapshot.ToJson();
            Run(snapshot, "{\"burns\":[" + Burn("0", 500) + "," + Burn("\"ap\"", 200) + "],\"target\":\"Mun\"}");
            Assert.Equal(before, snapshot.ToJson());
        }

        // ---- Errors ----

        [Fact]
        public void Bad_inputs_are_plain_errors()
        {
            Assert.Contains("there is no body named \"Eeloo\"", Error(Snapshot(), "{\"burns\":[" + Burn("0", 1) + "],\"target\":\"Eeloo\"}"));
            Assert.Equal("burn 1.at must be \"ap\", \"pe\", a UT in seconds, or \"in <s>\"", Error(Snapshot(), "{\"burns\":[" + Burn("\"soon\"", 1) + "]}"));
            Assert.Equal("burn 2 is at UT 5, before the burn before it", Error(Snapshot(), "{\"burns\":[" + Burn("10", 1) + "," + Burn("5", 1) + "]}"));
            Assert.Contains("an escape with no apoapsis", Error(Snapshot(munPhase: -Math.PI / 2), "{\"burns\":[" + Burn("0", 1000) + "," + Burn("\"ap\"", 1) + "]}"));
            Assert.Contains("is after the craft reaches Kerbin's atmosphere", Error(Snapshot(), "{\"burns\":[" + Burn("0", -100) + "," + Burn("\"in 100000\"", 1) + "]}"));
            Assert.Equal("input.burns takes at most 3 items", Error(Snapshot(), "{\"burns\":[" + string.Join(",", Enumerable.Repeat(Burn("0", 1), 4)) + "]}"));

            var landed = Snapshot();
            landed.Situation = "LANDED";
            Assert.Contains("not in flight", Error(landed, "{\"burns\":[" + Burn("0", 1) + "]}"));
            var low = Snapshot(altitude: 50000);
            Assert.Contains("inside Kerbin's atmosphere", Error(low, "{\"burns\":[" + Burn("0", 1) + "]}"));
        }
    }
}
