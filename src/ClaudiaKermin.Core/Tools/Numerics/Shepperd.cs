/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>
    /// Shepperd's universal-variable Kepler propagator, ported from MechJebLib's <c>TwoBody/Shepperd.cs</c> (MechJeb2 a958383),
    /// the state-only <c>Solve</c>; the state transition partials (<c>Solve2</c>) are left until something needs them. It steps
    /// a state vector by a time on an ellipse, a parabola or a hyperbola alike, with no special case and no elements.
    /// Unlike MechJeb's, its input is checked in every build.
    /// <para>
    /// Shepperd SW. Universal Keplerian state transition matrix. Celestial Mechanics. 1985 Feb;35(2):129–44. doi:10.1007/BF01227666.
    /// Shepperd SW. Naturally occurring continued fractions in the variation of Kepler’s equation. Celestial Mechanics. 1987
    /// Mar;42(1–4):91–106. doi:10.1007/BF01232950.
    /// </para>
    /// </summary>
    internal static class Shepperd
    {
        /// <summary>
        /// The position and velocity <paramref name="dt"/> seconds after (or, negative, before) <paramref name="r0"/> and
        /// <paramref name="v0"/> on the two-body orbit round a body of gravitational parameter <paramref name="mu"/>. It solves for
        /// U3 directly by Newton's method, falling back to bisection, and never throws for finite input.
        /// </summary>
        public static (Vec3 r, Vec3 v) Solve(double mu, double dt, Vec3 r0, Vec3 v0)
        {
            var start = default(WarmStart);
            return Solve(mu, dt, r0, v0, ref start);
        }

        /// <summary>
        /// Where the last solve for one state ended: Shepperd's variable u, the time it solved for and du/dt there. A caller that
        /// steps the same state by nearby times keeps one and passes it back, and the next solve starts from u extrapolated to its
        /// time instead of from 0 as MechJeb's does, which saves most of the iterations. The default is a cold start.
        /// </summary>
        internal struct WarmStart
        {
            public double U;
            public double Dt;
            public double Rate;
        }

        /// <summary>
        /// <see cref="Solve(double, double, Vec3, Vec3)"/> starting from <paramref name="start"/> and leaving this solve there; a
        /// start outside the orbit's bounds on u starts at 0. Only the iteration count depends on it, not the answer.
        /// </summary>
        public static (Vec3 r, Vec3 v) Solve(double mu, double dt, Vec3 r0, Vec3 v0, ref WarmStart start)
        {
            if (!Finite(mu) || mu <= 0) throw new ArgumentException("Shepperd.Solve needs a positive, finite gravitational parameter, not " + mu);
            if (!Finite(dt)) throw new ArgumentException("Shepperd.Solve needs a finite time, not " + dt);
            if (!Finite(r0) || !Finite(v0)) throw new ArgumentException("Shepperd.Solve needs a finite position and velocity");
            if (r0.Length <= 0) throw new ArgumentException("Shepperd.Solve needs a position off the body's centre");

            const double tolerance = 1.0e-12;
            const int imax = 50;
            var umax = double.MaxValue;
            var umin = double.MinValue;
            double orbits = 0;
            var threshold = tolerance * Math.Abs(dt);
            var rLen0 = r0.Length;
            var n0 = Vec3.Dot(r0, v0);

            var beta = 2.0 * (mu / rLen0) - Vec3.Dot(v0, v0);

            if (beta != 0.0)
            {
                umax = 1.0 / Math.Sqrt(Math.Abs(beta));
                umin = -1.0 / Math.Sqrt(Math.Abs(beta));
            }

            var u = start.U + (dt - start.Dt) * start.Rate;
            if (!Finite(u) || u <= umin || u >= umax) u = 0;

            if (beta > 0.0)
            {
                orbits = beta * dt - 2 * n0;
                orbits = 1 + orbits * Math.Sqrt(beta) / (Math.PI * mu);
                orbits = Math.Floor(orbits / 2);
            }

            var uold = double.MinValue;
            var dtold = double.MinValue;
            var u1 = 0.0;
            var u2 = 0.0;
            var r1 = 0.0;
            var solved = u;
            var slope = 0.0;

            for (var i = 1; i < imax; i++)
            {
                var q = beta * u * u;
                q /= 1.0 + q;

                double n = 0;
                double r = 1;
                double l = 1;
                double s = 1;
                double d = 3;
                double gcf = 1;
                double k = -5;

                double gold = 0;

                // This solves for U3 directly by using G(3, 0, 3/2, q).
                while (gcf != gold)
                {
                    k = -k;
                    l += 2;
                    d += 4 * l;
                    n += (1 + k) * l;
                    r = d / (d - n * r * q);
                    s = (r - 1) * s;
                    gold = gcf;
                    gcf = gold + s;
                }

                var h0 = 1 - 2 * q;
                var h1 = 2 * u * (1 - q);
                var u0 = 2 * h0 * h0 - 1;
                u1 = 2 * h0 * h1;
                u2 = 2 * h1 * h1;
                var u3 = 2 * h1 * u2 * gcf / 3;

                if (orbits != 0)
                    u3 += 2 * Math.PI * orbits / (beta * Math.Sqrt(beta));

                r1 = rLen0 * u0 + n0 * u1 + mu * u2;
                var t = rLen0 * u1 + n0 * u2 + mu * u3;
                slope = 4 * r1 / (1 + beta * u * u);
                solved = u;

                var terror = dt - t;

                if (Math.Abs(terror) < threshold)
                    break;

                if (i > 1 && u == uold)
                    break;

                if (i > 1 && t == dtold)
                    break;

                uold = u;
                dtold = t;

                var ustep = terror / slope;
                if (ustep > 0)
                {
                    umin = u;
                    u += ustep;

                    if (u > umax)
                        u = (umin + umax) / 2;
                }
                else
                {
                    umax = u;
                    u += ustep;

                    if (u < umin)
                        u = (umin + umax) / 2;
                }
            }

            start.U = solved;
            start.Dt = dt;
            start.Rate = slope > 0 ? 1 / slope : 0;

            var f = 1.0 - mu / rLen0 * u2;
            var gg = 1.0 - mu / r1 * u2;
            var g = rLen0 * u1 + n0 * u2;
            var ff = -mu * u1 / (rLen0 * r1);

            return (f * r0 + g * v0, ff * r0 + gg * v0);
        }

        private static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static bool Finite(Vec3 a)
        {
            return Finite(a.X) && Finite(a.Y) && Finite(a.Z);
        }
    }
}
