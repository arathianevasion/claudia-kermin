// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>
    /// Seeded random sweep of the encounter search in <see cref="BodySystem"/> (#440, reached through <see cref="BodySystem.Follow"/>,
    /// as <c>FirstEncounter</c> is private): random transfer arcs from low Kerbin orbit towards the Mun, some aimed to meet it and
    /// some not, checked against fine fixed-step stepping of the same arc. An arc the stepping finds the Mun's sphere of influence
    /// on must end in an encounter within one step of it; an arc it never finds there must return none. The search's step-limit
    /// path is #439's and is not tested here.
    /// </summary>
    public sealed class TrajectorySweepTests
    {
        private const int Cases = 24;
        private const double KerbinR = 600000;
        private const double MunSma = 12000000;
        private const double MunSoi = 2429559.1;
        private const double MunGm = 6.5138398e10;

        /// <summary>The fixed step of the reference, s: the craft moves under 10 km in it, against a sphere of influence 2429 km in radius.</summary>
        private const double ReferenceStep = 10;

        /// <summary><c>FirstEncounter</c> bisects the crossing to a millisecond; its time may sit that far inside the sphere.</summary>
        private const double BisectTolerance = 1e-3;

        public static IEnumerable<object[]> Seeds
        {
            get { return Sweep.Seeds; }
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void FirstEncounter_agrees_with_fine_stepping_and_misses_return_none(int seed)
        {
            Agrees(seed);
        }

        private static BodySystem Bodies(double munPhase)
        {
            return new BodySystem(new[]
            {
                new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 },
                new SnapshotBody
                {
                    Name = "Kerbin", Parent = "Sun", Gm = Sweep.KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1,
                    SoiRadius = 84159286.48, RotationPeriod = 21549.425, Sma = 13599840256,
                },
                new SnapshotBody
                {
                    Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = 200000, SoiRadius = MunSoi, RotationPeriod = 138984.38,
                    Sma = MunSma, MeanAnomalyAtEpoch = munPhase,
                },
            });
        }

        internal static void Agrees(int seed)
        {
            var rng = new Random(seed);
            int hits = 0, misses = 0;
            for (var i = 0; i < Cases; i++)
            {
                // Equatorial like the Mun, from a periapsis of 100 km to an apoapsis in or near its distance.
                var aimed = i % 2 == 0;
                var pe = KerbinR + rng.Uniform(100000, 300000);
                var ap = aimed ? rng.Uniform(MunSma - 0.8 * MunSoi, MunSma + 0.8 * MunSoi) : rng.Uniform(8000000, 20000000);
                var a = (pe + ap) / 2;
                var e = (ap - pe) / (ap + pe);
                var argPe = rng.Uniform(0, 360);
                var orbit = Conic.FromElements(Sweep.KerbinGm, a, e, 0, 0, argPe, 0, 0);

                // The Mun is put where it will be (at, or an offset from) the craft's apoapsis when the craft gets there.
                var offset = aimed ? rng.Uniform(-0.4 * MunSoi / MunSma, 0.4 * MunSoi / MunSma) : rng.Uniform(-Math.PI, Math.PI);
                var munN = Math.Sqrt(Sweep.KerbinGm / (MunSma * MunSma * MunSma));
                var phase = argPe * Math.PI / 180 + Math.PI + offset - munN * orbit.Period / 2;
                var bodies = Bodies(phase);
                var kerbin = bodies.Find("Kerbin");
                var mun = bodies.Find("Mun");
                var munOrbit = bodies.OrbitOf(mun);

                var inputs = "pe=" + Sweep.R(pe) + " ap=" + Sweep.R(ap) + " argPe=" + Sweep.R(argPe) + " munPhase=" + Sweep.R(phase)
                    + " offset=" + Sweep.R(offset) + " aimed=" + aimed;

                // The reference: step the same arc (by its elements) a fixed step at a time over the search's window, one orbit.
                double? firstInside = null;
                for (var t = 0.0; t <= orbit.Period; t += ReferenceStep)
                {
                    if ((orbit.Position(t) - munOrbit.Position(t)).Length <= MunSoi)
                    {
                        firstInside = t;
                        break;
                    }
                }

                // Two ways the same orbit reaches the search: from its elements, and from a state vector (as after a burn).
                for (var form = 0; form < 2; form++)
                {
                    var searched = orbit;
                    var how = "elements";
                    if (form == 1)
                    {
                        Vec3 r, v;
                        orbit.State(0, out r, out v);
                        searched = Conic.FromState(Sweep.KerbinGm, r, v, 0);
                        how = "state";
                    }
                    var patch = bodies.Follow(kerbin, searched, 0, 1)[0];
                    var where = inputs + " built from " + how;
                    if (firstInside.HasValue)
                    {
                        if (patch.End != PatchEnd.Encounter)
                            Sweep.Fail("Trajectory", seed, i, where, "stepping enters the Mun's sphere at " + Sweep.R(firstInside.Value) + " s but the search ended " + patch.End);
                        if (patch.Next == null || patch.Next.Name != "Mun")
                            Sweep.Fail("Trajectory", seed, i, where, "the encounter is with " + (patch.Next == null ? "nothing" : patch.Next.Name) + ", not the Mun");
                        // The true crossing lies in the step before the first one inside; the search bisects to just inside it.
                        if (patch.EndUt < firstInside.Value - ReferenceStep - BisectTolerance || patch.EndUt > firstInside.Value + BisectTolerance)
                            Sweep.Fail("Trajectory", seed, i, where, "encounter at " + Sweep.R(patch.EndUt) + " s, stepping's first step inside is " + Sweep.R(firstInside.Value) + " s");
                        var gap = (orbit.Position(patch.EndUt) - munOrbit.Position(patch.EndUt)).Length - MunSoi;
                        if (gap > 10 || gap < -1000)
                            Sweep.Fail("Trajectory", seed, i, where, "at the encounter time the craft is " + Sweep.R(gap) + " m from the sphere's edge");
                    }
                    else if (patch.End == PatchEnd.Encounter)
                    {
                        // Only a pass the step skips could be real: it must still be a pass through the sphere.
                        var gap = (orbit.Position(patch.EndUt) - munOrbit.Position(patch.EndUt)).Length - MunSoi;
                        Sweep.Fail("Trajectory", seed, i, where, "stepping never enters the Mun's sphere but the search reports an encounter at " + Sweep.R(patch.EndUt) + " s, " + Sweep.R(gap) + " m from its edge");
                    }
                }
                if (firstInside.HasValue) hits++;
                else misses++;
            }
            Assert.True(hits > 0 && misses > 0, "Trajectory seed " + seed + ": " + hits + " encounters and " + misses + " misses; the sweep needs both");
        }
    }
}
