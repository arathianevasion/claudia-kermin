// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>What <c>transfer_to</c> asks the ejection search: the moon, the periapsis to arrive at, and from when.</summary>
    internal sealed class EjectionAsk
    {
        public string Target;
        /// <summary>The periapsis altitude at the target, m above its sea level.</summary>
        public double TargetPe;
        /// <summary>The soonest UT the burn may be: the block's start plus the turn time.</summary>
        public double EarliestUt;
        /// <summary>The window's UT, or null for the next window.</summary>
        public double? WindowUt;
        /// <summary>The latest UT the burn may be (the block's max_duration), or null for none: the search ends there.</summary>
        public double? LatestUt;
        /// <summary>The most delta-v the burn may take (the craft's), m/s, or null for no limit.</summary>
        public double? MaxDv;
    }

    /// <summary>The ejection node found, or why there is none (<see cref="Error"/>, with the rest unset).</summary>
    internal sealed class EjectionPlan
    {
        public string Error;
        public double Ut;
        /// <summary>The node's components, m/s, in stock's maneuver-node frame (as <see cref="PredictBurn"/> takes them).</summary>
        public double Prograde;
        public double Normal;
        public double Radial;
        public double DeltaV;
        /// <summary>When the craft enters the target's sphere of influence, and when it reaches periapsis there.</summary>
        public double EncounterUt;
        public double ArrivalUt;
        /// <summary>The periapsis predicted at the target, m above its sea level.</summary>
        public double PeActual;
        /// <summary>The search: from this UT, over one synodic period (s).</summary>
        public double SearchFrom;
        public double Synodic;
    }

    /// <summary>
    /// <c>transfer_to</c>'s ejection burn, in place of RSVP's search (spec #314): the cheapest ejection to a moon of the body the
    /// craft orbits, within one synodic period from the earliest departure, costed as RSVP costs it (the ejection plus a circular
    /// capture at the target periapsis). A Lambert grid over departure time and time of flight finds the window, and then
    /// <see cref="PredictBurn"/>'s propagation, as <c>solve_burn</c> uses it, sizes the prograde part of the burn so the patched
    /// conics arrive at the periapsis asked for, on a prograde orbit round the target where one does. It works on the Game
    /// Snapshot's detached orbits and only reads. Only the ejection node is given; the capture is <c>circularize</c>'s. After an
    /// ejection that misses (#363), <c>transfer_to</c> asks the same search for its correction from the orbit it is on, within
    /// the block's limits (<see cref="EjectionAsk.LatestUt"/>, <see cref="EjectionAsk.MaxDv"/>).
    /// </summary>
    internal static class EjectionBurn
    {
        private const string Failure = "found no transfer";
        /// <summary>Departures tried per synodic period, as <c>transfer_to</c> seeded RSVP (span / 100).</summary>
        private const int Seeds = 100;
        /// <summary>Times of flight tried per departure, from 0.6 to 1.4 of the Hohmann time.</summary>
        private const int Flights = 21;
        /// <summary>
        /// The steps, m/s, the prograde part is scanned in round the Lambert burn before it is bisected, and how many each way:
        /// fine to find a burn that passes inside the goal periapsis (a Minmus encounter is lost 2 m/s off), coarse out from it.
        /// </summary>
        private const double FineStep = 0.1;
        private const double ScanStep = 2;
        private const int ScanSteps = 60;
        /// <summary>The descent from the grid's dip: its first time-of-flight step (of the Hohmann time), the step it stops under (s), and its most rounds.</summary>
        private const double FirstFlightStep = 0.04;
        private const double DescentFloor = 0.05;
        private const int DescentRounds = 400;
        /// <summary>The bisection on the prograde part: it stops within <see cref="PeClose"/> m of target_pe, and fails beyond <see cref="PeNear"/> m.</summary>
        private const double PeClose = 10;
        private const double PeNear = 100;
        private const int Bisections = 60;

        public static EjectionPlan Find(GameSnapshot snapshot, EjectionAsk ask)
        {
            if (string.IsNullOrEmpty(ask.Target)) return Fail("no target given");
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, ask.Target, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            var target = setup.Target;
            var home = setup.Body;
            if (!string.Equals(target.Parent, home.Name, StringComparison.OrdinalIgnoreCase))
                return Fail(target.Name + " does not orbit " + home.Name + ", the body the craft orbits");
            if (!setup.Start.Closed) return Fail("the craft is not in a closed orbit round " + home.Name);
            var ceiling = target.SoiRadius - target.Radius;
            if (ask.TargetPe < 0 || ask.TargetPe >= ceiling)
                return Fail("target_pe must be from 0 to " + Num(ceiling) + " m, inside " + target.Name + "'s sphere of influence");

            var craft = setup.Start;
            var moon = setup.System.OrbitOf(target);
            var peR = target.Radius + ask.TargetPe;
            var synodic = Synodic(craft.Period, moon.Period);
            var from = Math.Max(setup.Now, Math.Max(ask.EarliestUt, ask.WindowUt ?? double.NegativeInfinity));
            // A latest UT shortens the span, and no window past it may be taken.
            var until = ask.LatestUt ?? double.PositiveInfinity;
            if (until <= from)
                return Fail("the burn must come by UT " + Num(until) + ", before the earliest it can be, UT " + Num(from));
            var span = Math.Min(synodic, until - from);
            Func<double, double, double> cost = (t, tof) =>
            {
                Vec3 burn;
                return Cost(craft, moon, target.Gm, peR, t, tof, out burn);
            };

            // The grid: a departure every span / Seeds from one step before the start (so a window just before it is a
            // falling edge, not a minimum) to a quarter span past the end, each at its cheapest time of flight.
            var step = span / Seeds;
            var count = Seeds + Seeds / 4 + 2;
            var cheapest = new double[count];
            var flight = new double[count];
            for (var i = 0; i < count; i++)
            {
                var t = from + (i - 1) * step;
                var hohmann = Hohmann(home.Gm, craft.Radius(t), moon.A);
                cheapest[i] = double.PositiveInfinity;
                for (var j = 0; j < Flights; j++)
                {
                    var tof = hohmann * (0.6 + 0.8 * j / (Flights - 1));
                    var c = cost(t, tof);
                    if (c < cheapest[i])
                    {
                        cheapest[i] = c;
                        flight[i] = tof;
                    }
                }
            }
            // A window is a dip; the cheapest one inside the span, or else the one just past it (the span's start cut a window).
            int pick = -1, late = -1;
            for (var i = 1; i < count - 1; i++)
            {
                if (double.IsInfinity(cheapest[i]) || !(cheapest[i] < cheapest[i - 1] && cheapest[i] <= cheapest[i + 1])) continue;
                if ((i - 1) * step <= span)
                {
                    if (pick < 0 || cheapest[i] < cheapest[pick]) pick = i;
                }
                else if (late < 0 || cheapest[i] < cheapest[late]) late = i;
            }
            if (pick < 0 && !ask.LatestUt.HasValue) pick = late;
            // With a latest UT the span may hold no dip (the cost still falling at its end): its cheapest departure, then.
            if (pick < 0 && ask.LatestUt.HasValue)
                for (var i = 1; i < count - 1 && (i - 1) * step <= span; i++)
                    if (!double.IsInfinity(cheapest[i]) && (pick < 0 || cheapest[i] < cheapest[pick])) pick = i;
            if (pick < 0)
                return Fail("no departure in the " + Num(span) + " s from UT " + Num(from) + " has a path to " + target.Name);

            // Coordinate descent from the grid's dip, never before the start or past the grid's end (or the latest UT).
            var last = Math.Min(from + (count - 2) * step, until);
            double bestT = from + (pick - 1) * step, bestTof = flight[pick], best = cheapest[pick];
            double dt = step, dTof = Hohmann(home.Gm, craft.Radius(bestT), moon.A) * FirstFlightStep;
            for (var round = 0; round < DescentRounds && (dt > DescentFloor || dTof > DescentFloor); round++)
            {
                var moved = false;
                foreach (var tryT in new[] { bestT - dt, bestT + dt })
                {
                    if (tryT < from || tryT > last) continue;
                    var c = cost(tryT, bestTof);
                    if (c < best) { best = c; bestT = tryT; moved = true; }
                }
                foreach (var tryTof in new[] { bestTof - dTof, bestTof + dTof })
                {
                    var c = cost(bestT, tryTof);
                    if (c < best) { best = c; bestTof = tryTof; moved = true; }
                }
                if (!moved)
                {
                    dt /= 2;
                    dTof /= 2;
                }
            }
            Vec3 lambert;
            Cost(craft, moon, target.Gm, peR, bestT, bestTof, out lambert);
            Vec3 r, v;
            craft.State(bestT, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);

            var plan = Aim(setup, target, moon, bestT, Vec3.Dot(lambert, along), Vec3.Dot(lambert, up), Vec3.Dot(lambert, outward), ask.TargetPe);
            if (plan.Error != null) return plan;
            if (plan.DeltaV > ask.MaxDv)
                return Fail("the cheapest burn, at UT " + Num(bestT) + ", needs " + Num(plan.DeltaV) + " m/s, more than the craft's " + Num(ask.MaxDv.Value) + " m/s");
            plan.SearchFrom = from;
            plan.Synodic = synodic;
            return plan;
        }

        /// <summary>Seconds between one departure window and the next for orbits of these periods; the longer period if they are the same.</summary>
        public static double Synodic(double a, double b)
        {
            var beat = Math.Abs(1 / a - 1 / b);
            return beat < 1e-12 ? Math.Max(a, b) : 1 / beat;
        }

        /// <summary>RSVP's cost of a departure at <paramref name="t"/> taking <paramref name="tof"/> s: the ejection plus a circular capture at <paramref name="peR"/>.</summary>
        private static double Cost(Conic craft, Conic moon, double targetGm, double peR, double t, double tof, out Vec3 burn)
        {
            burn = default(Vec3);
            Vec3 r1, vc, r2, vt, v1, v2;
            craft.State(t, out r1, out vc);
            moon.State(t + tof, out r2, out vt);
            if (!Lambert.Solve(craft.Mu, r1, r2, tof, craft.W, out v1, out v2)) return double.PositiveInfinity;
            burn = v1 - vc;
            var arriving = (v2 - vt).Length;
            return burn.Length + Math.Sqrt(arriving * arriving + 2 * targetGm / peR) - Math.Sqrt(targetGm / peR);
        }

        private static double Hohmann(double gm, double r1, double r2)
        {
            var a = (r1 + r2) / 2;
            return Math.PI * Math.Sqrt(a * a * a / gm);
        }

        /// <summary>One trial of the burn on the patched conics: where it meets the target, or null when it doesn't go straight there.</summary>
        private sealed class Arrival
        {
            /// <summary>The prograde part of the burn tried, m/s.</summary>
            public double ProgradeDv;
            public double Pe;
            /// <summary>Whether the orbit round the target goes the way the target orbits.</summary>
            public bool OrbitsPrograde;
            public double EncounterUt;
            public double PeUt;
        }

        /// <summary>
        /// The Lambert burn aims at the target's centre, without its gravity: scans its prograde part each way on the patched
        /// conics until the periapsis at the target crosses the one asked for, bisects each crossing, and keeps the one that
        /// orbits the target prograde (else the smaller burn).
        /// </summary>
        private static EjectionPlan Aim(PredictBurn.Setup setup, SnapshotBody target, Conic moon, double ut, double prograde, double normal, double radial, double goal)
        {
            var at = JsonReader.Parse(ut.ToString("R", CultureInfo.InvariantCulture));
            Func<double, Arrival> arrive = p =>
            {
                var requests = new List<PredictBurn.BurnRequest> { new PredictBurn.BurnRequest { At = at, Prograde = p, Normal = normal, Radial = radial } };
                List<PredictBurn.BurnDone> done;
                List<Patch> after;
                if (PredictBurn.Simulate(setup, requests, out done, out after, false) != null || after.Count < 2) return null;
                if (after[0].End != PatchEnd.Encounter || !string.Equals(after[0].Next.Name, target.Name, StringComparison.OrdinalIgnoreCase)) return null;
                var there = after[1].Orbit;
                return new Arrival
                {
                    ProgradeDv = p,
                    Pe = there.PeR - target.Radius,
                    OrbitsPrograde = Vec3.Dot(there.W, moon.W) > 0,
                    EncounterUt = after[0].EndUt,
                    PeUt = there.NextPeriapsis(after[1].StartUt) ?? after[1].StartUt,
                };
            };

            // A burn that passes inside the periapsis asked for: the Lambert burn itself, or one a little either side of it.
            var start = arrive(prograde);
            var reached = start != null;
            double? inside = start != null && start.Pe < goal ? prograde : (double?)null;
            for (var k = 1; k <= ScanSteps && !inside.HasValue; k++)
            {
                foreach (var way in new[] { 1.0, -1.0 })
                {
                    var p = prograde + way * k * FineStep;
                    var got = arrive(p);
                    reached |= got != null;
                    if (got != null && got.Pe < goal)
                    {
                        inside = p;
                        break;
                    }
                }
            }
            if (!inside.HasValue)
                return Fail(reached
                    ? "the ejection at UT " + Num(ut) + " reaches " + target.Name + ", but no burn there passes as low as a periapsis of " + Num(goal) + " m"
                    : "the cheapest ejection, at UT " + Num(ut) + ", does not reach " + target.Name + "'s sphere of influence on the patched conics");

            // Out from it each way, the periapsis rises until the encounter is lost: the goal lies between.
            Arrival chosen = null;
            foreach (var way in new[] { 1.0, -1.0 })
            {
                var below = inside.Value;
                for (var k = 1; k <= ScanSteps; k++)
                {
                    var p = inside.Value + way * k * ScanStep;
                    var got = arrive(p);
                    if (got != null && got.Pe < goal)
                    {
                        below = p;
                        continue;
                    }
                    var found = Bisect(arrive, below, p, goal);
                    var better = found != null && (chosen == null || found.OrbitsPrograde && !chosen.OrbitsPrograde
                        || found.OrbitsPrograde == chosen.OrbitsPrograde && Math.Abs(found.ProgradeDv) < Math.Abs(chosen.ProgradeDv));
                    if (better) chosen = found;
                    break;
                }
            }
            return chosen != null
                ? Plan(ut, normal, radial, chosen)
                : Fail("the ejection at UT " + Num(ut) + " reaches " + target.Name + ", but no burn there arrives with a periapsis of " + Num(goal) + " m");
        }

        /// <summary>
        /// Narrows the prograde part between <paramref name="below"/> (periapsis under the goal) and <paramref name="above"/> (over
        /// it, or no encounter) to a periapsis within <see cref="PeClose"/> m of the goal; null when it can't get within <see cref="PeNear"/> m.
        /// </summary>
        private static Arrival Bisect(Func<double, Arrival> arrive, double below, double above, double goal)
        {
            Arrival nearest = null;
            for (var i = 0; i < Bisections; i++)
            {
                var mid = (below + above) / 2;
                var got = arrive(mid);
                if (got != null && (nearest == null || Math.Abs(got.Pe - goal) < Math.Abs(nearest.Pe - goal))) nearest = got;
                if (got != null && Math.Abs(got.Pe - goal) < PeClose) break;
                if (got != null && got.Pe < goal) below = mid;
                else above = mid;
            }
            return nearest != null && Math.Abs(nearest.Pe - goal) < PeNear ? nearest : null;
        }

        private static EjectionPlan Plan(double ut, double normal, double radial, Arrival arrival)
        {
            var prograde = arrival.ProgradeDv;
            return new EjectionPlan
            {
                Ut = ut,
                Prograde = prograde,
                Normal = normal,
                Radial = radial,
                DeltaV = Math.Sqrt(prograde * prograde + normal * normal + radial * radial),
                EncounterUt = arrival.EncounterUt,
                ArrivalUt = arrival.PeUt,
                PeActual = arrival.Pe,
            };
        }

        /// <summary>No node: "found no transfer: <paramref name="why"/>".</summary>
        public static EjectionPlan Fail(string why)
        {
            return new EjectionPlan { Error = Failure + ": " + why };
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
