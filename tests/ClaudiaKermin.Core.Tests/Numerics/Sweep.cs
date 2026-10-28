// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>
    /// What the seeded random sweeps (#440) share: the fixed seeds, a seeded draw, a random Kepler orbit, and the failure message
    /// that carries the seed and the inputs so a failure reproduces from the log. Each sweep is a <c>[Theory]</c> over
    /// <see cref="Seeds"/> with <c>MemberData</c>, as MechJebLibTest does; a case is one seed's random draws, so
    /// <c>new Random(seed)</c> and the case number rebuild it.
    /// </summary>
    internal static class Sweep
    {
        public const double KerbinGm = 3.5316e12;

        /// <summary>Few enough to run in CI, spread enough to cover each orbit shape.</summary>
        public static readonly int[] SeedValues = new[] { 1, 2, 3, 5, 8, 13, 21, 34 };

        public static IEnumerable<object[]> Seeds
        {
            get
            {
                foreach (var seed in SeedValues) yield return new object[] { seed };
            }
        }

        public static double Uniform(this Random rng, double lo, double hi)
        {
            return lo + (hi - lo) * rng.NextDouble();
        }

        public static string R(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string R(Vec3 v)
        {
            return "(" + R(v.X) + ", " + R(v.Y) + ", " + R(v.Z) + ")";
        }

        /// <summary>Fails with the seed, the case and the inputs, which is everything the case needs to be rerun.</summary>
        public static void Fail(string sweep, int seed, int index, string inputs, string what)
        {
            Assert.Fail(sweep + " seed " + seed + " case " + index + ": " + what + "\n  inputs: " + inputs);
        }

        /// <summary>The relative difference of two vectors, against the second's length.</summary>
        public static double Off(Vec3 actual, Vec3 expected)
        {
            return (actual - expected).Length / expected.Length;
        }

        /// <summary>The difference of two angles, wrapped into [0, π].</summary>
        public static double AngleOff(double a, double b)
        {
            return Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI));
        }

        /// <summary>A random orbit round Kerbin: an ellipse with e in [0, 0.95] or a hyperbola with e in [1.05, 3], any inclination.</summary>
        public static Conic RandomOrbit(Random rng, bool hyperbola, out string inputs)
        {
            // Exactly circular, equatorial, retrograde-equatorial and polar now and then: the corners the formulas divide by.
            var e = hyperbola ? rng.Uniform(1.05, 3) : (rng.Next(10) == 0 ? 0 : rng.Uniform(0, 0.95));
            var pe = rng.Uniform(650000, 5000000);
            var a = pe / (1 - e);
            var kind = rng.Next(10);
            var inc = kind == 0 ? 0 : kind == 1 ? 180 : kind == 2 ? 90 : rng.Uniform(0, 180);
            var lan = rng.Uniform(0, 360);
            var argPe = rng.Uniform(0, 360);
            var m0 = hyperbola ? rng.Uniform(-4, 4) : rng.Uniform(-Math.PI, Math.PI);
            var epoch = rng.Uniform(0, 1e6);
            inputs = "mu=" + R(KerbinGm) + " sma=" + R(a) + " e=" + R(e) + " inc=" + R(inc) + " lan=" + R(lan) + " argPe=" + R(argPe)
                + " m0=" + R(m0) + " epoch=" + R(epoch);
            return Conic.FromElements(KerbinGm, a, e, inc, lan, argPe, m0, epoch);
        }

        /// <summary>The orbit's state by its elements alone, with no propagator: true anomaly from the mean, then the perifocal frame.</summary>
        public static void ElementState(Conic orbit, double ut, out Vec3 r, out Vec3 v)
        {
            orbit.StateAtTrue(orbit.TrueAnomaly(ut), out r, out v);
        }
    }
}
