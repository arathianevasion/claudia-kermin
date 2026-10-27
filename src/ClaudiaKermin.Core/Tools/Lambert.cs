// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// Lambert's problem round one body, single revolution, by universal variables (Vallado's bisection): the orbit from
    /// <c>r1</c> to <c>r2</c> in a given time. The transfer goes round the way <c>normal</c> (the craft's orbit normal) turns,
    /// so it is never a retrograde path.
    /// </summary>
    internal static class Lambert
    {
        /// <summary>
        /// The velocities leaving <paramref name="r1"/> and arriving at <paramref name="r2"/> after <paramref name="seconds"/>;
        /// false when there is no such path (the two points are in line through the body, or the time can't be met).
        /// </summary>
        public static bool Solve(double mu, Vec3 r1, Vec3 r2, double seconds, Vec3 normal, out Vec3 v1, out Vec3 v2)
        {
            v1 = default(Vec3);
            v2 = default(Vec3);
            if (seconds <= 0) return false;
            double a1 = r1.Length, a2 = r2.Length;
            var cos = Math.Max(-1, Math.Min(1, Vec3.Dot(r1, r2) / (a1 * a2)));
            var way = Vec3.Dot(Vec3.Cross(r1, r2), normal) >= 0 ? 1.0 : -1.0;
            var a = way * Math.Sqrt(a1 * a2 * (1 + cos));
            if (Math.Abs(a) < 1e-6 * Math.Max(a1, a2)) return false;

            // The time of flight rises with z; z = 4π² is a whole revolution, and a fast hyperbola is far below -4π².
            double lo = -400, hi = 4 * Math.PI * Math.PI, y = 0;
            var sqrtMu = Math.Sqrt(mu);
            var met = false;
            for (var i = 0; i < 200 && !met; i++)
            {
                var z = (lo + hi) / 2;
                double c, s;
                Stumpff(z, out c, out s);
                y = a1 + a2 + a * (z * s - 1) / Math.Sqrt(c);
                if (y < 0)
                {
                    lo = z;
                    continue;
                }
                var x = Math.Sqrt(y / c);
                var t = (x * x * x * s + a * Math.Sqrt(y)) / sqrtMu;
                met = Math.Abs(t - seconds) < 1e-7 * seconds;
                if (t < seconds) lo = z;
                else hi = z;
            }
            if (!met || y <= 0) return false;
            var f = 1 - y / a1;
            var g = a * Math.Sqrt(y / mu);
            var gDot = 1 - y / a2;
            if (Math.Abs(g) < 1e-12) return false;
            v1 = (r2 - f * r1) / g;
            v2 = (gDot * r2 - r1) / g;
            return !double.IsNaN(v1.X) && !double.IsNaN(v2.X);
        }

        /// <summary>The Stumpff functions C(z) and S(z).</summary>
        private static void Stumpff(double z, out double c, out double s)
        {
            if (z > 1e-6)
            {
                var q = Math.Sqrt(z);
                c = (1 - Math.Cos(q)) / z;
                s = (q - Math.Sin(q)) / (q * q * q);
            }
            else if (z < -1e-6)
            {
                var q = Math.Sqrt(-z);
                c = (Math.Cosh(q) - 1) / -z;
                s = (Math.Sinh(q) - q) / (q * q * q);
            }
            else
            {
                c = 0.5 - z / 24;
                s = 1.0 / 6 - z / 120;
            }
        }
    }
}
