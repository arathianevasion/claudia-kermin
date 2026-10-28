/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>
    /// Brent's derivative-free local minimisation (golden section and successive parabolic interpolation), ported from MechJebLib's
    /// <c>Minimization/BrentMin.cs</c> (MechJeb2 a958383). The iteration cap and a non-finite value come back as a
    /// <see cref="SearchResult"/> with a reason instead of an exception.
    /// </summary>
    internal static class BrentMin
    {
        private const double Epsilon = 2.2204460492503131e-16;

        /// <summary>A local minimum of <paramref name="f"/> in [a, b]; <see cref="SearchResult.X"/> and <see cref="SearchResult.Fx"/> are the point and its value.</summary>
        /// <param name="tol">The absolute tolerance on x, on top of the machine-precision term.</param>
        /// <param name="maxIter">The cap on iterations after the first evaluation.</param>
        public static SearchResult Solve(Func<double, double> f, double a, double b, double tol, int maxIter)
        {
            const string name = "Brent's minimization method";
            var problem = SearchInput.Problem(name, a, b, tol, maxIter);
            if (problem == null && !(a < b)) problem = name + ": the lower bound must be below the upper bound";
            if (problem != null) return SearchResult.Invalid(problem);

            var c = 0.5 * (3.0 - Math.Sqrt(5.0));
            double d = 0, e = 0;
            var eps = Math.Sqrt(Epsilon);
            double sa = a, sb = b;

            var x = sa + c * (b - a);
            var w = x;
            var v = w;
            var fx = f(x);
            var calls = 1;
            if (!SearchInput.Finite(fx))
                return SearchResult.Of(SearchStatus.NonFinite, name + ": the function returned a non-finite value at " + R(x), double.NaN, double.NaN, calls);
            var fw = fx;
            var fv = fw;

            var i = 0;
            while (true)
            {
                var m = 0.5 * (sa + sb);
                var t = eps * Math.Abs(x) + tol;
                var t2 = 2.0 * t;
                if (Math.Abs(x - m) <= t2 - 0.5 * (sb - sa)) break;
                if (i >= maxIter)
                    return SearchResult.Of(SearchStatus.IterationCap,
                        name + ": " + maxIter + " iterations without narrowing the minimum below " + R(tol) + " (still within " + R(sb - sa) + ")", x, fx, calls);
                i++;

                double r = 0.0, q = 0.0, p = 0.0;
                if (t < Math.Abs(e))
                {
                    r = (x - w) * (fx - fv);
                    q = (x - v) * (fx - fw);
                    p = (x - v) * q - (x - w) * r;
                    q = 2.0 * (q - r);
                    if (0.0 < q) p = -p;
                    q = Math.Abs(q);
                    r = e;
                    e = d;
                }

                double u;
                if (Math.Abs(p) < Math.Abs(0.5 * q * r) && q * (sa - x) < p && p < q * (sb - x))
                {
                    d = p / q;
                    u = x + d;
                    if (u - sa < t2 || sb - u < t2) d = x < m ? t : -t;
                }
                else
                {
                    e = x < m ? sb - x : sa - x;
                    d = c * e;
                }

                if (t <= Math.Abs(d)) u = x + d;
                else if (0.0 < d) u = x + t;
                else u = x - t;

                var fu = f(u);
                calls++;
                if (!SearchInput.Finite(fu))
                    return SearchResult.Of(SearchStatus.NonFinite, name + ": the function returned a non-finite value at " + R(u), x, fx, calls);

                if (fu <= fx)
                {
                    if (u < x) sb = x;
                    else sa = x;
                    v = w; fv = fw;
                    w = x; fw = fx;
                    x = u; fx = fu;
                }
                else
                {
                    if (u < x) sa = u;
                    else sb = u;

                    if (fu <= fw || w == x)
                    {
                        v = w; fv = fw;
                        w = u; fw = fu;
                    }
                    else if (fu <= fv || v == x || v == w)
                    {
                        v = u; fv = fu;
                    }
                }
            }

            return SearchResult.Done(x, fx, calls);
        }

        private static string R(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
