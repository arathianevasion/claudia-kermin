// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The heat maths behind ADDONS:CLAUDIA:HOTTESTPART, on made-up temperatures.</summary>
    public class PartHeatTests
    {
        [Fact]
        public void TheCoreIsAPercentOfItsLimitWhenItIsHotterThanTheSkin()
        {
            Assert.Equal(50, PartHeat.Percent(500, 1000, 100, 2000), 6);
        }

        [Fact]
        public void TheSkinCountsWhenItIsHotterThanTheCore()
        {
            Assert.Equal(80, PartHeat.Percent(500, 1000, 1600, 2000), 6);
        }

        [Fact]
        public void APartWithNoLimitsScoresZero()
        {
            Assert.Equal(0, PartHeat.Percent(500, 0, 100, 0), 6);
            Assert.Equal(0, PartHeat.Percent(500, -1, 100, double.NaN), 6);
        }

        [Fact]
        public void APartWithOnlyASkinLimitScoresByTheSkin()
        {
            Assert.Equal(25, PartHeat.Percent(900, 0, 500, 2000), 6);
        }

        [Fact]
        public void APartPastItsLimitScoresOver100()
        {
            Assert.Equal(120, PartHeat.Percent(1200, 1000, 0, 2000), 6);
        }
    }
}
