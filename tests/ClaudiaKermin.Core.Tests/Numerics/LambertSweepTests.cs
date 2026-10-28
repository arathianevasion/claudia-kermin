// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>
    /// Seeded random sweep of <see cref="Lambert"/> (Izzo, #437) against the propagator: a random r1 and v1 are stepped by a time
    /// of flight to r2, and the solver, given r1, r2 and the time, must recover v1 and v2. Prograde and retrograde about the normal
    /// asked for, with 0 to 2 whole revolutions on an ellipse.
    /// </summary>
    public sealed class LambertSweepTests
    {
        private const int Cases = 60;
        private const double Relative = 1e-6;

        /// <summary>
        /// Transfers within this sine of 0° or 180° are left out: r1 and r2 are then nearly in line through the body, the plane they
        /// span is ill-conditioned in any solver, and Lambert's problem there is not well posed.
        /// </summary>
        private const double InLine = 1e-3;

        public static IEnumerable<object[]> Seeds
        {
            get { return Sweep.Seeds; }
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void It_recovers_v1_and_v2_from_the_propagator(int seed)
        {
            Recovers(seed);
        }

        internal static void Recovers(int seed)
        {
            var rng = new Random(seed);
            var solved = 0;
            for (var i = 0; i < Cases; i++)
            {
                var mu = Sweep.KerbinGm;
                var r1 = RandomDirection(rng) * rng.Uniform(700000, 4000000);
                // Between a quarter and a bit over the escape speed: ellipses of every eccentricity, and some hyperbolas.
                var vEscape = Math.Sqrt(2 * mu / r1.Length);
                var v1 = RandomDirection(rng) * (rng.Uniform(0.25, 1.1) * vEscape);
                var retrograde = i % 2 == 1;
                var orbit = Conic.FromState(mu, r1, v1, 0);
                var h = Vec3.Cross(r1, v1);
                var normal = retrograde ? -1 * h : h;

                var revolutions = orbit.Closed ? i % 3 : 0;
                var tof = orbit.Closed
                    ? (revolutions + rng.Uniform(0.1, 0.9)) * orbit.Period
                    : rng.Uniform(100, 20000);
                Vec3 r2, v2;
                ElementPath(orbit, tof, out r2, out v2);

                var inputs = "mu=" + Sweep.R(mu) + " r1=" + Sweep.R(r1) + " v1=" + Sweep.R(v1) + " tof=" + Sweep.R(tof)
                    + " revolutions=" + revolutions + " retrograde=" + retrograde + " normal=" + Sweep.R(normal) + " r2=" + Sweep.R(r2);
                var sine = Vec3.Cross(r1.Normalized, r2.Normalized).Length;
                if (sine < InLine) continue;

                var s = Lambert.Solve(mu, r1, r2, tof, normal, revolutions, retrograde);
                if (s.Error != null) Sweep.Fail("Lambert", seed, i, inputs, "no solution: " + s.Error);
                // With revolutions there are two paths to r2 in the time, low and high energy: the one flown is either.
                if (revolutions > 0 && !Matches(s, v1, v2))
                {
                    s = Lambert.Solve(mu, r1, r2, tof, normal, revolutions, retrograde, true);
                    if (s.Error != null) Sweep.Fail("Lambert", seed, i, inputs, "no high-energy solution: " + s.Error);
                }
                var d1 = Sweep.Off(s.V1, v1);
                var d2 = Sweep.Off(s.V2, v2);
                if (d1 > Relative || d2 > Relative)
                    Sweep.Fail("Lambert", seed, i, inputs, "v1 off by " + Sweep.R(d1) + " and v2 by " + Sweep.R(d2) + " relative: got v1=" + Sweep.R(s.V1) + " v2=" + Sweep.R(s.V2) + ", expected v1=" + Sweep.R(v1) + " v2=" + Sweep.R(v2));
                solved++;
            }
            Assert.True(solved > Cases / 2, "Lambert seed " + seed + ": only " + solved + " of " + Cases + " draws were well posed enough to solve");
        }

        private static bool Matches(LambertSolution s, Vec3 v1, Vec3 v2)
        {
            return s.Error == null && Sweep.Off(s.V1, v1) <= Relative && Sweep.Off(s.V2, v2) <= Relative;
        }

        /// <summary>The state <paramref name="tof"/> seconds on, by Kepler's equation (Conic's element path, not Shepperd).</summary>
        private static void ElementPath(Conic fromState, double tof, out Vec3 r, out Vec3 v)
        {
            Sweep.ElementState(fromState, fromState.Epoch + tof, out r, out v);
        }

        private static Vec3 RandomDirection(Random rng)
        {
            var z = rng.Uniform(-1, 1);
            var phi = rng.Uniform(0, 2 * Math.PI);
            var rho = Math.Sqrt(1 - z * z);
            return new Vec3(rho * Math.Cos(phi), rho * Math.Sin(phi), z);
        }
    }
}
