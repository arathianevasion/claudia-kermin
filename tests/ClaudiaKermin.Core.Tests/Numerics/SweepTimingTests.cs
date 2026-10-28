// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>The one wall-clock assertion for the whole seeded sweep (#440), by the pattern #310 and #425 settled.</summary>
    public sealed class SweepTimingTests
    {
        [Fact]
        public void The_whole_sweep_runs_in_seconds()
        {
            // Every seed of every sweep, as the Theories run them. Measured at about 0.5 s alone in a Debug build, so 5 s is ten times that; the budget leaves room for the
            // parallel full-solution run, which Timing.Within's best-of-several is there to ride out.
            Timing.Within(5000, () =>
            {
                foreach (var seed in Sweep.SeedValues)
                {
                    ConicSweepTests.RoundTrips(seed);
                    ConicSweepTests.Anomalies(seed);
                    ShepperdSweepTests.Agrees(seed);
                    LambertSweepTests.Recovers(seed);
                    TrajectorySweepTests.Agrees(seed);
                }
            });
        }
    }
}
