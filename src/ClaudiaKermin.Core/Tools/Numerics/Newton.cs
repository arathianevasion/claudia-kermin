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
    /// Newton's method. <see cref="SolveBracketed"/> is MechJebLib's <c>Rootfinding/Newton.cs</c> (MechJeb2 a958383): Newton with a
    /// fall back to bisection inside a bracket. <see cref="Solve"/> is the plain iteration from a start point, for a well-behaved
    /// function with a derivative (Kepler's equation). Neither throws: a failure is a <see cref="SearchResult"/> with a reason.
    /// </summary>
    internal static class Newton
    {
        /// <summary>Plain Newton from <paramref name="x0"/>, until a step is smaller than <paramref name="tol"/>.</summary>
        public static SearchResult Solve(Func<double, double> f, Func<double, double> fprime, double x0, double tol, int maxIter)
        {
            const string name = "Newton's method";
            if (!SearchInput.Finite(x0)) return SearchResult.Invalid(name + ": the starting point must be finite");
            var problem = SearchInput.Problem(name, tol, maxIter);
            if (problem != null) return SearchResult.Invalid(problem);

            var x = x0;
            var fx = f(x);
            var calls = 1;
            for (var i = 0; i < maxIter; i++)
            {
                if (!SearchInput.Finite(fx))
                    return SearchResult.Of(SearchStatus.NonFinite, name + ": the function returned a non-finite value at " + R(x), x, fx, calls);
                if (fx == 0) return SearchResult.Done(x, fx, calls);
                var dfx = fprime(x);
                if (!SearchInput.Finite(dfx) || dfx == 0)
                    return SearchResult.Of(SearchStatus.FlatDerivative, name + ": the derivative is " + R(dfx) + " at " + R(x), x, fx, calls);
                var dx = fx / dfx;
                x -= dx;
                fx = f(x);
                calls++;
                if (Math.Abs(dx) < tol)
                {
                    if (!SearchInput.Finite(fx))
                        return SearchResult.Of(SearchStatus.NonFinite, name + ": the function returned a non-finite value at " + R(x), x, fx, calls);
                    return SearchResult.Done(x, fx, calls);
                }
            }
            return SearchResult.Of(SearchStatus.IterationCap, name + ": " + maxIter + " iterations without a step under " + R(tol), x, fx, calls);
        }

        /// <summary>Newton inside [a, b], whose ends must have opposite signs, falling back to bisection when a step leaves the bracket or is too slow.</summary>
        public static SearchResult SolveBracketed(Func<double, double> f, Func<double, double> fprime, double a, double b, double tol, int maxIter)
        {
            const string name = "Newton's method";
            var problem = SearchInput.Problem(name, a, b, tol, maxIter);
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

            // Orient so that f(a) < 0 < f(b).
            if (fa > 0)
            {
                var t = a; a = b; b = t;
            }

            var c = 0.5 * (a + b);
            var dxold = Math.Abs(b - a);
            var dx = dxold;
            var fc = f(c);
            var dfc = fprime(c);
            calls++;
            for (var i = 0; i < maxIter; i++)
            {
                if (!SearchInput.Finite(fc) || !SearchInput.Finite(dfc))
                    return SearchResult.Of(SearchStatus.NonFinite, name + ": the function or its derivative returned a non-finite value at " + R(c), c, fc, calls);

                if (((c - b) * dfc - fc) * ((c - a) * dfc - fc) > 0.0 || Math.Abs(2.0 * fc) > Math.Abs(dxold * dfc))
                {
                    dxold = dx;
                    dx = 0.5 * (b - a);
                    c = a + dx;
                    if (a == c) return SearchResult.Done(c, fc, calls);
                }
                else
                {
                    dxold = dx;
                    dx = fc / dfc;
                    var oldc = c;
                    c -= dx;
                    if (oldc == c) return SearchResult.Done(c, fc, calls);
                }

                if (Math.Abs(dx) < tol)
                    return SearchResult.Done(c, f(c), calls + 1);

                fc = f(c);
                dfc = fprime(c);
                calls++;

                if (fc < 0.0) a = c;
                else b = c;
            }
            return SearchResult.Of(SearchStatus.IterationCap, name + ": " + maxIter + " iterations without a step under " + R(tol), c, fc, calls);
        }

        private static string R(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
