// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class DefaultSizesTests
    {
        [Fact]
        public void At_the_current_scale_the_defaults_are_the_widened_sizes()
        {
            var mission = DefaultSizes.Mission(1.2f, 260, 80);
            Assert.Equal(500, mission.Width);
            Assert.Equal(800, mission.Height);
            var settings = DefaultSizes.Settings(1.2f, 200, 90);
            Assert.Equal(450, settings.Width);
            Assert.Equal(700, settings.Height);
            Assert.Equal(740, DefaultSizes.StripWidth(1.2f));
            Assert.Equal(640, DefaultSizes.PanelHeight(1.2f));
        }

        [Fact]
        public void The_position_passes_through()
        {
            var mission = DefaultSizes.Mission(1.2f, 260, 80);
            Assert.Equal(260, mission.X);
            Assert.Equal(80, mission.Y);
        }

        [Fact]
        public void A_bigger_scale_makes_every_default_bigger()
        {
            Assert.True(DefaultSizes.Mission(1.5f, 0, 0).Width > 500);
            Assert.True(DefaultSizes.Mission(1.5f, 0, 0).Height > 800);
            Assert.True(DefaultSizes.Settings(1.5f, 0, 0).Width > 450);
            Assert.True(DefaultSizes.Settings(1.5f, 0, 0).Height > 700);
            Assert.True(DefaultSizes.StripWidth(1.5f) > 740);
            Assert.True(DefaultSizes.PanelHeight(1.5f) > 640);
        }

        [Fact]
        public void At_scale_one_the_defaults_are_the_design_sizes()
        {
            Assert.Equal(DefaultSizes.StripWidthDesign, DefaultSizes.StripWidth(1f));
            Assert.Equal(DefaultSizes.MissionWidthDesign, DefaultSizes.Mission(1f, 0, 0).Width);
        }

        [Fact]
        public void At_the_current_scale_the_minimums_are_the_old_hand_picked_sizes()
        {
            Assert.Equal(430, DefaultSizes.MissionMin(1.2f).Width);
            Assert.Equal(460, DefaultSizes.MissionMin(1.2f).Height);
            Assert.Equal(450, DefaultSizes.SettingsMin(1.2f).Width);
            Assert.Equal(400, DefaultSizes.SettingsMin(1.2f).Height);
        }

        [Theory]
        [InlineData(0.8f)]
        [InlineData(1f)]
        [InlineData(1.2f)]
        [InlineData(1.5f)]
        public void A_default_is_never_clamped_by_its_minimum(float scale)
        {
            Assert.True(DefaultSizes.Mission(scale, 0, 0).Width >= DefaultSizes.MissionMin(scale).Width);
            Assert.True(DefaultSizes.Mission(scale, 0, 0).Height >= DefaultSizes.MissionMin(scale).Height);
            Assert.True(DefaultSizes.Settings(scale, 0, 0).Width >= DefaultSizes.SettingsMin(scale).Width);
            Assert.True(DefaultSizes.Settings(scale, 0, 0).Height >= DefaultSizes.SettingsMin(scale).Height);
        }

        [Fact]
        public void A_bigger_scale_makes_every_minimum_bigger()
        {
            Assert.True(DefaultSizes.MissionMin(1.5f).Width > 430);
            Assert.True(DefaultSizes.MissionMin(1.5f).Height > 460);
            Assert.True(DefaultSizes.SettingsMin(1.5f).Width > 450);
            Assert.True(DefaultSizes.SettingsMin(1.5f).Height > 400);
        }
    }
}
