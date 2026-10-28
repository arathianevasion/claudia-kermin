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
    /// Brent's rootfinding method (secant, inverse quadratic interpolation and bisection), ported from MechJebLib's
    /// <c>Rootfinding/BrentRoot.cs</c> (MechJeb2 a958383), bounded search only. Unlike MechJeb's, a bracket that does not change
    /// sign, a non-finite value or the iteration cap comes back as a <see cref="SearchResult"/> with a reason instead of an exception.
    /// </summary>
    internal static class BrentRoot
    {
        /// <summary>The root of <paramref name="f"/> in [a, b], whose ends must have opposite signs (or be a root).</summary>
        /// <param name="rtol">The tolerance on x, relative to max(|x|, 1).</param>
        /// <param name="maxIter">The cap on iterations after the two end evaluations.</param>
        public static SearchResult Solve(Func<double, double> f, double a, double b, double rtol, int maxIter)
        {
            const string name = "Brent's rootfinding method";
            var problem = SearchInput.Problem(name, a, b, rtol, maxIter);
            if (problem != null) return SearchResult.Invalid(problem);

            var fa = f(a);
            var fb = f(b);
            var calls = 2;
            if (!SearchInput.Finite(fa) || !SearchInput.Finite(fb))
                return SearchResult.Of(SearchStatus.NonFinite, name + ": the function returned a non-finite value at an end of the interval", double.NaN, double.NaN, calls);
            if (fa == 0) return SearchResult.Done(a, fa, calls);
            if (fb == 0) return SearchResult.Done(b, fb, calls);
            if ((fa > 0) == (fb > 0))
                return SearchResult.Of(SearchStatus.NotBracketed,
                    name + ": the interval does not bracket a root (f(" + R(a) + ") and f(" + R(b) + ") have the same sign)",
                    Math.Abs(fa) < Math.Abs(fb) ? a : b, Math.Min(Math.Abs(fa), Math.Abs(fb)), calls);

            if (Math.Abs(fa) < Math.Abs(fb))
            {
                var t = a; a = b; b = t;
                t = fa; fa = fb; fb = t;
            }

            double fc = fb;
            double c = double.NaN, d = double.NaN, e = double.NaN;
            var i = 0;
            while (fb != 0 && a != b)
            {
                if ((fb > 0) == (fc > 0))
                {
                    c = a;
                    fc = fa;
                    d = b - a;
                    e = d;
                }

                if (Math.Abs(fc) < Math.Abs(fb))
                {
                    a = b;
                    b = c;
                    c = a;
                    fa = fb;
                    fb = fc;
                    fc = fa;
                }

                var m = 0.5 * (c - b);
                var toler = 2.0 * rtol * Math.Max(Math.Abs(b), 1.0);
                if (Math.Abs(m) <= toler || fb == 0.0) break;

                if (i >= maxIter)
                    return SearchResult.Of(SearchStatus.IterationCap,
                        name + ": " + maxIter + " iterations without narrowing the root below " + R(rtol) + " (still within " + R(Math.Abs(c - b)) + ")", b, fb, calls);
                i++;

                if (Math.Abs(e) < toler || Math.Abs(fa) <= Math.Abs(fb))
                {
                    d = m;
                    e = m;
                }
                else
                {
                    var s = fb / fa;
                    double p, q;
                    if (a == c)
                    {
                        p = 2.0 * m * s;
                        q = 1.0 - s;
                    }
                    else
                    {
                        q = fa / fc;
                        var r = fb / fc;
                        p = s * (2.0 * m * q * (q - r) - (b - a) * (r - 1.0));
                        q = (q - 1.0) * (r - 1.0) * (s - 1.0);
                    }

                    if (p > 0) q = -q;
                    else p = -p;

                    if (2.0 * p < 3.0 * m * q - Math.Abs(toler * q) && p < Math.Abs(0.5 * e * q))
                    {
                        e = d;
                        d = p / q;
                    }
                    else
                    {
                        d = m;
                        e = m;
                    }
                }

                a = b;
                fa = fb;
                if (Math.Abs(d) > toler) b += d;
                else if (b > c) b -= toler;
                else b += toler;

                fb = f(b);
                calls++;
                if (!SearchInput.Finite(fb))
                    return SearchResult.Of(SearchStatus.NonFinite, name + ": the function returned a non-finite value at " + R(b), a, fa, calls);
            }

            return SearchResult.Done(b, fb, calls);
        }

        private static string R(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
