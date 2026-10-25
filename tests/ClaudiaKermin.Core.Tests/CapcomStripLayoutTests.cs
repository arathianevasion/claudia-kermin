// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Where the Capcom strip's pieces go: the segment track's widths and scroll, and which side the docked panel opens on.</summary>
    public sealed class CapcomStripLayoutTests
    {
        // A 590 px track with 10 px gaps and a 110 px minimum segment width: five segments fit (5 × 110 + 4 × 10 = 590).
        private static SegmentTrack Track(int count)
        {
            return new SegmentTrack(count, 590, 110, 10);
        }

        // ---- Segment track ----

        [Fact]
        public void SegmentsThatFitShareTheTrackEqually()
        {
            var track = Track(3);
            Assert.Equal(190, track.SegmentWidth);
            Assert.False(track.Scrolls);
            Assert.Equal(0, track.MaxScroll);
        }

        [Fact]
        public void MoreSegmentsThanFitKeepTheMinimumWidthAndScroll()
        {
            // 8 × 110 + 7 × 10 = 950 px of segments in 590.
            var track = Track(8);
            Assert.Equal(110, track.SegmentWidth);
            Assert.True(track.Scrolls);
            Assert.Equal(360, track.MaxScroll);
        }

        [Fact]
        public void TheVisibleRangeIsEverySegmentAnyPartOfWhichShows()
        {
            var track = Track(8);
            // Segment i starts at 120 i; scrolled 130 px, the view runs 130..720: segment 1 (120..230) to segment 6 (720..830) is just off.
            var visible = track.Visible(130);
            Assert.Equal(1, visible.First);
            Assert.Equal(5, visible.Last);
            Assert.Equal(new VisibleRange(0, 4), track.Visible(0));
            Assert.Equal(new VisibleRange(3, 7), track.Visible(360));
        }

        [Fact]
        public void AScrollIsKeptInsideTheTrack()
        {
            var track = Track(8);
            Assert.Equal(0, track.Clamp(-50));
            Assert.Equal(360, track.Clamp(1000));
            Assert.Equal(0, Track(3).Clamp(40));
        }

        [Fact]
        public void TheCurrentAndNextSegmentScrollIntoViewFromTheLeft()
        {
            // Segments 5 and 6 run 600..830; the view must end at 830.
            Assert.Equal(240, Track(8).ScrollToShow(0, 5));
        }

        [Fact]
        public void TheCurrentSegmentScrollsIntoViewFromTheRight()
        {
            // Scrolled to the end, going back to segment 1 (120..230) brings its left edge to the view's.
            Assert.Equal(120, Track(8).ScrollToShow(360, 1));
        }

        [Fact]
        public void AScrollThatAlreadyShowsBothIsLeftAlone()
        {
            Assert.Equal(130, Track(8).ScrollToShow(130, 3));
        }

        [Fact]
        public void TheLastSegmentHasNoNextOne()
        {
            Assert.Equal(360, Track(8).ScrollToShow(0, 7));
        }

        [Fact]
        public void ATrackTooNarrowForTwoSegmentsShowsTheCurrentOne()
        {
            // 200 px holds one 110 px segment but not two: segment 4 (480..590) starts the view.
            var track = new SegmentTrack(8, 200, 110, 10);
            Assert.Equal(480, track.ScrollToShow(0, 4));
            Assert.Equal(480, track.ScrollToShow(700, 4));
        }

        [Fact]
        public void NoCurrentSegmentKeepsTheScroll()
        {
            Assert.Equal(130, Track(8).ScrollToShow(130, -1));
            Assert.Equal(360, Track(8).ScrollToShow(900, 8));
        }

        // ---- Placing the strip and its panel ----

        // A 620 px strip 120 high, a 32 px tab, a 640 px panel that can shrink to 200.
        private static readonly StripSize Size = new StripSize(620, 120, 32, 640, 200);

        [Fact]
        public void UnderTheAltimeterThePanelOpensBelow()
        {
            var place = CapcomStripLayout.Place(650, 70, Size, 1920, 1080);
            Assert.Equal(PanelSide.Below, place.Side);
            Assert.Equal(640, place.PanelHeight);
            Assert.Equal(650, place.X);
            Assert.Equal(70, place.Y);
        }

        [Fact]
        public void NearTheBottomThePanelOpensAbove()
        {
            // 128 px below the strip and its tab, 768 above.
            var place = CapcomStripLayout.Place(650, 800, Size, 1920, 1080);
            Assert.Equal(PanelSide.Above, place.Side);
            Assert.Equal(640, place.PanelHeight);
        }

        [Fact]
        public void WithRoomForTheWholePanelOnNeitherSideItTakesTheRoomierOneAndShrinks()
        {
            // 428 px below, 468 above.
            var place = CapcomStripLayout.Place(650, 500, Size, 1920, 1080);
            Assert.Equal(PanelSide.Above, place.Side);
            Assert.Equal(468, place.PanelHeight);
        }

        [Fact]
        public void OnAShortScreenThePanelShrinksBelowTheStrip()
        {
            var place = CapcomStripLayout.Place(650, 70, Size, 1280, 720);
            Assert.Equal(PanelSide.Below, place.Side);
            Assert.Equal(498, place.PanelHeight);
        }

        [Fact]
        public void ThePanelNeverShrinksUnderItsFloor()
        {
            Assert.Equal(200, CapcomStripLayout.Place(650, 70, Size, 1280, 400).PanelHeight);
        }

        [Fact]
        public void ThePanelDoesNotOpenAboveIntoLessRoomThanItsFloor()
        {
            // 120 px below and 168 above on a 480 px screen: more above, but under the 200 px floor, so it stays below.
            var place = CapcomStripLayout.Place(650, 200, Size, 1280, 480);
            Assert.Equal(PanelSide.Below, place.Side);
        }

        [Fact]
        public void TheStripAndItsTabStayOnScreen()
        {
            var place = CapcomStripLayout.Place(-100, -5, Size, 1920, 1080);
            Assert.Equal(0, place.X);
            Assert.Equal(0, place.Y);
            place = CapcomStripLayout.Place(1500, 2000, Size, 1920, 1080);
            Assert.Equal(1300, place.X);
            Assert.Equal(928, place.Y);
            Assert.Equal(PanelSide.Above, place.Side);
        }

        [Fact]
        public void AStripWiderThanTheScreenSticksToItsLeftEdge()
        {
            Assert.Equal(0, CapcomStripLayout.Place(300, 70, Size, 500, 1080).X);
        }

        // ---- Mouse lock ----

        // A 740 x 200 window with a 30 px tab row and a 100 px wide tab, so the tab spans x 320..420.
        [Theory]
        [InlineData(10, 100, true)]   // strip body
        [InlineData(10, 175, false)]  // bottom tab row, left band
        [InlineData(730, 175, false)] // bottom tab row, right band
        [InlineData(370, 175, true)]  // the tab
        [InlineData(319, 175, false)] // just left of the tab
        [InlineData(320, 175, true)]  // tab's left edge
        [InlineData(420, 175, false)] // just right of the tab
        [InlineData(10, 169, true)]   // just above the tab row
        [InlineData(-1, 100, false)]
        [InlineData(10, 200, false)]
        public void BelowTheMouseIsLockedOnlyOverTheBodyAndTheTab(float x, float y, bool locked)
        {
            Assert.Equal(locked, CapcomStripHit.Locks(x, y, 740, 200, PanelSide.Below, 30, 100));
        }

        [Theory]
        [InlineData(10, 15, false)]   // top tab row, left band
        [InlineData(370, 15, true)]   // the tab
        [InlineData(730, 15, false)]  // right band
        [InlineData(10, 30, true)]    // strip body starts under the row
        [InlineData(10, 175, true)]   // bottom edge is body now
        public void AboveTheTabRowIsAtTheTop(float x, float y, bool locked)
        {
            Assert.Equal(locked, CapcomStripHit.Locks(x, y, 740, 200, PanelSide.Above, 30, 100));
        }
    }
}
