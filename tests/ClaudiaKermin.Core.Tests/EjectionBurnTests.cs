// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:EJECTIONBURN</c>, <c>transfer_to</c>'s ejection solver (spec #314), through <see cref="BlockCalls"/>: against
    /// the Hohmann figures on a hand-built circular orbit with the stock Kerbin system's constants, and on a Game Snapshot JSON
    /// like the <c>transfer_to</c> block test's start (Fixtures/snapshots/kerbin-orbit-100km.json: the stock body elements,
    /// Minmus's inclined orbit included, and a near-circular 100 km orbit at UT 100). That file is written by hand in the
    /// snapshot's format, not captured in the game: no in-orbit snapshot had been recorded. Each node is checked again through
    /// <c>predict_burn</c>, which must arrive at <c>target_pe</c>.
    /// </summary>
    public sealed class EjectionBurnTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double MunSma = 12000000;
        private const double TargetPe = 30000;

        /// <summary>
        /// From 100 km (r1 = 700 km) to the Mun's r2 = 12,000 km: circular speed sqrt(mu/r1) = 2246 m/s, the transfer's
        /// periapsis speed sqrt(mu * 2 * r2 / (r1 * (r1 + r2))) = 3088 m/s, so 842 m/s.
        /// </summary>
        private static readonly double HohmannDv = Math.Sqrt(KerbinGm * 2 * MunSma / (700000.0 * (700000 + MunSma))) - Math.Sqrt(KerbinGm / 700000);

        /// <summary>A circular, equatorial 100 km orbit at its periapsis at UT 0, the Mun at <paramref name="munPhase"/>.</summary>
        private static GameSnapshot Circular(double munPhase = 1.7)
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = KerbinR + 100000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000,
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

        private static GameSnapshot BlockTestStart()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "kerbin-orbit-100km.json")));
        }

        private static IDictionary<string, object> Ask(GameSnapshot snapshot, string target = "Mun", double earliest = 0, object window = null, double pe = TargetPe)
        {
            var input = new Dictionary<string, object> { { "target", target }, { "target_pe", pe }, { "earliest_ut", earliest } };
            if (window != null) input["window"] = window;
            return BlockCalls.EjectionBurn(input, snapshot);
        }

        private static IDictionary<string, object> Solved(GameSnapshot snapshot, string target = "Mun", double earliest = 0, object window = null, double pe = TargetPe)
        {
            var result = Ask(snapshot, target, earliest, window, pe);
            Assert.Equal("", result["error"]);
            return result;
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return (double)result[key];
        }

        /// <summary>The periapsis at <paramref name="target"/> that predict_burn gives for the node, m above sea level.</summary>
        private static double PredictedPe(GameSnapshot snapshot, IDictionary<string, object> node, string target)
        {
            var input = string.Format(CultureInfo.InvariantCulture,
                "{{\"burns\":[{{\"at\":{0:R},\"dv\":{{\"prograde\":{1:R},\"normal\":{2:R},\"radial\":{3:R}}}}}],\"target\":\"{4}\"}}",
                D(node, "ut"), D(node, "prograde"), D(node, "normal"), D(node, "radial"), target);
            var outcome = PlanningTools.Default().Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json))
                return doc.RootElement.GetProperty("closest_approach").GetProperty("altitude").GetDouble();
        }

        // ---- Hohmann figures ----

        [Fact]
        public void From_100_km_to_the_Mun_the_node_is_the_Hohmann_burn()
        {
            Assert.Equal(842, HohmannDv, 0);
            var node = Solved(Circular());
            Assert.InRange(D(node, "dv"), HohmannDv - 5, HohmannDv + 10);
            Assert.InRange(D(node, "prograde"), HohmannDv - 5, HohmannDv + 10);
            Assert.InRange(Math.Abs(D(node, "normal")), 0, 1);
            Assert.InRange(Math.Abs(D(node, "radial")), 0, 15);
        }

        [Fact]
        public void The_node_arrives_at_target_pe_on_predict_burns_propagation()
        {
            var snapshot = Circular();
            var node = Solved(snapshot);
            Assert.InRange(D(node, "target_pe_actual"), TargetPe - 10, TargetPe + 10);
            Assert.InRange(PredictedPe(snapshot, node, "Mun"), TargetPe - 100, TargetPe + 100);
            Assert.True(D(node, "encounter_ut") > D(node, "ut"));
            Assert.True(D(node, "arrival_ut") > D(node, "encounter_ut"));
        }

        [Fact]
        public void A_higher_target_pe_is_reached_too()
        {
            var snapshot = Circular();
            var node = Solved(snapshot, pe: 500000);
            Assert.InRange(PredictedPe(snapshot, node, "Mun"), 499000, 501000);
            Assert.InRange(D(node, "dv"), HohmannDv - 30, HohmannDv + 30);
        }

        [Fact]
        public void It_runs_in_milliseconds()
        {
            Timing.Within(500, () =>
            {
                Solved(Circular());
                Solved(BlockTestStart(), earliest: 400);
            });
        }

        // ---- window "next" and a UT ----

        [Fact]
        public void Window_next_is_the_next_window_from_the_earliest_departure()
        {
            var node = Solved(Circular(), earliest: 300);
            var synodic = D(node, "synodic");
            Assert.InRange(synodic, 1980, 1995);
            Assert.Equal(300, D(node, "search_from"));
            Assert.InRange(D(node, "ut"), 300, 300 + synodic);
        }

        [Fact]
        public void A_window_before_the_earliest_departure_is_skipped_for_the_next()
        {
            var snapshot = Circular();
            var first = Solved(snapshot);
            var window = D(first, "ut");
            var synodic = D(first, "synodic");
            // The block can't burn until a minute after that window: the next one, a synodic period on, costs the same.
            var next = Solved(snapshot, earliest: window + 60);
            Assert.InRange(D(next, "ut"), window + synodic - 60, window + synodic + 60);
            Assert.InRange(D(next, "dv"), D(first, "dv") - 3, D(first, "dv") + 3);
        }

        [Fact]
        public void A_UT_window_searches_from_that_time()
        {
            var snapshot = Circular();
            var first = Solved(snapshot);
            var window = D(first, "ut");
            var synodic = D(first, "synodic");
            var from = window + synodic / 2;
            var later = Solved(snapshot, window: from);
            Assert.Equal(from, D(later, "search_from"));
            Assert.InRange(D(later, "ut"), window + synodic - 60, window + synodic + 60);
            Assert.InRange(D(later, "dv"), HohmannDv - 5, HohmannDv + 10);
        }

        [Fact]
        public void A_UT_window_before_the_earliest_departure_searches_from_the_earliest()
        {
            var node = Solved(Circular(), earliest: 500, window: 100.0);
            Assert.Equal(500, D(node, "search_from"));
            Assert.True(D(node, "ut") >= 500);
        }

        [Fact]
        public void Window_next_given_as_text_is_the_default()
        {
            var snapshot = Circular();
            Assert.Equal(D(Solved(snapshot), "ut"), D(Solved(snapshot, window: "next"), "ut"));
        }

        // ---- the Game Snapshot of the transfer_to block test ----

        [Fact]
        public void The_block_tests_Mun_transfer_keeps_its_contract()
        {
            // test.cfg: ejection_node_dv < 900, ejection_wait (from the block's start) within a synodic period and the lead.
            var snapshot = BlockTestStart();
            var node = Solved(snapshot, earliest: snapshot.Ut + 30);
            Assert.InRange(D(node, "dv"), 830, 900);
            Assert.InRange(D(node, "ut") - snapshot.Ut, 30, 30 + D(node, "synodic"));
            Assert.InRange(PredictedPe(snapshot, node, "Mun"), TargetPe - 100, TargetPe + 100);
        }

        [Fact]
        public void The_block_tests_orbit_reaches_inclined_Minmus()
        {
            var snapshot = BlockTestStart();
            var node = Solved(snapshot, "Minmus", snapshot.Ut + 30, pe: 20000);
            Assert.InRange(PredictedPe(snapshot, node, "Minmus"), 19900, 20100);
            // A Hohmann transfer to Minmus's 47,000 km is 907 m/s in plane, plus the plane change toward its 6 degrees.
            Assert.InRange(D(node, "dv"), 900, 1100);
        }

        // ---- the correction after an ejection that missed (#363) ----

        /// <summary>
        /// The mission of #363: the ejection from <see cref="Circular"/> flown short, leaving apoapsis at 8,587 km and periapsis
        /// at 90 km (the Mun orbits at 12,000 km, so no encounter), seen a minute after the burn. The orbit's periapsis is where
        /// the circular orbit was at the node's UT (a circular equatorial orbit's angle runs at its mean motion from 0 at UT 0).
        /// </summary>
        private static GameSnapshot Missed(out double ejectionUt)
        {
            var snapshot = Circular();
            ejectionUt = D(Solved(snapshot), "ut");
            const double pe = KerbinR + 90238, ap = KerbinR + 8586806;
            var angle = Math.Sqrt(KerbinGm / Math.Pow(KerbinR + 100000, 3)) * ejectionUt * 180 / Math.PI % 360;
            snapshot.Ut = ejectionUt + 60;
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (pe + ap) / 2, Eccentricity = (ap - pe) / (ap + pe), Body = "Kerbin", Periapsis = 90238, Apoapsis = 8586806,
                ArgumentOfPeriapsis = angle, Epoch = ejectionUt,
            };
            return snapshot;
        }

        private static IDictionary<string, object> Correction(GameSnapshot snapshot, double? latest = null, double? maxDv = null)
        {
            var input = new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", TargetPe }, { "earliest_ut", snapshot.Ut + 120 } };
            if (latest.HasValue) input["latest_ut"] = latest.Value;
            if (maxDv.HasValue) input["max_dv"] = maxDv.Value;
            return BlockCalls.EjectionBurn(input, snapshot);
        }

        [Fact]
        public void A_missed_ejection_gets_a_correction_that_meets_the_target_at_target_pe()
        {
            double ejected;
            var snapshot = Missed(out ejected);
            var fix = Correction(snapshot);
            Assert.Equal("", fix["error"]);
            Assert.True(D(fix, "ut") >= snapshot.Ut + 120);
            Assert.InRange(PredictedPe(snapshot, fix, "Mun"), TargetPe - 100, TargetPe + 100);
            // Near periapsis, where the ejection fell about 44 m/s of speed short, tens of m/s put apoapsis back at the Mun.
            Assert.InRange(D(fix, "dv"), 1, 60);
        }

        [Fact]
        public void The_correction_comes_by_the_latest_UT_or_not_at_all()
        {
            double ejected;
            var snapshot = Missed(out ejected);
            // Ten minutes is a short span of the 13.8 h synodic period the search would otherwise take.
            var latest = snapshot.Ut + 720;
            var soon = Correction(snapshot, latest);
            Assert.Equal("", soon["error"]);
            Assert.InRange(D(soon, "ut"), snapshot.Ut + 120, latest);
            Assert.InRange(PredictedPe(snapshot, soon, "Mun"), TargetPe - 100, TargetPe + 100);
            // synodic stays the synodic period, not the span searched (1 / |1/36,550 - 1/138,984| s for the missed orbit).
            Assert.InRange(D(soon, "synodic"), 49000, 51000);
            // Thirty seconds holds no dip in the cost (it rises from the first departure): the cheapest in it is taken.
            Assert.Equal("", Correction(snapshot, snapshot.Ut + 150)["error"]);
            Assert.Equal("found no transfer: the burn must come by UT " + Math.Round(snapshot.Ut + 100) + ", before the earliest it can be, UT "
                + Math.Round(snapshot.Ut + 120), Correction(snapshot, snapshot.Ut + 100)["error"]);
        }

        [Fact]
        public void A_correction_dearer_than_the_craft_s_delta_v_is_refused()
        {
            double ejected;
            var snapshot = Missed(out ejected);
            var error = (string)Correction(snapshot, maxDv: 1)["error"];
            Assert.StartsWith("found no transfer: the cheapest burn, at UT ", error);
            Assert.EndsWith(" m/s, more than the craft's 1 m/s", error);
            Assert.Equal("", Correction(snapshot, maxDv: 5000)["error"]);
        }

        // ---- failures: "found no transfer: <why>" ----

        [Fact]
        public void A_body_that_does_not_orbit_the_craft_s_body_has_no_transfer()
        {
            var result = Ask(BlockTestStart(), "Sun");
            Assert.Equal("found no transfer: Sun does not orbit Kerbin, the body the craft orbits", result["error"]);
            Assert.False(result.ContainsKey("ut"));
        }

        [Fact]
        public void An_unknown_body_has_no_transfer()
        {
            Assert.StartsWith("found no transfer: there is no body named \"Duna\"", (string)Ask(BlockTestStart(), "Duna")["error"]);
        }

        [Fact]
        public void A_landed_craft_has_no_transfer()
        {
            var snapshot = BlockTestStart();
            snapshot.Situation = "LANDED";
            Assert.Equal("found no transfer: the craft is landed, not in flight; there is no orbit to burn from", Ask(snapshot)["error"]);
        }

        [Fact]
        public void A_target_pe_outside_the_sphere_of_influence_has_no_transfer()
        {
            Assert.StartsWith("found no transfer: target_pe must be from 0 to 2229559 m", (string)Ask(BlockTestStart(), pe: 3000000)["error"]);
        }

        [Fact]
        public void A_window_that_is_neither_next_nor_a_UT_has_no_transfer()
        {
            Assert.Equal("found no transfer: window must be \"next\" or a UT, not soon", Ask(BlockTestStart(), window: "soon")["error"]);
        }

        [Fact]
        public void A_missing_target_pe_has_no_transfer()
        {
            var result = BlockCalls.EjectionBurn(new Dictionary<string, object> { { "target", "Mun" } }, BlockTestStart());
            Assert.Equal("found no transfer: target_pe is required", result["error"]);
        }
    }
}
