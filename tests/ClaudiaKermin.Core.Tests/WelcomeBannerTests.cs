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
            return WelcomeBanner.Lines(view, "claude-sonnet-5-5", false, site, goalSet, "0.9.0").Select(l => l.Text).ToArray();
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
                "Untitled Space Craft on the pad",
                "Describe a Mission Goal to begin.",
            }, Text(WithCraft("Untitled Space Craft")));
        }

        [Fact]
        public void LinesAreStyledTitleTaglineTextSecondary()
        {
            var lines = WelcomeBanner.Lines(new MissionView(), "m", true, BannerSite.None, false, "1.0.0");
            Assert.Equal(BannerTone.Title, lines[0].Tone);
            Assert.Equal(WelcomeBanner.Tagline, lines[1].Text);
            Assert.Equal(BannerTone.Tagline, lines[1].Tone);
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
            Assert.Equal(new[]
            {
                "Claudia Kermin  v0.9.0",
                "I fly, you clean up",
                "claude-opus-5-5 · Remote",
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
            Assert.Equal(4, Text(WithCraft("")).Length);
            Assert.Equal(4, Text(WithCraft("X"), BannerSite.None).Length);
        }

        [Fact]
        public void BannerShowsOnEveryStageSoItIsFirstAfterEndMissionToo()
        {
            foreach (MissionStage stage in Enum.GetValues(typeof(MissionStage)))
                Assert.True(WelcomeBanner.Shows(stage));
        }

        [Fact]
        public void NoLineMentionsTheSpendingCap()
        {
            Assert.DoesNotContain(Text(WithCraft("X")), t => t.Contains("Spending Cap"));
        }

        [Fact]
        public void ClosingLineIsExactlyWhatTheTicketAsks()
        {
            Assert.Equal("Tell me where you want to go and I might take you there!!", WelcomeBanner.ClosingLine);
        }

        // Art 224 wide, gap 16, widest text line 180: needs 420.
        [Theory]
        [InlineData(420f, BannerLayout.Wide)]
        [InlineData(419.9f, BannerLayout.Narrow)]
        [InlineData(900f, BannerLayout.Wide)]
        [InlineData(0f, BannerLayout.Narrow)]
        public void LayoutIsWideWhenArtGapAndWidestLineFit(float available, BannerLayout expected)
        {
            Assert.Equal(expected, WelcomeBanner.ChooseLayout(available, 224f, 16f, 180f));
        }
    }
}
