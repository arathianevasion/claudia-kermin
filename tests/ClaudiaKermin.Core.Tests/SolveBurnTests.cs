// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>solve_burn</c> through the Planning Tools entry point on hand-built Game Snapshots with the stock Kerbin system's
    /// constants: the closed-form goals against vis-viva worked by hand, and next_patch_pe against the predict_burn answer for
    /// the burn it returns.
    /// </summary>
    public sealed class SolveBurnTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double MunSma = 12000000;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>The craft round Kerbin between <paramref name="altitude"/> and <paramref name="apAltitude"/>, at its periapsis at UT 0 unless <paramref name="phase"/> says otherwise.</summary>
        private static GameSnapshot Snapshot(double altitude = 100000, double? apAltitude = null, double phase = 0, double munPhase = Math.PI)
        {
            var rp = KerbinR + altitude;
            var ra = KerbinR + (apAltitude ?? altitude);
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = phase, Epoch = 0, Body = "Kerbin", Periapsis = altitude, Apoapsis = apAltitude ?? altitude,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = MunSma,
                MeanAnomalyAtEpoch = munPhase,
            });
            return snapshot;
        }

        /// <summary>A circular 100 km orbit with the Mun placed so a Hohmann burn at UT 60 meets it, a little off dead centre.</summary>
        private static GameSnapshot MunBound()
        {
            double r1 = KerbinR + 100000, at = (r1 + MunSma) / 2;
            var transfer = Math.PI * Math.Sqrt(at * at * at / KerbinGm);
            var craftRate = Math.Sqrt(KerbinGm / (r1 * r1 * r1));
            var munRate = Math.Sqrt(KerbinGm / (MunSma * MunSma * MunSma));
            return Snapshot(phase: -craftRate * 60, munPhase: Math.PI - munRate * transfer - munRate * 60 + 0.02);
        }

        private JsonElement Run(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("solve_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("solve_burn", input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        /// <summary>Vis-viva speed at radius <paramref name="r"/> on an orbit of semi-major axis <paramref name="a"/>.</summary>
        private static double Speed(double r, double a)
        {
            return Math.Sqrt(KerbinGm * (2 / r - 1 / a));
        }

        private static JsonElement Predicted(JsonElement result)
        {
            return result.GetProperty("predict_burn");
        }

        // ---- Closed-form goals ----

        [Fact]
        public void Circularizing_at_the_apoapsis_burns_the_vis_viva_difference_and_leaves_a_circle_there()
        {
            double rp = KerbinR + 100000, ra = KerbinR + 500000;
            var result = Run(Snapshot(100000, 500000), "{\"goal\":\"circularize\",\"at\":\"ap\"}");
            var expected = Math.Sqrt(KerbinGm / ra) - Speed(ra, (rp + ra) / 2);
            Assert.Equal(expected, result.GetProperty("dv").GetProperty("prograde").GetDouble(), 2);
            Assert.Equal(0, result.GetProperty("dv").GetProperty("normal").GetDouble());
            Assert.Equal(0, result.GetProperty("dv").GetProperty("radial").GetDouble());
            Assert.Equal(expected, result.GetProperty("magnitude").GetDouble(), 2);
            var patch = Predicted(result).GetProperty("patches")[0];
            Assert.Equal(500000, patch.GetProperty("pe").GetDouble(), 0);
            Assert.Equal(500000, patch.GetProperty("ap").GetDouble(), 0);
            // From periapsis at UT 0, apoapsis is half a period on.
            var a = (rp + ra) / 2;
            Assert.Equal(Math.PI * Math.Sqrt(a * a * a / KerbinGm), result.GetProperty("at_ut").GetDouble(), 0);
        }

        [Fact]
        public void Circularizing_at_the_periapsis_burns_retrograde_to_a_circle_at_the_periapsis()
        {
            double rp = KerbinR + 100000, ra = KerbinR + 500000;
            var result = Run(Snapshot(100000, 500000), "{\"goal\":\"circularize\",\"at\":\"pe\"}");
            Assert.Equal(Math.Sqrt(KerbinGm / rp) - Speed(rp, (rp + ra) / 2), result.GetProperty("dv").GetProperty("prograde").GetDouble(), 2);
            Assert.True(result.GetProperty("dv").GetProperty("prograde").GetDouble() < 0);
            var patch = Predicted(result).GetProperty("patches")[0];
            Assert.Equal(100000, patch.GetProperty("ap").GetDouble(), 0);
            Assert.Equal(100000, patch.GetProperty("pe").GetDouble(), 0);
        }

        [Fact]
        public void Set_ap_burns_at_the_periapsis_to_the_vis_viva_speed_for_the_new_apoapsis()
        {
            double rp = KerbinR + 100000, ra = KerbinR + 700000;
            var result = Run(Snapshot(), "{\"goal\":\"set_ap\",\"value\":700000}");
            Assert.Equal(Speed(rp, (rp + ra) / 2) - Math.Sqrt(KerbinGm / rp), result.GetProperty("dv").GetProperty("prograde").GetDouble(), 2);
            var patch = Predicted(result).GetProperty("patches")[0];
            Assert.Equal(700000, patch.GetProperty("ap").GetDouble(), 0);
            Assert.Equal(100000, patch.GetProperty("pe").GetDouble(), 0);
            Assert.Equal("stable", patch.GetProperty("ends").GetString());
        }

        [Fact]
        public void Set_pe_burns_at_the_apoapsis_to_the_vis_viva_speed_for_the_new_periapsis()
        {
            double rp = KerbinR + 100000, ra = KerbinR + 500000, newPe = KerbinR + 200000, a = (rp + ra) / 2;
            var result = Run(Snapshot(100000, 500000), "{\"goal\":\"set_pe\",\"value\":200000}");
            Assert.Equal(Speed(ra, (ra + newPe) / 2) - Speed(ra, a), result.GetProperty("dv").GetProperty("prograde").GetDouble(), 2);
            Assert.True(result.GetProperty("dv").GetProperty("prograde").GetDouble() > 0);
            var patch = Predicted(result).GetProperty("patches")[0];
            Assert.Equal(200000, patch.GetProperty("pe").GetDouble(), 0);
            Assert.Equal(500000, patch.GetProperty("ap").GetDouble(), 0);
        }

        [Fact]
        public void Lowering_the_periapsis_is_a_retrograde_burn()
        {
            var result = Run(Snapshot(100000, 500000), "{\"goal\":\"set_pe\",\"value\":50000}");
            Assert.True(result.GetProperty("dv").GetProperty("prograde").GetDouble() < 0);
            Assert.Equal(50000, Predicted(result).GetProperty("patches")[0].GetProperty("pe").GetDouble(), 0);
        }

        [Fact]
        public void The_result_has_the_burn_time_and_the_predict_burn_result_for_the_same_burn()
        {
            var snapshot = Snapshot();
            snapshot.DeltaVSimulator = true;
            var result = Run(snapshot, "{\"goal\":\"set_ap\",\"value\":300000}");
            var burn = Predicted(result).GetProperty("burns")[0];
            Assert.InRange(burn.GetProperty("dv").GetDouble() - result.GetProperty("magnitude").GetDouble(), -0.06, 0.06);
            Assert.Equal(result.GetProperty("at_ut").GetDouble(), burn.GetProperty("at_ut").GetDouble(), 1);
            Assert.Contains("impulsive", Predicted(result).GetProperty("model").GetString());
            // No stages in the snapshot: the burn time says so rather than guessing.
            Assert.Equal(JsonValueKind.Null, result.GetProperty("burn_time").ValueKind);
        }

        [Fact]
        public void The_goal_that_a_burn_cannot_reach_says_why()
        {
            Assert.Contains("below the Pe", Error(Snapshot(), "{\"goal\":\"set_ap\",\"value\":50000}"));
            Assert.Contains("above the Ap", Error(Snapshot(), "{\"goal\":\"set_pe\",\"value\":300000}"));
            Assert.Contains("sea level", Error(Snapshot(), "{\"goal\":\"set_pe\",\"value\":-5}"));
        }

        [Fact]
        public void Inputs_that_do_not_fit_the_goal_are_refused()
        {
            Assert.Contains("at", Error(Snapshot(), "{\"goal\":\"circularize\"}"));
            Assert.Contains("value", Error(Snapshot(), "{\"goal\":\"circularize\",\"at\":\"ap\",\"value\":5}"));
            Assert.Contains("value", Error(Snapshot(), "{\"goal\":\"set_ap\"}"));
            Assert.Contains("leave at out", Error(Snapshot(), "{\"goal\":\"set_ap\",\"value\":300000,\"at\":\"ap\"}"));
            Assert.Contains("direction", Error(Snapshot(), "{\"goal\":\"set_ap\",\"value\":300000,\"direction\":\"prograde\"}"));
            Assert.Contains("one of", Error(Snapshot(), "{\"goal\":\"plane_change\"}"));
        }

        [Fact]
        public void An_escape_orbit_has_no_apoapsis_to_circularize_at()
        {
            var snapshot = Snapshot();
            snapshot.Orbit.SemiMajorAxis = -2000000;
            snapshot.Orbit.Eccentricity = 1.35;
            Assert.Contains("apoapsis", Error(snapshot, "{\"goal\":\"circularize\",\"at\":\"ap\"}"));
        }

        // ---- next_patch_pe ----

        [Fact]
        public void Next_patch_pe_finds_the_burn_whose_mun_periapsis_is_the_goal_and_predict_burn_agrees()
        {
            var snapshot = MunBound();
            var result = Run(snapshot, "{\"goal\":\"next_patch_pe\",\"value\":50000}");
            var patches = Predicted(result).GetProperty("patches").EnumerateArray().ToArray();
            Assert.Equal("Mun", patches[1].GetProperty("body").GetString());
            Assert.InRange(patches[1].GetProperty("pe").GetDouble(), 49998, 50002);
            // predict_burn on the returned components says the same, to within what their rounding to 0.01 m/s moves the Pe.
            var dv = result.GetProperty("dv");
            var again = _tools.Run("predict_burn", "{\"burns\":[{\"at\":" + result.GetProperty("at_ut").GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"dv\":{\"prograde\":" + dv.GetProperty("prograde").GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}}]}", snapshot, new NoPlanningBody());
            Assert.False(again.IsError, again.Json);
            using (var doc = JsonDocument.Parse(again.Json))
                Assert.InRange(doc.RootElement.GetProperty("patches")[1].GetProperty("pe").GetDouble(), 49000, 51000);
            Assert.Equal(60, result.GetProperty("at_ut").GetDouble(), 1);
        }

        [Fact]
        public void Next_patch_pe_takes_the_burn_time_the_caller_gives()
        {
            var result = Run(MunBound(), "{\"goal\":\"next_patch_pe\",\"value\":100000,\"at\":60,\"direction\":\"prograde\"}");
            Assert.Equal(60, result.GetProperty("at_ut").GetDouble(), 1);
            Assert.InRange(Predicted(result).GetProperty("patches")[1].GetProperty("pe").GetDouble(), 99998, 100002);
        }

        [Fact]
        public void Next_patch_pe_says_which_pe_values_the_burns_do_reach_when_the_goal_is_out_of_range()
        {
            var message = Error(MunBound(), "{\"goal\":\"next_patch_pe\",\"value\":90000000000}");
            Assert.Contains("only reaches", message);
            Assert.Contains("90000000000", message);
        }

        [Fact]
        public void Next_patch_pe_says_so_when_no_burn_leaves_the_sphere_of_influence()
        {
            var message = Error(Snapshot(), "{\"goal\":\"next_patch_pe\",\"value\":50000,\"direction\":\"retrograde\"}");
            Assert.Contains("no next patch", message);
            Assert.Contains("retrograde", message);
        }
    }
}
