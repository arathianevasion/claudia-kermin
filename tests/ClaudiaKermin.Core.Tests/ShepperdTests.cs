// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <see cref="Shepperd"/>, the universal-variable propagator (#436): each kind of conic against its closed form, a round trip
    /// out and back, and agreement with <see cref="Conic"/>'s Kepler solution from the elements.
    /// </summary>
    public sealed class ShepperdTests
    {
        private const double KerbinGm = 3.5316e12;

        // ---- each conic against its closed form ----

        [Fact]
        public void A_circular_orbit_goes_a_quarter_round_in_a_quarter_period()
        {
            const double r = 700000;
            var speed = Math.Sqrt(KerbinGm / r);
            var period = 2 * Math.PI * Math.Sqrt(r * r * r / KerbinGm);
            var s = Shepperd.Solve(KerbinGm, period / 4, new Vec3(r, 0, 0), new Vec3(0, speed, 0));
            Close(new Vec3(0, r, 0), s.r, 1e-10);
            Close(new Vec3(-speed, 0, 0), s.v, 1e-10);
        }

        [Fact]
        public void An_ellipse_goes_from_periapsis_to_apoapsis_in_half_a_period_and_past_several_orbits()
        {
            double pe = 700000, ap = 12000000;
            var a = (pe + ap) / 2;
            var vPe = Math.Sqrt(KerbinGm * (2 / pe - 1 / a));
            var vAp = vPe * pe / ap;
            var period = 2 * Math.PI * Math.Sqrt(a * a * a / KerbinGm);
            var half = Shepperd.Solve(KerbinGm, period / 2, new Vec3(pe, 0, 0), new Vec3(0, vPe, 0));
            Close(new Vec3(-ap, 0, 0), half.r, 1e-9);
            Close(new Vec3(0, -vAp, 0), half.v, 1e-9);
            var many = Shepperd.Solve(KerbinGm, 7.5 * period, new Vec3(pe, 0, 0), new Vec3(0, vPe, 0));
            Close(new Vec3(-ap, 0, 0), many.r, 1e-9);
            Close(new Vec3(0, -vAp, 0), many.v, 1e-9);
        }

        [Fact]
        public void A_parabola_reaches_ninety_degrees_on_Barkers_equation()
        {
            // Periapsis q on +x: at true anomaly 90°, tan(ν/2) = 1, so t = √(2q³/μ)·(1 + 1/3), r = (0, 2q, 0), v = √(μ/2q)·(-1, 1, 0).
            const double q = 700000;
            var t = Math.Sqrt(2 * q * q * q / KerbinGm) * 4 / 3;
            var s = Shepperd.Solve(KerbinGm, t, new Vec3(q, 0, 0), new Vec3(0, Math.Sqrt(2 * KerbinGm / q), 0));
            var k = Math.Sqrt(KerbinGm / (2 * q));
            Close(new Vec3(0, 2 * q, 0), s.r, 1e-9);
            Close(new Vec3(-k, k, 0), s.v, 1e-9);
            var back = Shepperd.Solve(KerbinGm, -t, new Vec3(q, 0, 0), new Vec3(0, Math.Sqrt(2 * KerbinGm / q), 0));
            Close(new Vec3(0, -2 * q, 0), back.r, 1e-9);
            Close(new Vec3(k, k, 0), back.v, 1e-9);
        }

        [Fact]
        public void A_hyperbola_reaches_a_known_hyperbolic_anomaly()
        {
            // From periapsis, at hyperbolic anomaly H the time is (e sinh H - H)/n, the position |a|(e - cosh H, √(e²-1) sinh H)
            // and the velocity √(μ/|a|)/(e cosh H - 1)·(-sinh H, √(e²-1) cosh H).
            double e = 1.8, aAbs = 2000000, h = 1.3;
            var n = Math.Sqrt(KerbinGm / (aAbs * aAbs * aAbs));
            var t = (e * Math.Sinh(h) - h) / n;
            var b = Math.Sqrt(e * e - 1);
            var vPe = Math.Sqrt(KerbinGm * (e + 1) / (aAbs * (e - 1)));
            var s = Shepperd.Solve(KerbinGm, t, new Vec3(aAbs * (e - 1), 0, 0), new Vec3(0, vPe, 0));
            var k = Math.Sqrt(KerbinGm / aAbs) / (e * Math.Cosh(h) - 1);
            Close(new Vec3(aAbs * (e - Math.Cosh(h)), aAbs * b * Math.Sinh(h), 0), s.r, 1e-9);
            Close(new Vec3(-k * Math.Sinh(h), k * b * Math.Cosh(h), 0), s.v, 1e-9);
        }

        [Fact]
        public void No_time_leaves_the_state_where_it_is()
        {
            var r0 = new Vec3(812345, -123456, 45678);
            var v0 = new Vec3(300, 2100, -150);
            var s = Shepperd.Solve(KerbinGm, 0, r0, v0);
            Close(r0, s.r, 1e-14);
            Close(v0, s.v, 1e-14);
        }

        // ---- round trip ----

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.3)]
        [InlineData(0.95)]
        [InlineData(1.5)]
        [InlineData(4.0)]
        public void Out_and_back_returns_to_the_start(double e)
        {
            var conic = Conic.FromElements(KerbinGm, e < 1 ? 3000000 : -3000000, e, 28, 40, 75, 0.4, 0);
            Vec3 r0, v0;
            conic.StateAtTrue(0.4, out r0, out v0);
            foreach (var dt in new[] { 60.0, 2000, 40000, -9000 })
            {
                var there = Shepperd.Solve(KerbinGm, dt, r0, v0);
                var back = Shepperd.Solve(KerbinGm, -dt, there.r, there.v);
                Close(r0, back.r, 1e-9);
                Close(v0, back.v, 1e-9);
            }
        }

        // ---- agreement with Conic ----

        [Theory]
        [InlineData(3000000, 0.0)]
        [InlineData(3000000, 0.2)]
        [InlineData(9000000, 0.9)]
        [InlineData(-3000000, 1.2)]
        [InlineData(-800000, 3.0)]
        public void It_agrees_with_Kepler_on_the_elements(double sma, double e)
        {
            var conic = Conic.FromElements(KerbinGm, sma, e, 12, 200, 30, -0.7, 1000);
            var r0 = KeplerPosition(conic, 1000);
            var v0 = KeplerVelocity(conic, 1000);
            foreach (var later in new[] { 1000.0, 1300, 5000, 25000, -4000 })
            {
                var s = Shepperd.Solve(KerbinGm, later - 1000, r0, v0);
                Close(KeplerPosition(conic, later), s.r, 1e-8);
                Close(KeplerVelocity(conic, later), s.v, 1e-8);
            }
        }

        private static Vec3 KeplerPosition(Conic conic, double ut)
        {
            Vec3 r, v;
            conic.StateAtTrue(conic.TrueAnomaly(ut), out r, out v);
            return r;
        }

        private static Vec3 KeplerVelocity(Conic conic, double ut)
        {
            Vec3 r, v;
            conic.StateAtTrue(conic.TrueAnomaly(ut), out r, out v);
            return v;
        }

        // ---- warm start ----

        [Theory]
        [InlineData(3000000, 0.2)]
        [InlineData(-3000000, 1.5)]
        public void A_warm_start_from_any_earlier_solve_gives_the_cold_answer(double sma, double e)
        {
            var conic = Conic.FromElements(KerbinGm, sma, e, 12, 200, 30, -0.7, 0);
            Vec3 r0, v0;
            conic.StateAtTrue(-0.5, out r0, out v0);
            var warm = default(Shepperd.WarmStart);
            // Nearby steps, a jump back, a jump many orbits on, and back to the start.
            foreach (var dt in new[] { 10.0, 20, 35, -6000, 50000, 50010, 0 })
            {
                var cold = Shepperd.Solve(KerbinGm, dt, r0, v0);
                var hot = Shepperd.Solve(KerbinGm, dt, r0, v0, ref warm);
                Close(cold.r, hot.r, 1e-11);
                Close(cold.v, hot.v, 1e-11);
            }
        }

        // ---- input ----

        [Fact]
        public void Non_finite_input_is_an_argument_exception()
        {
            var r0 = new Vec3(700000, 0, 0);
            var v0 = new Vec3(0, 2300, 0);
            Assert.Throws<ArgumentException>(() => Shepperd.Solve(double.NaN, 10, r0, v0));
            Assert.Throws<ArgumentException>(() => Shepperd.Solve(double.PositiveInfinity, 10, r0, v0));
            Assert.Throws<ArgumentException>(() => Shepperd.Solve(KerbinGm, double.NaN, r0, v0));
            Assert.Throws<ArgumentException>(() => Shepperd.Solve(KerbinGm, double.NegativeInfinity, r0, v0));
            Assert.Throws<ArgumentException>(() => Shepperd.Solve(KerbinGm, 10, new Vec3(double.NaN, 0, 0), v0));
            Assert.Throws<ArgumentException>(() => Shepperd.Solve(KerbinGm, 10, r0, new Vec3(0, double.PositiveInfinity, 0)));
        }

        /// <summary>Equal within <paramref name="relative"/> of the expected vector's length.</summary>
        private static void Close(Vec3 expected, Vec3 actual, double relative)
        {
            var miss = (actual - expected).Length;
            Assert.True(miss <= relative * expected.Length, "expected " + Show(expected) + ", got " + Show(actual) + " (off by " + miss + ")");
        }

        private static string Show(Vec3 a)
        {
            return "(" + a.X.ToString("R") + ", " + a.Y.ToString("R") + ", " + a.Z.ToString("R") + ")";
        }
    }
}
