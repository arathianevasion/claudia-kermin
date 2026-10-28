// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <see cref="Lambert"/>, the Izzo solver (#437): golden figures against the closed form, and arcs that <see cref="Conic"/>
    /// propagates from a known <c>v1</c> to <c>r2</c>, which the solver must recover. The random sweep is #440's.
    /// </summary>
    public sealed class LambertTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double Relative = 1e-6;
        private static readonly Vec3 Up = new Vec3(0, 0, 1);

        // ---- golden ----

        [Fact]
        public void A_Hohmann_half_ellipse_matches_the_closed_form()
        {
            double r1 = 700000, r2 = 12000000;
            var a = (r1 + r2) / 2;
            var tof = Math.PI * Math.Sqrt(a * a * a / KerbinGm);
            var s = Lambert.Solve(KerbinGm, new Vec3(r1, 0, 0), new Vec3(-r2, 0, 0), tof, Up);
            Assert.Null(s.Error);
            var vPe = Math.Sqrt(KerbinGm * 2 * r2 / (r1 * (r1 + r2)));
            var vAp = vPe * r1 / r2;
            Close(new Vec3(0, vPe, 0), s.V1);
            Close(new Vec3(0, -vAp, 0), s.V2);
        }

        [Fact]
        public void Retrograde_flips_the_angular_momentum()
        {
            var r1 = new Vec3(800000, 100000, 20000);
            var r2 = new Vec3(-300000, 900000, -50000);
            var pro = Lambert.Solve(KerbinGm, r1, r2, 1500, Up);
            var retro = Lambert.Solve(KerbinGm, r1, r2, 1500, Up, retrograde: true);
            Assert.Null(pro.Error);
            Assert.Null(retro.Error);
            Assert.True(Vec3.Dot(Vec3.Cross(r1, pro.V1), Up) > 0);
            Assert.True(Vec3.Dot(Vec3.Cross(r1, retro.V1), Up) < 0);
            Arrives(r1, pro.V1, r2, 1500);
            Arrives(r1, retro.V1, r2, 1500);
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        public void Revolutions_give_a_longer_arc_the_propagator_confirms(int revolutions, bool highEnergy)
        {
            var r1 = new Vec3(800000, 0, 0);
            var r2 = new Vec3(0, 1000000, 0);
            var circular = 2 * Math.PI * Math.Sqrt(Math.Pow(900000, 3) / KerbinGm);
            var tof = (revolutions + 0.8) * circular;
            var s = Lambert.Solve(KerbinGm, r1, r2, tof, Up, revolutions, highEnergy: highEnergy);
            Assert.Null(s.Error);
            Arrives(r1, s.V1, r2, tof);
            var orbit = Conic.FromState(KerbinGm, r1, s.V1, 0);
            Assert.True(orbit.Closed);
            Assert.InRange(tof / orbit.Period, revolutions, revolutions + 1);
            Assert.True(Vec3.Dot(Vec3.Cross(r1, s.V1), Up) > 0);
        }

        [Fact]
        public void The_high_energy_path_is_the_higher_energy_of_the_two()
        {
            var r1 = new Vec3(800000, 0, 0);
            var r2 = new Vec3(0, 1000000, 0);
            var tof = 1.8 * 2 * Math.PI * Math.Sqrt(Math.Pow(900000, 3) / KerbinGm);
            var low = Lambert.Solve(KerbinGm, r1, r2, tof, Up, 1);
            var high = Lambert.Solve(KerbinGm, r1, r2, tof, Up, 1, highEnergy: true);
            Assert.True(Energy(r1, low.V1) < Energy(r1, high.V1) - 1);
        }

        // ---- propagate, then solve ----

        /// <summary>
        /// r1 (km), v1 (m/s) and a time of flight (s) round Kerbin: low, high, eccentric and inclined ellipses, past 180°, nearly
        /// a whole orbit, a hyperbola and a near-parabola, two multi-revolution arcs.
        /// </summary>
        [Theory]
        [InlineData(700, 0, 0, 0, 2246, 0, 600)]
        [InlineData(700, 0, 0, 0, 2246, 0, 1500)]
        [InlineData(700, 0, 0, 0, 2246, 0, 1900)]
        [InlineData(700, 0, 0, 0, 3000, 0, 20000)]
        [InlineData(700, 0, 0, 100, 2200, 400, 1200)]
        [InlineData(0, 900, 300, -1800, 50, 600, 2200)]
        [InlineData(-650, 300, 100, -500, -2000, 900, 900)]
        [InlineData(2000, 1500, -400, 300, -1200, 200, 4000)]
        [InlineData(700, 0, 0, 0, 4000, 0, 3000)]
        [InlineData(700, 0, 0, 200, 3170, 100, 5000)]
        [InlineData(5000, 0, 0, 0, 800, 100, 60000)]
        [InlineData(700, 0, 0, 0, 2400, 0, 7000)]
        [InlineData(700, 0, 0, 0, 2300, 200, 12000)]
        public void It_recovers_v1_from_where_the_propagator_lands(double x, double y, double z, double vx, double vy, double vz, double tof)
        {
            var r1 = new Vec3(x * 1000, y * 1000, z * 1000);
            var v1 = new Vec3(vx, vy, vz);
            var orbit = Conic.FromState(KerbinGm, r1, v1, 0);
            Vec3 r2, v2;
            orbit.State(tof, out r2, out v2);
            var revolutions = orbit.Closed ? (int)Math.Floor(tof / orbit.Period) : 0;
            var normal = Vec3.Cross(r1, v1);
            var low = Lambert.Solve(KerbinGm, r1, r2, tof, normal, revolutions);
            Assert.Null(low.Error);
            var s = low;
            if (revolutions > 0 && (low.V1 - v1).Length > Relative * v1.Length)
            {
                s = Lambert.Solve(KerbinGm, r1, r2, tof, normal, revolutions, highEnergy: true);
                Assert.Null(s.Error);
            }
            Close(v1, s.V1);
            Close(v2, s.V2);
        }

        // ---- no solution ----

        [Fact]
        public void A_180_degree_transfer_with_no_plane_is_an_error()
        {
            var r1 = new Vec3(700000, 0, 0);
            var r2 = new Vec3(-12000000, 0, 0);
            var s = Lambert.Solve(KerbinGm, r1, r2, 200000, new Vec3(1, 0, 0));
            Assert.NotNull(s.Error);
            Vec3 v1, v2;
            Assert.False(Lambert.Solve(KerbinGm, r1, r2, 200000, new Vec3(-3, 0, 0), out v1, out v2));
        }

        [Fact]
        public void A_time_too_short_for_the_revolutions_is_an_error()
        {
            var r1 = new Vec3(800000, 0, 0);
            var r2 = new Vec3(0, 1000000, 0);
            var circular = 2 * Math.PI * Math.Sqrt(Math.Pow(900000, 3) / KerbinGm);
            var s = Lambert.Solve(KerbinGm, r1, r2, 0.5 * circular, Up, 1);
            Assert.NotNull(s.Error);
            Assert.Contains("revolution", s.Error);
            Assert.NotNull(Lambert.Solve(KerbinGm, r1, r2, 1.2 * circular, Up, 2, highEnergy: true).Error);
        }

        [Fact]
        public void The_same_point_twice_is_an_error()
        {
            var r = new Vec3(800000, 0, 0);
            Assert.NotNull(Lambert.Solve(KerbinGm, r, r, 5000, Up, 1).Error);
        }

        [Theory]
        [InlineData(0, 1000, 0, 1, 0)]
        [InlineData(-1, 1000, 0, 1, 0)]
        [InlineData(double.NaN, 1000, 0, 1, 0)]
        [InlineData(KerbinGm, 0, 0, 1, 0)]
        [InlineData(KerbinGm, -5, 0, 1, 0)]
        [InlineData(KerbinGm, double.PositiveInfinity, 0, 1, 0)]
        [InlineData(KerbinGm, 1000, -1, 1, 0)]
        [InlineData(KerbinGm, 1000, 0, 0, 0)]
        [InlineData(KerbinGm, 1000, 0, double.NaN, 0)]
        [InlineData(KerbinGm, 1000, 0, 1, 1)]
        public void Bad_inputs_are_an_error_not_an_exception(double mu, double seconds, int revolutions, double normalZ, int zeroR1)
        {
            var r1 = zeroR1 == 1 ? new Vec3(0, 0, 0) : new Vec3(800000, 0, 0);
            var s = Lambert.Solve(mu, r1, new Vec3(0, 900000, 0), seconds, new Vec3(0, 0, normalZ), revolutions);
            Assert.NotNull(s.Error);
            Vec3 v1, v2;
            Assert.False(Lambert.Solve(mu, r1, new Vec3(0, 900000, 0), seconds, new Vec3(0, 0, normalZ), out v1, out v2, revolutions));
        }

        // ---- speed ----

        [Fact]
        public void It_runs_in_microseconds()
        {
            var r1 = new Vec3(700000, 0, 0);
            var r2 = new Vec3(-9000000, 6000000, 300000);
            Lambert.Solve(KerbinGm, r1, r2, 100000, Up);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 10000; i++)
            {
                Lambert.Solve(KerbinGm, r1, r2, 50000 + 10 * i, Up);
                Lambert.Solve(KerbinGm, r1, r2, 500000 + 10 * i, Up, 1);
            }
            Assert.InRange(watch.Elapsed.TotalMilliseconds, 0, 200);
        }

        private static void Arrives(Vec3 r1, Vec3 v1, Vec3 r2, double tof)
        {
            Vec3 r, v;
            Conic.FromState(KerbinGm, r1, v1, 0).State(tof, out r, out v);
            Assert.True((r - r2).Length < Relative * r2.Length, "lands " + (r - r2).Length + " m from r2");
        }

        private static void Close(Vec3 expected, Vec3 actual)
        {
            var off = (actual - expected).Length / expected.Length;
            Assert.True(off < Relative, "off by " + off + " relative: expected (" + expected.X + ", " + expected.Y + ", " + expected.Z
                + "), got (" + actual.X + ", " + actual.Y + ", " + actual.Z + ")");
        }

        private static double Energy(Vec3 r, Vec3 v)
        {
            return Vec3.Dot(v, v) / 2 - KerbinGm / r.Length;
        }
    }
}
