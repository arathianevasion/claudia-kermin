/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Ported from MechJeb2's MechJebLib/Lambert/Izzo.cs (https://github.com/MuMech/MechJeb2, commit a958383), which MechJeb
// ported from poliastro (MIT License), poliastro/core/iod.py (commit 21fd7719e89a7d22b4eac63141a60a7f1b01768c), itself an
// implementation of Izzo, D. "Revisiting Lambert's problem." Celestial Mechanics and Dynamical Astronomy 121.1 (2015): 1-15.
// Changed from MechJeb's: inputs are checked in every build and a failure is an error in the result, never an exception; the
// transfer plane of a 180° arc comes from the normal; the multi-revolution case finds the shortest time for that many
// revolutions (as poliastro does, which MechJeb dropped) so a time too short is an error, and brackets both of its roots.

using System;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>A <see cref="Lambert"/> transfer: the velocities at each end, or why there is none.</summary>
    internal struct LambertSolution
    {
        /// <summary>The velocity leaving r1, m/s.</summary>
        public Vec3 V1;
        /// <summary>The velocity arriving at r2, m/s.</summary>
        public Vec3 V2;
        /// <summary>Null when solved; otherwise why there is no such transfer, and V1 and V2 are zero.</summary>
        public string Error;
    }

    /// <summary>
    /// Lambert's problem round one body by Izzo's method (Householder iterations on the non-dimensional time of flight): the orbit
    /// from <c>r1</c> to <c>r2</c> in a given time, with whole revolutions on the way if asked. The transfer goes round the way
    /// <c>normal</c> (the craft's orbit normal) turns, or against it when retrograde; a 180° transfer lies in the plane normal to it.
    /// </summary>
    internal static class Lambert
    {
        /// <summary>MechJeb's iteration cap and step tolerance on x for one revolution.</summary>
        private const int MaxIterations = 35;
        private const double StepTolerance = 1e-8;

        /// <summary>The bracketed searches of the multi-revolution case: their cap, and their step tolerance on x.</summary>
        private const int MaxBracketedIterations = 100;
        private const double BracketedTolerance = 1e-13;

        /// <summary>The sine of the angle between r1 and r2 below which they are in line, and the plane comes from the normal.</summary>
        private const double InLineSine = 1e-8;

        /// <summary>
        /// The velocities leaving <paramref name="r1"/> and arriving at <paramref name="r2"/> after <paramref name="seconds"/>;
        /// false when there is no such path (see the other overload's errors).
        /// </summary>
        public static bool Solve(double mu, Vec3 r1, Vec3 r2, double seconds, Vec3 normal, out Vec3 v1, out Vec3 v2,
            int revolutions = 0, bool retrograde = false, bool highEnergy = false)
        {
            var s = Solve(mu, r1, r2, seconds, normal, revolutions, retrograde, highEnergy);
            v1 = s.V1;
            v2 = s.V2;
            return s.Error == null;
        }

        /// <summary>
        /// The transfer from <paramref name="r1"/> to <paramref name="r2"/> in <paramref name="seconds"/> round a body of
        /// gravitational parameter <paramref name="mu"/>, making <paramref name="revolutions"/> whole revolutions first. With
        /// revolutions there are two paths, and this is the lower-energy one (the smaller orbit) unless
        /// <paramref name="highEnergy"/>. An error, never an exception, when the inputs are bad or not finite, r1 and r2 are the
        /// same point, the transfer is 180° and <paramref name="normal"/> is along r1 (no plane), or the time is too short
        /// for the revolutions.
        /// </summary>
        public static LambertSolution Solve(double mu, Vec3 r1, Vec3 r2, double seconds, Vec3 normal,
            int revolutions = 0, bool retrograde = false, bool highEnergy = false)
        {
            var bad = Check(mu, r1, r2, seconds, normal, revolutions);
            if (bad != null) return Fail(bad);

            double r1Norm = r1.Length, r2Norm = r2.Length;
            var iR1 = r1 / r1Norm;
            var iR2 = r2 / r2Norm;

            // Chord and semiperimeter.
            var cNorm = (r2 - r1).Length;
            var s = (r1Norm + r2Norm + cNorm) * 0.5;
            if (cNorm <= 1e-12 * s) return Fail("r1 and r2 are the same point");

            // The plane, and whether the transfer goes the long way round its normal iH: the short way turns round iH, so the
            // long way is the one against the normal asked for (or with it, when retrograde).
            var cross = Vec3.Cross(iR1, iR2);
            var sine = cross.Length;
            Vec3 iH;
            bool flip;
            if (sine > InLineSine)
            {
                iH = cross / sine;
                flip = (Vec3.Dot(iH, normal) < 0) != retrograde;
            }
            else
            {
                var inPlane = normal - Vec3.Dot(normal, iR1) * iR1;
                if (inPlane.Length <= InLineSine * normal.Length) return Fail("r1 and r2 are in line through the body and the normal is along them, so the transfer has no plane");
                iH = inPlane.Normalized;
                flip = retrograde;
            }

            // Geometry of the problem.
            var ll = Math.Sqrt(1 - Math.Min(1.0, cNorm / s));

            // The fundamental tangential directions.
            Vec3 iT1, iT2;
            if (flip)
            {
                ll = -ll;
                iT1 = Vec3.Cross(iR1, iH);
                iT2 = Vec3.Cross(iR2, iH);
            }
            else
            {
                iT1 = Vec3.Cross(iH, iR1);
                iT2 = Vec3.Cross(iH, iR2);
            }

            // Non-dimensional time of flight.
            var scale = Math.Sqrt(2 * mu / (s * s * s));
            var t = scale * seconds;

            double x;
            var error = revolutions == 0 ? OneRevolution(ll, t, out x) : MultiRevolution(ll, t, revolutions, highEnergy, scale, out x);
            if (error != null) return Fail(error);
            var y = Math.Sqrt(1 - ll * ll * (1 - x * x));

            // Reconstruct the radial and tangential components at r1 and r2.
            var gamma = Math.Sqrt(mu * s / 2);
            var rho = (r1Norm - r2Norm) / cNorm;
            var sigma = Math.Sqrt(Math.Max(0, 1 - rho * rho));
            var vr1 = gamma * (ll * y - x - rho * (ll * y + x)) / r1Norm;
            var vr2 = -gamma * (ll * y - x + rho * (ll * y + x)) / r2Norm;
            var vt1 = gamma * sigma * (y + ll * x) / r1Norm;
            var vt2 = gamma * sigma * (y + ll * x) / r2Norm;

            var v1 = vr1 * iR1 + vt1 * iT1;
            var v2 = vr2 * iR2 + vt2 * iT2;
            if (!Finite(v1) || !Finite(v2)) return Fail("the solver did not converge");
            return new LambertSolution { V1 = v1, V2 = v2 };
        }

        /// <summary>Why the inputs can't be solved, or null.</summary>
        private static string Check(double mu, Vec3 r1, Vec3 r2, double seconds, Vec3 normal, int revolutions)
        {
            if (!(mu > 0) || double.IsInfinity(mu)) return "mu must be positive and finite";
            if (!(seconds > 0) || double.IsInfinity(seconds)) return "the time of flight must be positive and finite";
            if (!Finite(r1) || !Finite(r2)) return "r1 and r2 must be finite";
            if (!(r1.Length > 0) || !(r2.Length > 0)) return "r1 and r2 must be away from the body's centre";
            if (!Finite(normal) || !(normal.Length > 0)) return "the normal must be a finite, nonzero vector";
            if (revolutions < 0) return "revolutions must be 0 or more";
            return null;
        }

        /// <summary>x for a single revolution: MechJeb's Householder iterations from pykep's initial guess.</summary>
        private static string OneRevolution(double ll, double t, out double x)
        {
            x = Householder(InitialGuess(t, ll), t, ll);
            return double.IsNaN(x) || !(x > -1) ? "the solver did not converge" : null;
        }

        /// <summary>
        /// x for <paramref name="m"/> revolutions. The time of flight falls from infinity at x = -1 to its least at some x in
        /// between and rises to infinity again at x = 1, so there is no path quicker than that least time, and otherwise one on
        /// each side of it: the one nearer x = 0 is the smaller orbit (a = s / 2 / (1 - x²)), the lower energy.
        /// </summary>
        private static string MultiRevolution(double ll, double t, int m, bool highEnergy, double scale, out double x)
        {
            x = double.NaN;
            var xMin = LeastTime(ll, m);
            if (double.IsNaN(xMin)) return "the solver did not converge";
            double d1, d2, d3;
            var tMin = Tof(xMin, ll, m, out d1, out d2, out d3);
            if (t < tMin)
                return "no path makes " + m + (m == 1 ? " revolution" : " revolutions") + " in " + Num(t / scale)
                    + " s: the quickest takes " + Num(tMin / scale) + " s";

            // MechJeb's two initial guesses, one each side; any guess outside its side starts from the middle of it.
            var right = Math.Pow(8 * t / (m * Math.PI), 2.0 / 3.0);
            var left = Math.Pow((m * Math.PI + Math.PI) / (8 * t), 2.0 / 3.0);
            var xLeft = Bracketed(ll, t, m, -1, xMin, (left - 1) / (left + 1), false);
            var xRight = Bracketed(ll, t, m, xMin, 1, (right - 1) / (right + 1), true);
            if (double.IsNaN(xLeft) || double.IsNaN(xRight)) return "the solver did not converge";
            var leftIsLower = Math.Abs(xLeft) <= Math.Abs(xRight);
            x = leftIsLower != highEnergy ? xLeft : xRight;
            return null;
        }

        /// <summary>The x in (-1, 1) of the least time of flight for <paramref name="m"/> revolutions: Halley on dT/dx, kept in its bracket.</summary>
        private static double LeastTime(double ll, int m)
        {
            double lo = -1, hi = 1, x = 0.1;
            for (var i = 0; i < MaxBracketedIterations; i++)
            {
                double d1, d2, d3;
                Tof(x, ll, m, out d1, out d2, out d3);
                if (double.IsNaN(d1)) return double.NaN;
                if (d1 < 0) lo = x;
                else hi = x;
                var next = x - 2 * d1 * d2 / (2 * d2 * d2 - d1 * d3);
                if (!(next > lo && next < hi)) next = (lo + hi) / 2;
                if (Math.Abs(next - x) < BracketedTolerance) return next;
                x = next;
            }
            return double.NaN;
        }

        /// <summary>
        /// The root of T(x) = <paramref name="t"/> between <paramref name="lo"/> and <paramref name="hi"/>, where T falls (or
        /// <paramref name="rising"/>, rises) with x: Householder steps from <paramref name="x"/>, bisecting when one leaves the bracket.
        /// </summary>
        private static double Bracketed(double ll, double t, int m, double lo, double hi, double x, bool rising)
        {
            if (!(x > lo && x < hi)) x = (lo + hi) / 2;
            for (var i = 0; i < MaxBracketedIterations; i++)
            {
                double d1, d2, d3;
                var f = Tof(x, ll, m, out d1, out d2, out d3) - t;
                if (double.IsNaN(f)) return double.NaN;
                if (f > 0 == rising) hi = x;
                else lo = x;
                var next = HouseholderStep(x, f, d1, d2, d3);
                if (!(next > lo && next < hi)) next = (lo + hi) / 2;
                if (Math.Abs(next - x) < BracketedTolerance) return next;
                x = next;
            }
            return double.NaN;
        }

        /// <summary>
        /// The non-dimensional time of flight at <paramref name="x"/>, and its first three derivatives in x.
        /// </summary>
        private static double Tof(double x, double ll, int m, out double d1, out double d2, out double d3)
        {
            var y = Math.Sqrt(1 - ll * ll * (1 - x * x));
            var t = TofEquationY(x, y, ll, m);
            var ll3 = ll * ll * ll;
            var oneMinusX2 = 1 - x * x;
            d1 = (3 * t * x - 2 + 2 * ll3 * x / y) / oneMinusX2;
            d2 = (3 * t + 5 * x * d1 + 2 * (1 - ll * ll) * ll3 / (y * y * y)) / oneMinusX2;
            d3 = (7 * x * d2 + 8 * d1 - 6 * (1 - ll * ll) * ll3 * ll * ll * x / Math.Pow(y, 5)) / oneMinusX2;
            return t;
        }

        /// <summary>Psi, the auxiliary angle (Eq. 17), by the inverse function that suits the motion.</summary>
        private static double ComputePsi(double x, double y, double ll)
        {
            // Elliptic motion: the arc cosine avoids numerical errors.
            if (x >= -1 && x < 1) return SafeAcos(x * y + ll * (1 - x * x));

            // Hyperbolic motion: the hyperbolic sine is bijective.
            if (x > 1) return Asinh((y - x * ll) * Math.Sqrt(x * x - 1));

            // Parabolic motion.
            return 0.0;
        }

        /// <summary>The time-of-flight equation, with y computed outside.</summary>
        private static double TofEquationY(double x, double y, double ll, int m)
        {
            double t;
            if (m == 0 && Math.Sqrt(0.6) < x && x < Math.Sqrt(1.4))
            {
                var eta = y - ll * x;
                var s1 = (1 - ll - x * eta) * 0.5;
                var q = 4.0 / 3.0 * Hyp2F1B(s1);
                t = (eta * eta * eta * q + 4 * ll * eta) * 0.5;
            }
            else
            {
                var psi = ComputePsi(x, y, ll);
                t = ((psi + m * Math.PI) / Math.Sqrt(Math.Abs(1 - x * x)) - x + ll * y) / (1 - x * x);
            }
            return t;
        }

        /// <summary>
        /// The single-revolution initial guess: pykep's, not the paper's or poliastro's
        /// (https://github.com/esa/pykep/commit/fef5f271984f34c390b4fb85f0781bedb737702c).
        /// </summary>
        private static double InitialGuess(double t, double ll)
        {
            var ll2 = ll * ll;
            var ll3 = ll * ll2;
            var t0 = SafeAcos(ll) + ll * Math.Sqrt(1 - ll2); // Equation 19
            var t1 = 2 * (1 - ll3) / 3.0; // Equation 21
            if (t >= t0) return -(t - t0) / (t - t0 + 4);
            if (t < t1) return t1 * (t1 - t) / (2.0 / 5.0 * (1 - ll2 * ll3) * t) + 1;

            // The constant is ln 2.
            return Math.Pow(t / t0, 0.69314718055994529 / Math.Log(t1 / t0)) - 1.0;
        }

        /// <summary>A zero of the single-revolution time-of-flight equation by Householder's method; NaN when it doesn't converge.</summary>
        private static double Householder(double p0, double t0, double ll)
        {
            for (var i = 0; i < MaxIterations; i++)
            {
                double d1, d2, d3;
                var fval = Tof(p0, ll, 0, out d1, out d2, out d3) - t0;
                var p = HouseholderStep(p0, fval, d1, d2, d3);
                if (Math.Abs(p - p0) < StepTolerance) return p;
                p0 = p;
            }
            return double.NaN;
        }

        /// <summary>The quartic Householder step from <paramref name="x"/> for f and its first three derivatives.</summary>
        private static double HouseholderStep(double x, double f, double d1, double d2, double d3)
        {
            return x - f * ((d1 * d1 - f * d2 / 2) / (d1 * (d1 * d1 - f * d2) + d3 * f * f / 6));
        }

        /// <summary>The hypergeometric function 2F1(3, 1; 5/2; x), see Battin; ported from poliastro/_math/special.py.</summary>
        private static double Hyp2F1B(double x)
        {
            if (x >= 1.0) return double.PositiveInfinity;
            // A NaN would never settle, and loop for ever.
            if (double.IsNaN(x)) return double.NaN;
            var res = 1.0;
            var term = 1.0;
            for (var i = 0; ; i++)
            {
                term = term * (3 + i) * (1 + i) / (2.5 + i) * x / (i + 1);
                var resOld = res;
                res += term;
                if (resOld == res) return res;
            }
        }

        private static double SafeAcos(double x)
        {
            return Math.Acos(Math.Max(-1, Math.Min(1, x)));
        }

        private static double Asinh(double x)
        {
            return Math.Sign(x) * Math.Log(Math.Abs(x) + Math.Sqrt(x * x + 1));
        }

        private static bool Finite(Vec3 v)
        {
            return Finite(v.X) && Finite(v.Y) && Finite(v.Z);
        }

        private static bool Finite(double d)
        {
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }

        private static LambertSolution Fail(string why)
        {
            return new LambertSolution { Error = why };
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
