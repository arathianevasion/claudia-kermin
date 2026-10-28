// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>A plain 3-vector in a body-centred inertial frame, metres or m/s.</summary>
    internal struct Vec3
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double Length
        {
            get { return Math.Sqrt(X * X + Y * Y + Z * Z); }
        }

        public Vec3 Normalized
        {
            get { return this / Length; }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator *(Vec3 a, double k) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }
        public static Vec3 operator *(double k, Vec3 a) { return a * k; }
        public static Vec3 operator /(Vec3 a, double k) { return new Vec3(a.X / k, a.Y / k, a.Z / k); }

        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }

    /// <summary>
    /// A detached Kepler orbit (ellipse or hyperbola) round one body: the impulsive, two-body model stock's patched conics use,
    /// in plain doubles so it runs and is tested without the game. It is never the live vessel's orbit: it is built from the Game
    /// Snapshot's elements, or from a state vector, and nothing about it reaches the game. Angles in the elements are KSP's:
    /// inclination, LAN and argument of periapsis in degrees, mean anomaly at epoch in radians, the equator as the reference plane.
    /// An orbit built from a state vector keeps that state, and <see cref="State"/> steps it by the time with <see cref="Shepperd"/>
    /// rather than going through the elements and back; the elements still answer apsides, period, nodes and anomalies. Such an
    /// orbit remembers where its last step's solve ended to start the next one there, so it is not for sharing across threads.
    /// </summary>
    internal sealed class Conic
    {
        /// <summary>The body's gravitational parameter, m³/s².</summary>
        public readonly double Mu;
        /// <summary>Semi-major axis, m; negative on a hyperbola, as KSP has it.</summary>
        public readonly double A;
        public readonly double E;
        /// <summary>Unit vectors: towards periapsis, 90° on in the direction of motion, and along the angular momentum.</summary>
        public readonly Vec3 P;
        public readonly Vec3 Q;
        public readonly Vec3 W;
        /// <summary>The UT <see cref="M0"/> is given at, and the mean anomaly then (radians; hyperbolic mean anomaly on a hyperbola).</summary>
        public readonly double Epoch;
        public readonly double M0;
        /// <summary>Whether it came from <see cref="FromState"/>, and the state at <see cref="Epoch"/> it came from.</summary>
        private readonly bool _fromState;
        private readonly Vec3 _r0;
        private readonly Vec3 _v0;
        /// <summary>Where Shepperd's last solve for this orbit ended, its next start: the searches step one orbit by nearby times.</summary>
        private Shepperd.WarmStart _warm;

        private Conic(double mu, double a, double e, Vec3 p, Vec3 q, Vec3 w, double epoch, double m0)
        {
            Mu = mu;
            A = a;
            E = e;
            P = p;
            Q = q;
            W = w;
            Epoch = epoch;
            M0 = m0;
        }

        public bool Closed
        {
            get { return E < 1; }
        }

        /// <summary>The mean motion, rad/s.</summary>
        public double N
        {
            get { return Math.Sqrt(Mu / Math.Abs(A * A * A)); }
        }

        /// <summary>The semi-latus rectum, m.</summary>
        public double SemiLatus
        {
            get { return A * (1 - E * E); }
        }

        /// <summary>Periapsis radius from the body's centre, m.</summary>
        public double PeR
        {
            get { return A * (1 - E); }
        }

        /// <summary>Apoapsis radius, m; infinity on a hyperbola.</summary>
        public double ApR
        {
            get { return Closed ? A * (1 + E) : double.PositiveInfinity; }
        }

        /// <summary>Seconds per orbit; infinity on a hyperbola.</summary>
        public double Period
        {
            get { return Closed ? 2 * Math.PI / N : double.PositiveInfinity; }
        }

        /// <summary>Inclination to the body's equator, degrees.</summary>
        public double Inclination
        {
            get { return Math.Acos(Math.Max(-1, Math.Min(1, W.Z))) * 180 / Math.PI; }
        }

        /// <summary>An orbit from KSP's elements (the Game Snapshot's craft orbit, or a body's orbit round its parent).</summary>
        public static Conic FromElements(double mu, double sma, double ecc, double incDeg, double lanDeg, double argPeDeg, double meanAnomalyAtEpoch, double epoch)
        {
            var i = incDeg * Math.PI / 180;
            var o = lanDeg * Math.PI / 180;
            var w = argPeDeg * Math.PI / 180;
            double ci = Math.Cos(i), si = Math.Sin(i), co = Math.Cos(o), so = Math.Sin(o), cw = Math.Cos(w), sw = Math.Sin(w);
            var p = new Vec3(co * cw - so * sw * ci, so * cw + co * sw * ci, sw * si);
            var q = new Vec3(-co * sw - so * cw * ci, -so * sw + co * cw * ci, cw * si);
            var n = new Vec3(so * si, -co * si, ci);
            return new Conic(mu, sma, ecc, p, q, n, epoch, meanAnomalyAtEpoch);
        }

        /// <summary>The orbit through position <paramref name="r"/> with velocity <paramref name="v"/> at <paramref name="ut"/>.</summary>
        public static Conic FromState(double mu, Vec3 r, Vec3 v, double ut)
        {
            var h = Vec3.Cross(r, v);
            if (h.Length <= 0) throw new ArgumentException("a straight up-and-down path has no orbit plane");
            var w = h.Normalized;
            var rLen = r.Length;
            var eVec = Vec3.Cross(v, h) / mu - r / rLen;
            var e = eVec.Length;
            var a = 1 / (2 / rLen - Vec3.Dot(v, v) / mu);
            // A circle has no periapsis: measure from where the craft is now.
            var p = e > 1e-11 ? eVec / e : r / rLen;
            var q = Vec3.Cross(w, p);
            var nu = Math.Atan2(Vec3.Dot(r, q), Vec3.Dot(r, p));
            if (e <= 1e-11) e = 0;
            var conic = new Conic(mu, a, e, p, q, w, ut, 0);
            return new Conic(mu, a, e, p, q, w, ut, conic.MeanFromTrue(nu), r, v);
        }

        private Conic(double mu, double a, double e, Vec3 p, Vec3 q, Vec3 w, double epoch, double m0, Vec3 r0, Vec3 v0)
            : this(mu, a, e, p, q, w, epoch, m0)
        {
            _fromState = true;
            _r0 = r0;
            _v0 = v0;
        }

        public double MeanAnomaly(double ut)
        {
            return M0 + N * (ut - Epoch);
        }

        /// <summary>Position and velocity relative to the body at <paramref name="ut"/>.</summary>
        public void State(double ut, out Vec3 r, out Vec3 v)
        {
            if (_fromState)
            {
                var s = Shepperd.Solve(Mu, ut - Epoch, _r0, _v0, ref _warm);
                r = s.r;
                v = s.v;
                return;
            }
            var nu = TrueAnomaly(ut);
            StateAtTrue(nu, out r, out v);
        }

        public Vec3 Position(double ut)
        {
            Vec3 r, v;
            State(ut, out r, out v);
            return r;
        }

        public double Radius(double ut)
        {
            return Position(ut).Length;
        }

        public void StateAtTrue(double nu, out Vec3 r, out Vec3 v)
        {
            var p = SemiLatus;
            double c = Math.Cos(nu), s = Math.Sin(nu);
            var radius = p / (1 + E * c);
            r = radius * (c * P + s * Q);
            var k = Math.Sqrt(Mu / p);
            v = k * (-s * P + (E + c) * Q);
        }

        public double TrueAnomaly(double ut)
        {
            var m = MeanAnomaly(ut);
            if (Closed)
            {
                var ea = SolveElliptic(Wrap(m), E);
                return Math.Atan2(Math.Sqrt(1 - E * E) * Math.Sin(ea), Math.Cos(ea) - E);
            }
            var h = SolveHyperbolic(m, E);
            return 2 * Math.Atan(Math.Sqrt((E + 1) / (E - 1)) * Math.Tanh(h / 2));
        }

        public double MeanFromTrue(double nu)
        {
            if (Closed)
            {
                var ea = Math.Atan2(Math.Sqrt(1 - E * E) * Math.Sin(nu), E + Math.Cos(nu));
                return ea - E * Math.Sin(ea);
            }
            var h = 2 * Atanh(Math.Sqrt((E - 1) / (E + 1)) * Math.Tan(nu / 2));
            return E * Math.Sinh(h) - h;
        }

        /// <summary>The first UT at or after <paramref name="after"/> when the craft is at true anomaly <paramref name="nu"/>; null when a hyperbola has passed it.</summary>
        public double? TimeAtTrue(double nu, double after)
        {
            var target = MeanFromTrue(nu);
            if (Closed)
            {
                var d = Wrap(target - MeanAnomaly(after));
                if (d < 0) d += 2 * Math.PI;
                return after + d / N;
            }
            var ut = Epoch + (target - M0) / N;
            return ut >= after - 1e-6 ? ut : (double?)null;
        }

        public double? NextPeriapsis(double after)
        {
            return TimeAtTrue(0, after);
        }

        public double? NextApoapsis(double after)
        {
            return Closed ? TimeAtTrue(Math.PI, after) : null;
        }

        /// <summary>
        /// The first UT after <paramref name="after"/> when the distance from the body's centre crosses <paramref name="radius"/>,
        /// going out (<paramref name="outward"/>) or coming in; null when the orbit never does.
        /// </summary>
        public double? TimeAtRadius(double radius, double after, bool outward)
        {
            if (E < 1e-12 || radius < PeR || radius > ApR) return null;
            var cos = (SemiLatus / radius - 1) / E;
            var nu = Math.Acos(Math.Max(-1, Math.Min(1, cos)));
            return TimeAtTrue(outward ? nu : -nu, after);
        }

        /// <summary>Wraps an angle into (-π, π].</summary>
        private static double Wrap(double angle)
        {
            var a = Math.IEEERemainder(angle, 2 * Math.PI);
            return a <= -Math.PI ? a + 2 * Math.PI : a;
        }

        private static double Atanh(double x)
        {
            return 0.5 * Math.Log((1 + x) / (1 - x));
        }

        private static double SolveElliptic(double m, double e)
        {
            var ea = e < 0.8 ? m : (m >= 0 ? Math.PI : -Math.PI);
            for (var i = 0; i < 100; i++)
            {
                var step = (ea - e * Math.Sin(ea) - m) / (1 - e * Math.Cos(ea));
                ea -= step;
                if (Math.Abs(step) < 1e-14) break;
            }
            return ea;
        }

        private static double SolveHyperbolic(double m, double e)
        {
            var h = Math.Sign(m) * Math.Log(2 * Math.Abs(m) / e + 1.8);
            for (var i = 0; i < 200; i++)
            {
                var step = (e * Math.Sinh(h) - h - m) / (e * Math.Cosh(h) - 1);
                ClampStep(ref step);
                h -= step;
                if (Math.Abs(step) < 1e-14 * Math.Max(1, Math.Abs(h))) break;
            }
            return h;
        }

        /// <summary>Keeps a Newton step on the hyperbola from overshooting far out, where cosh grows fast.</summary>
        private static void ClampStep(ref double step)
        {
            if (step > 1) step = 1;
            else if (step < -1) step = -1;
        }
    }
}
