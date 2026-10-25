// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The welcome banner: its text lines, when it shows and which layout fits.</summary>
    public sealed class WelcomeBannerTests
    {
        private static string[] Text(MissionView view, BannerSite site = BannerSite.Pad, bool goalSet = false)
        {
            return WelcomeBanner.Lines(view, "claude-sonnet-5-5", false, 10m, site, goalSet, "0.9.0").Select(l => l.Text).ToArray();
        }

        private static MissionView WithCraft(string name)
        {
            var view = new MissionView();
            view.CraftCheck.CraftName = name;
            return view;
        }

        [Fact]
        public void ArtIsSevenRowsOfAtMost28ColumnsWithNoTrailingSpaces()
        {
            var art = WelcomeBanner.Art.Replace("\r", "");
            var rows = art.Split('\n');
            Assert.Equal(7, rows.Length);
            foreach (var row in rows)
            {
                Assert.True(row.Length <= 28);
                Assert.Equal(row.TrimEnd(), row);
            }
            Assert.All(art.Replace("\n", ""), c => Assert.Contains(c, " ▀▄█"));
            Assert.Equal("▀   ▀██████▀█████▀▀████▀  ▄", rows[5]);
        }

        [Fact]
        public void FreshWindowShowsThePendingSetupAndAsksForAGoal()
        {
            Assert.Equal(new[]
            {
                "Claudia Kermin  v0.9.0",
                "I fly, you clean up",
                "claude-sonnet-5-5 · Crewed",
                "Spending Cap $10.00",
                "Untitled Space Craft on the pad",
                "Describe a Mission Goal to begin.",
            }, Text(WithCraft("Untitled Space Craft")));
        }

        [Fact]
        public void LinesAreStyledTitleSecondaryText()
        {
            var lines = WelcomeBanner.Lines(new MissionView(), "m", true, 5m, BannerSite.None, false, "1.0.0");
            Assert.Equal(BannerTone.Title, lines[0].Tone);
            Assert.Equal(BannerTone.Secondary, lines[1].Tone);
            Assert.Equal(BannerTone.Text, lines[2].Tone);
            Assert.Equal("m · Remote", lines[2].Text);
            Assert.Equal(BannerTone.Secondary, lines.Last().Tone);
        }

        [Fact]
        public void GoalSetBeforeAMissionSaysToCheckTheCraft()
        {
            Assert.Equal("Check the craft, then Start Mission.", Text(new MissionView(), goalSet: true).Last());
        }

        [Fact]
        public void ARunningMissionShowsItsOwnValuesAndOmitsTheLastLine()
        {
            var view = WithCraft("Mun Lander");
            view.Stage = MissionStage.Running;
            view.Model = "claude-opus-5-5";
            view.Remote = true;
            view.SpendingCap = 25.5m;
            Assert.Equal(new[]
            {
                "Claudia Kermin  v0.9.0",
                "I fly, you clean up",
                "claude-opus-5-5 · Remote",
                "Spending Cap $25.50",
                "Mun Lander in the VAB",
            }, Text(view, BannerSite.Vab));
        }

        [Theory]
        [InlineData(BannerSite.Pad, "X on the pad")]
        [InlineData(BannerSite.Vab, "X in the VAB")]
        [InlineData(BannerSite.Sph, "X in the SPH")]
        public void CraftLineNamesTheSite(BannerSite site, string expected)
        {
            Assert.Contains(expected, Text(WithCraft("X"), site));
        }

        [Fact]
        public void NoCraftOrNoSiteOmitsTheCraftLine()
        {
            Assert.Equal(5, Text(WithCraft("")).Length);
            Assert.Equal(5, Text(WithCraft("X"), BannerSite.None).Length);
        }

        [Fact]
        public void BannerShowsOnEveryStageSoItIsFirstAfterEndMissionToo()
        {
            foreach (MissionStage stage in Enum.GetValues(typeof(MissionStage)))
                Assert.True(WelcomeBanner.Shows(stage));
        }

        [Theory]
        [InlineData(65, BannerLayout.Narrow)]
        [InlineData(66, BannerLayout.Wide)]
        [InlineData(120, BannerLayout.Wide)]
        [InlineData(0, BannerLayout.Narrow)]
        public void LayoutIsWideFrom66Columns(int columns, BannerLayout expected)
        {
            Assert.Equal(expected, WelcomeBanner.ChooseLayout(columns));
        }

        [Fact]
        public void ColumnsAreWholeCharactersThatFitAfterPadding()
        {
            Assert.Equal(10, WelcomeBanner.Columns(120, 10, 20));
            Assert.Equal(0, WelcomeBanner.Columns(5, 10, 20));
            Assert.Equal(0, WelcomeBanner.Columns(100, 0, 0));
        }
    }
}
