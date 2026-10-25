// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class RunEventFilterTests
    {
        [Fact]
        public void The_craft_own_event_is_recorded()
        {
            Assert.True(RunEventFilter.Accepts("craft", "craft", ""));
        }

        [Fact]
        public void Another_vessel_event_is_not_recorded()
        {
            Assert.False(RunEventFilter.Accepts("other", "craft", ""));
        }

        [Fact]
        public void A_run_on_a_Child_Vessel_records_that_vessel_and_not_the_craft()
        {
            Assert.True(RunEventFilter.Accepts("child", "craft", "child"));
            Assert.False(RunEventFilter.Accepts("craft", "craft", "child"));
        }

        [Theory]
        [InlineData("", "craft")]
        [InlineData(null, "craft")]
        [InlineData("craft", "")]
        [InlineData("", "")]
        public void No_known_vessel_or_no_craft_records_nothing(string eventVessel, string craft)
        {
            Assert.False(RunEventFilter.Accepts(eventVessel, craft, ""));
        }
    }
}
