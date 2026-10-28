// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using ClaudiaKermin.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The ported searches (BrentRoot, BrentMin, Newton) and Scale on functions with known answers. Each search's test asserts a
    /// generous wall-clock budget in the style of EjectionBurnTests.It_runs_in_milliseconds; the pattern is settled in #310.
    /// </summary>
    public sealed class NumericsTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;

        private static double Cubic(double x) { return x * x * x - 2 * x - 5; }
        private static double CubicRoot = 2.0945514815423265;

        // Kepler's equation at e = 0.9, M = 1: E - e sin E - M = 0.
        private static double Kepler(double ea) { return ea - 0.9 * Math.Sin(ea) - 1; }
        private static double KeplerSlope(double ea) { return 1 - 0.9 * Math.Cos(ea); }

        private static void WithinBudget(Action search)
        {
            search();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) search();
            Assert.InRange(watch.Elapsed.TotalMilliseconds, 0, 500);
        }

        // ---- BrentRoot ----

        [Fact]
        public void BrentRoot_finds_the_root_of_a_cubic()
        {
            var result = BrentRoot.Solve(Cubic, 2, 3, 1e-12, 100);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(CubicRoot, result.X, 9);
            Assert.InRange(Math.Abs(result.Fx), 0, 1e-9);
        }

        [Fact]
        public void BrentRoot_solves_Keplers_equation_at_e_0_9()
        {
            var result = BrentRoot.Solve(Kepler, 0, Math.PI, 1e-12, 100);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(1.862, result.X, 3);
            Assert.InRange(Math.Abs(Kepler(result.X)), 0, 1e-10);
        }

        [Fact]
        public void BrentRoot_takes_an_end_that_is_a_root()
        {
            var result = BrentRoot.Solve(x => x - 3, 3, 10, 1e-9, 10);
            Assert.True(result.Converged);
            Assert.Equal(3, result.X);
        }

        [Fact]
        public void BrentRoot_reports_a_non_bracketing_interval_without_throwing()
        {
            var result = BrentRoot.Solve(Cubic, 3, 4, 1e-12, 100);
            Assert.False(result.Converged);
            Assert.Equal(SearchStatus.NotBracketed, result.Status);
            Assert.Contains("bracket", result.Reason);
        }

        [Fact]
        public void BrentRoot_reports_the_iteration_cap_without_throwing()
        {
            var result = BrentRoot.Solve(Cubic, 0, 100, 1e-14, 2);
            Assert.Equal(SearchStatus.IterationCap, result.Status);
            Assert.Contains("2 iterations", result.Reason);
        }

        [Fact]
        public void BrentRoot_reports_bad_input_and_non_finite_values()
        {
            Assert.Equal(SearchStatus.InvalidInput, BrentRoot.Solve(Cubic, double.NaN, 3, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, BrentRoot.Solve(Cubic, 0, double.PositiveInfinity, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, BrentRoot.Solve(Cubic, 0, 3, 0, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, BrentRoot.Solve(Cubic, 0, 3, 1e-9, 0).Status);
            Assert.Equal(SearchStatus.NonFinite, BrentRoot.Solve(x => double.NaN, 0, 3, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.NonFinite, BrentRoot.Solve(x => x < 1 ? -1 : (x < 2 ? double.NaN : 1), 0, 3, 1e-9, 10).Status);
        }

        [Fact]
        public void BrentRoot_runs_in_milliseconds()
        {
            WithinBudget(() => BrentRoot.Solve(Kepler, 0, Math.PI, 1e-12, 100));
        }

        // ---- BrentMin ----

        [Fact]
        public void BrentMin_finds_the_minimum_of_a_parabola()
        {
            var result = BrentMin.Solve(x => (x - 2) * (x - 2) + 3, -10, 10, 1e-9, 100);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(2, result.X, 6);
            Assert.Equal(3, result.Fx, 9);
        }

        [Fact]
        public void BrentMin_finds_the_minimum_of_a_flat_quartic()
        {
            // (x - 1)^4 is flat to the last digits near the minimum, so x is only good to about the fourth root of epsilon.
            var result = BrentMin.Solve(x => Math.Pow(x - 1, 4), -5, 5, 1e-9, 200);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(1, result.X, 2);
            Assert.InRange(result.Fx, 0, 1e-8);
        }

        [Fact]
        public void BrentMin_ends_on_a_constant_function()
        {
            var result = BrentMin.Solve(x => 7, 0, 10, 1e-9, 200);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(7, result.Fx);
            Assert.InRange(result.X, 0, 10);
        }

        [Fact]
        public void BrentMin_finds_the_closest_approach()
        {
            // Distance from the point (3, 1) to the line y = x/2: the derivative 2(x - 3) + (x/2 - 1) is zero at x = 2.8.
            var result = BrentMin.Solve(x => Math.Sqrt((x - 3) * (x - 3) + (x / 2 - 1) * (x / 2 - 1)), -10, 10, 1e-9, 100);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(2.8, result.X, 5);
        }

        [Fact]
        public void BrentMin_reports_the_iteration_cap_with_the_best_point_so_far()
        {
            var result = BrentMin.Solve(x => (x - 2) * (x - 2), -10, 10, 1e-12, 2);
            Assert.Equal(SearchStatus.IterationCap, result.Status);
            Assert.Contains("2 iterations", result.Reason);
            Assert.False(double.IsNaN(result.X));
        }

        [Fact]
        public void BrentMin_reports_bad_input_and_non_finite_values()
        {
            Assert.Equal(SearchStatus.InvalidInput, BrentMin.Solve(x => x, 5, 1, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, BrentMin.Solve(x => x, 1, 1, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, BrentMin.Solve(x => x, double.NaN, 1, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, BrentMin.Solve(x => x, 0, 1, -1, 10).Status);
            Assert.Equal(SearchStatus.NonFinite, BrentMin.Solve(x => double.NaN, 0, 1, 1e-9, 10).Status);
        }

        [Fact]
        public void BrentMin_runs_in_milliseconds()
        {
            WithinBudget(() => BrentMin.Solve(x => Math.Pow(x - 1, 4), -5, 5, 1e-9, 200));
        }

        // ---- Newton ----

        [Fact]
        public void Newton_solves_Keplers_equation_at_e_0_9_from_a_start_point()
        {
            var result = Newton.Solve(Kepler, KeplerSlope, Math.PI, 1e-14, 50);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(1.862, result.X, 3);
            Assert.InRange(Math.Abs(Kepler(result.X)), 0, 1e-12);
        }

        [Fact]
        public void Newton_finds_the_root_of_a_cubic()
        {
            var result = Newton.Solve(Cubic, x => 3 * x * x - 2, 2, 1e-14, 50);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(CubicRoot, result.X, 12);
        }

        [Fact]
        public void Newton_bracketed_solves_Keplers_equation_and_a_cubic()
        {
            var kepler = Newton.SolveBracketed(Kepler, KeplerSlope, 0, Math.PI, 1e-14, 100);
            Assert.True(kepler.Converged, kepler.Reason);
            Assert.InRange(Math.Abs(Kepler(kepler.X)), 0, 1e-12);

            var cubic = Newton.SolveBracketed(Cubic, x => 3 * x * x - 2, 2, 3, 1e-14, 100);
            Assert.True(cubic.Converged, cubic.Reason);
            Assert.Equal(CubicRoot, cubic.X, 12);
        }

        [Fact]
        public void Newton_bracketed_reports_a_non_bracketing_interval()
        {
            var result = Newton.SolveBracketed(Cubic, x => 3 * x * x - 2, 3, 4, 1e-12, 100);
            Assert.Equal(SearchStatus.NotBracketed, result.Status);
            Assert.Contains("bracket", result.Reason);
        }

        [Fact]
        public void Newton_reports_the_iteration_cap()
        {
            var plain = Newton.Solve(Kepler, KeplerSlope, Math.PI, 1e-14, 1);
            Assert.Equal(SearchStatus.IterationCap, plain.Status);
            Assert.Contains("1 iterations", plain.Reason);

            var bracketed = Newton.SolveBracketed(Cubic, x => 3 * x * x - 2, 0, 100, 1e-14, 2);
            Assert.Equal(SearchStatus.IterationCap, bracketed.Status);
        }

        [Fact]
        public void Newton_reports_a_flat_derivative_and_bad_input()
        {
            var flat = Newton.Solve(x => x * x + 1, x => 0, 1, 1e-9, 10);
            Assert.Equal(SearchStatus.FlatDerivative, flat.Status);
            Assert.Equal(SearchStatus.InvalidInput, Newton.Solve(Cubic, x => 1, double.NaN, 1e-9, 10).Status);
            Assert.Equal(SearchStatus.InvalidInput, Newton.Solve(Cubic, x => 1, 1, 1e-9, 0).Status);
            Assert.Equal(SearchStatus.InvalidInput, Newton.SolveBracketed(Cubic, x => 1, double.NaN, 3, 1e-9, 10).Status);
        }

        [Fact]
        public void Newton_runs_in_milliseconds()
        {
            WithinBudget(() => Newton.Solve(Kepler, KeplerSlope, Math.PI, 1e-14, 50));
            WithinBudget(() => Newton.SolveBracketed(Kepler, KeplerSlope, 0, Math.PI, 1e-14, 100));
        }

        // ---- Scale ----

        [Fact]
        public void Scale_of_a_Kerbin_100_km_circular_orbit_gives_mu_1()
        {
            var r = KerbinR + 100000;
            var scale = Scale.Create(KerbinGm, r);
            Assert.Equal(1, scale.ScaleMu(KerbinGm), 12);
            Assert.Equal(1, scale.ScaleLength(r), 12);
            // The circular speed is the velocity unit, and the period is 2 pi time units.
            var circular = Math.Sqrt(KerbinGm / r);
            Assert.Equal(1, scale.ScaleVelocity(circular), 12);
            Assert.Equal(2 * Math.PI, scale.ScaleTime(2 * Math.PI * Math.Sqrt(r * r * r / KerbinGm)), 9);
            Assert.Equal(1, scale.ScaleAcceleration(KerbinGm / (r * r)), 12);
        }

        [Fact]
        public void Scale_unscales_a_velocity_to_metres_per_second()
        {
            var r = KerbinR + 100000;
            var scale = Scale.Create(KerbinGm, r);
            Assert.Equal(Math.Sqrt(KerbinGm / r), scale.UnscaleVelocity(1), 9);
            Assert.Equal(2246.1, scale.UnscaleVelocity(1), 1);

            // Round trips.
            Assert.Equal(2300.5, scale.UnscaleVelocity(scale.ScaleVelocity(2300.5)), 9);
            Assert.Equal(r, scale.UnscaleLength(scale.ScaleLength(r)), 6);
            Assert.Equal(1234.5, scale.UnscaleTime(scale.ScaleTime(1234.5)), 9);
            Assert.Equal(9.81, scale.UnscaleAcceleration(scale.ScaleAcceleration(9.81)), 12);
        }

        [Fact]
        public void Scale_gives_the_same_scaled_problem_round_any_body()
        {
            var kerbin = Scale.Create(KerbinGm, KerbinR + 100000);
            var mun = Scale.Create(6.5138398e10, 200000 + 210000);
            Assert.Equal(kerbin.ScaleMu(KerbinGm), mun.ScaleMu(6.5138398e10), 12);
        }

        [Fact]
        public void Scale_refuses_a_non_positive_or_non_finite_mu_or_length()
        {
            Assert.Throws<ArgumentException>(() => Scale.Create(0, 700000));
            Assert.Throws<ArgumentException>(() => Scale.Create(-1, 700000));
            Assert.Throws<ArgumentException>(() => Scale.Create(KerbinGm, 0));
            Assert.Throws<ArgumentException>(() => Scale.Create(double.NaN, 700000));
            Assert.Throws<ArgumentException>(() => Scale.Create(KerbinGm, double.PositiveInfinity));
        }
    }
}
