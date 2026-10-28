// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The one pattern for wall-clock assertions in Core tests (#310, #425); reuse it for every solver's speed budget.
    ///
    /// The whole solution's tests run in parallel and the merge gates run on a busy machine, so a single timed run can be
    /// stretched several-fold by scheduling alone. Load only ever adds time, so the fastest of several runs is the honest
    /// measure of the code: <see cref="Within"/> warms the work up once (JIT, caches), then times it <c>attempts</c> times and
    /// asserts the best against the budget. A real slowdown makes every attempt slow and still fails; a noisy neighbour only
    /// spoils some attempts. The budget is the real one: never raise it to make a test pass, and don't use a mean or median
    /// (a loaded machine moves those). Put the loop inside <c>work</c> when one run is too short to time.
    /// </summary>
    internal static class Timing
    {
        /// <summary>Asserts that the best of <paramref name="attempts"/> timed runs of <paramref name="work"/> takes at most <paramref name="budgetMs"/>.</summary>
        public static void Within(double budgetMs, Action work, int attempts = 7)
        {
            work();
            var best = double.PositiveInfinity;
            for (var i = 0; i < attempts; i++)
            {
                var watch = Stopwatch.StartNew();
                work();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                if (best <= budgetMs) break;
            }
            Assert.True(best <= budgetMs, "best of " + attempts + " runs took " + best.ToString("0.0") + " ms; the budget is " + budgetMs + " ms");
        }
    }
}
