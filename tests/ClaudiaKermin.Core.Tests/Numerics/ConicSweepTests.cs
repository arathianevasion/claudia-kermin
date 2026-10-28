// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>
    /// Seeded random sweeps of <see cref="Conic"/> (#440): random elements to a state vector and back, and the anomaly
    /// conversions round trip, on ellipses (e in [0, 0.95]) and hyperbolas (e in [1.05, 3]) at every inclination.
    /// </summary>
    public sealed class ConicSweepTests
    {
        private const int Cases = 40;
        private const double Tolerance = 1e-9;

        public static IEnumerable<object[]> Seeds
        {
            get { return Sweep.Seeds; }
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void Elements_to_a_state_and_back_agree(int seed)
        {
            RoundTrips(seed);
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void Anomaly_conversions_round_trip(int seed)
        {
            Anomalies(seed);
        }

        internal static void RoundTrips(int seed)
        {
            var rng = new Random(seed);
            for (var i = 0; i < Cases; i++)
            {
                string inputs;
                var hyperbola = i % 2 == 1;
                var orbit = Sweep.RandomOrbit(rng, hyperbola, out inputs);
                var span = hyperbola ? 3 / orbit.N : orbit.Period;
                var ut = orbit.Epoch + rng.Uniform(-span, span);
                inputs += " ut=" + Sweep.R(ut);

                Vec3 r, v;
                Sweep.ElementState(orbit, ut, out r, out v);
                var back = Conic.FromState(Sweep.KerbinGm, r, v, ut);

                if (Math.Abs(back.A - orbit.A) > Tolerance * Math.Abs(orbit.A)) Sweep.Fail("Conic round trip", seed, i, inputs, "sma " + Sweep.R(back.A) + " came back for " + Sweep.R(orbit.A));
                if (Math.Abs(back.E - orbit.E) > Tolerance) Sweep.Fail("Conic round trip", seed, i, inputs, "e " + Sweep.R(back.E) + " came back for " + Sweep.R(orbit.E));
                var plane = (back.W - orbit.W).Length;
                if (plane > Tolerance) Sweep.Fail("Conic round trip", seed, i, inputs, "orbit normal off by " + Sweep.R(plane));
                // Periapsis has no direction on a circle, and is ill-conditioned for a near-circle.
                if (orbit.E > 1e-2)
                {
                    var peri = (back.P - orbit.P).Length;
                    if (peri > Tolerance) Sweep.Fail("Conic round trip", seed, i, inputs, "periapsis direction off by " + Sweep.R(peri));
                    var mean = hyperbola ? Math.Abs(back.MeanAnomaly(ut) - orbit.MeanAnomaly(ut)) : Sweep.AngleOff(back.MeanAnomaly(ut), orbit.MeanAnomaly(ut));
                    if (mean > Tolerance) Sweep.Fail("Conic round trip", seed, i, inputs, "mean anomaly off by " + Sweep.R(mean));
                }

                // The rebuilt elements give the state back, by the elements path (not Shepperd).
                Vec3 r2, v2;
                Sweep.ElementState(back, ut, out r2, out v2);
                var dr = Sweep.Off(r2, r);
                var dv = Sweep.Off(v2, v);
                if (dr > Tolerance) Sweep.Fail("Conic round trip", seed, i, inputs, "position off by " + Sweep.R(dr) + " relative: " + Sweep.R(r2) + " for " + Sweep.R(r));
                if (dv > Tolerance) Sweep.Fail("Conic round trip", seed, i, inputs, "velocity off by " + Sweep.R(dv) + " relative: " + Sweep.R(v2) + " for " + Sweep.R(v));
            }
        }

        internal static void Anomalies(int seed)
        {
            var rng = new Random(seed);
            for (var i = 0; i < Cases; i++)
            {
                string inputs;
                var hyperbola = i % 2 == 1;
                var orbit = Sweep.RandomOrbit(rng, hyperbola, out inputs);
                // A hyperbola only reaches true anomalies short of its asymptotes.
                var limit = hyperbola ? 0.95 * Math.Acos(-1 / orbit.E) : Math.PI;
                var nu = rng.Uniform(-limit, limit);
                inputs += " nu=" + Sweep.R(nu);

                // true -> mean -> time -> true
                var mean = orbit.MeanFromTrue(nu);
                var ut = orbit.Epoch + (mean - orbit.M0) / orbit.N;
                var again = orbit.TrueAnomaly(ut);
                var off = hyperbola ? Math.Abs(again - nu) : Sweep.AngleOff(again, nu);
                if (off > Tolerance) Sweep.Fail("Conic anomalies", seed, i, inputs, "true anomaly " + Sweep.R(again) + " came back for " + Sweep.R(nu) + " through mean anomaly " + Sweep.R(mean));

                // The same time is when TimeAtTrue says the craft is there, looking from a little before it.
                var before = hyperbola ? ut - 100 : ut - 0.4 * orbit.Period;
                var at = orbit.TimeAtTrue(nu, before);
                if (!at.HasValue) Sweep.Fail("Conic anomalies", seed, i, inputs, "TimeAtTrue found no time after " + Sweep.R(before) + ", expected " + Sweep.R(ut));
                else if (Math.Abs(at.Value - ut) > Tolerance * Math.Max(1, Math.Abs(ut)))
                    Sweep.Fail("Conic anomalies", seed, i, inputs, "TimeAtTrue gave " + Sweep.R(at.Value) + ", expected " + Sweep.R(ut));

                // mean -> true -> mean, from a mean anomaly drawn on its own.
                var m = hyperbola ? rng.Uniform(-6, 6) : rng.Uniform(-Math.PI, Math.PI);
                var t2 = orbit.Epoch + (m - orbit.M0) / orbit.N;
                var back = orbit.MeanFromTrue(orbit.TrueAnomaly(t2));
                var moff = hyperbola ? Math.Abs(back - m) : Sweep.AngleOff(back, m);
                if (moff > Tolerance * Math.Max(1, Math.Abs(m))) Sweep.Fail("Conic anomalies", seed, i, inputs, "mean anomaly " + Sweep.R(back) + " came back for " + Sweep.R(m));
            }
        }
    }
}
