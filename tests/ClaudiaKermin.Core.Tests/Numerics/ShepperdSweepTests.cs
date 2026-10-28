// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>
    /// Seeded random sweep of <see cref="Shepperd"/> (#436) against <see cref="Conic"/>'s element path: a random state stepped
    /// forwards and backwards by random times, several whole orbits on an ellipse, must land where Kepler's equation puts it.
    /// </summary>
    public sealed class ShepperdSweepTests
    {
        private const int Cases = 40;
        private const double Tolerance = 1e-8;

        public static IEnumerable<object[]> Seeds
        {
            get { return Sweep.Seeds; }
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void It_agrees_with_the_element_path(int seed)
        {
            Agrees(seed);
        }

        internal static void Agrees(int seed)
        {
            var rng = new Random(seed);
            for (var i = 0; i < Cases; i++)
            {
                string inputs;
                var hyperbola = i % 2 == 1;
                var orbit = Sweep.RandomOrbit(rng, hyperbola, out inputs);
                Vec3 r0, v0;
                Sweep.ElementState(orbit, orbit.Epoch, out r0, out v0);
                inputs += " r0=" + Sweep.R(r0) + " v0=" + Sweep.R(v0);

                var dt = RandomTime(rng, orbit, hyperbola);
                inputs += " dt=" + Sweep.R(dt);
                var s = Shepperd.Solve(Sweep.KerbinGm, dt, r0, v0);
                Vec3 er, ev;
                Sweep.ElementState(orbit, orbit.Epoch + dt, out er, out ev);
                var dr = Sweep.Off(s.r, er);
                var dv = Sweep.Off(s.v, ev);
                if (dr > Tolerance) Sweep.Fail("Shepperd", seed, i, inputs, "position off by " + Sweep.R(dr) + " relative: " + Sweep.R(s.r) + " for " + Sweep.R(er));
                if (dv > Tolerance) Sweep.Fail("Shepperd", seed, i, inputs, "velocity off by " + Sweep.R(dv) + " relative: " + Sweep.R(s.v) + " for " + Sweep.R(ev));

                // The orbit built from that state steps it the same way, carrying each solve's end to the next time.
                var stepped = Conic.FromState(Sweep.KerbinGm, r0, v0, orbit.Epoch);
                for (var k = 0; k < 3; k++)
                {
                    var t = RandomTime(rng, orbit, hyperbola);
                    Vec3 r, v;
                    stepped.State(orbit.Epoch + t, out r, out v);
                    Sweep.ElementState(orbit, orbit.Epoch + t, out er, out ev);
                    dr = Sweep.Off(r, er);
                    dv = Sweep.Off(v, ev);
                    if (dr > Tolerance || dv > Tolerance)
                        Sweep.Fail("Conic.FromState().State", seed, i, inputs + " step " + k + " dt=" + Sweep.R(t), "position off by " + Sweep.R(dr) + " and velocity by " + Sweep.R(dv) + " relative");
                }
            }
        }

        /// <summary>Up to three orbits either way on an ellipse; a hyperbola to a mean anomaly within six of the periapsis's.</summary>
        private static double RandomTime(Random rng, Conic orbit, bool hyperbola)
        {
            return hyperbola ? (rng.Uniform(-6, 6) - orbit.M0) / orbit.N : rng.Uniform(-3, 3) * orbit.Period;
        }
    }
}
