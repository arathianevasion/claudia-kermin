// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Resizing a window from an edge or corner: which zone a point is in, and the rect a drag gives.</summary>
    public sealed class WindowResizeTests
    {
        private const float ScreenW = 1000, ScreenH = 800;
        private const float Edge = 6, Corner = 16;
        // A 400 × 300 window at (100, 100); nothing smaller than 200 × 150.
        private static readonly WindowBox Start = new WindowBox(100, 100, 400, 300);

        private static WindowBox Drag(ResizeZone zone, float dx, float dy)
        {
            return WindowResize.Resize(Start, zone, dx, dy, 200, 150, ScreenW, ScreenH);
        }

        private static ResizeZone Hit(float x, float y, ResizeZone allowed = ResizeZone.All)
        {
            return WindowResize.Hit(400, 300, x, y, Edge, Corner, allowed);
        }

        private static void AssertBox(float x, float y, float w, float h, WindowBox box)
        {
            Assert.Equal(new[] { x, y, w, h }, new[] { box.X, box.Y, box.Width, box.Height });
        }

        // ---- Zones ----

        [Fact]
        public void EdgesAreAThinBandInsideTheWindow()
        {
            Assert.Equal(ResizeZone.Left, Hit(2, 150));
            Assert.Equal(ResizeZone.Right, Hit(397, 150));
            Assert.Equal(ResizeZone.Top, Hit(200, 3));
            Assert.Equal(ResizeZone.Bottom, Hit(200, 299));
            Assert.Equal(ResizeZone.None, Hit(200, 150));
            Assert.Equal(ResizeZone.None, Hit(7, 150));
        }

        [Fact]
        public void CornersAreABiggerSquare()
        {
            Assert.Equal(ResizeZone.Left | ResizeZone.Top, Hit(12, 12));
            Assert.Equal(ResizeZone.Right | ResizeZone.Bottom, Hit(390, 290));
            Assert.Equal(ResizeZone.Right | ResizeZone.Top, Hit(390, 2));
            Assert.Equal(ResizeZone.Left | ResizeZone.Bottom, Hit(0, 299));
        }

        [Fact]
        public void OutsideTheWindowIsNoZone()
        {
            Assert.Equal(ResizeZone.None, Hit(-1, 150));
            Assert.Equal(ResizeZone.None, Hit(150, 301));
        }

        [Fact]
        public void InATinyAreaTheFarSideStillResolvesToItsOwnEdge()
        {
            // 20 wide: the right half is the right edge, not the left one.
            Assert.Equal(ResizeZone.Right, WindowResize.Hit(20, 300, 18, 150, Edge, Corner, ResizeZone.All));
            Assert.Equal(ResizeZone.Left, WindowResize.Hit(20, 300, 2, 150, Edge, Corner, ResizeZone.All));
        }

        [Fact]
        public void AnEdgeThatCannotMoveIsNotAZone()
        {
            // A docked panel opening below its strip can't move its top edge.
            var allowed = ResizeZone.Left | ResizeZone.Right | ResizeZone.Bottom;
            Assert.Equal(ResizeZone.None, Hit(200, 2, allowed));
            // Its top corners still work as the side edges they touch.
            Assert.Equal(ResizeZone.Left, Hit(3, 10, allowed));
            Assert.Equal(ResizeZone.None, Hit(10, 10, allowed));
            Assert.Equal(ResizeZone.Left | ResizeZone.Bottom, Hit(10, 290, allowed));
        }

        // ---- Edges and corners: the opposite edge stays put ----

        [Fact]
        public void TheRightEdgeMovesOnlyTheRightEdge()
        {
            AssertBox(100, 100, 450, 300, Drag(ResizeZone.Right, 50, 30));
        }

        [Fact]
        public void TheLeftEdgeMovesTheLeftEdgeAndKeepsTheRightOne()
        {
            AssertBox(60, 100, 440, 300, Drag(ResizeZone.Left, -40, 30));
            AssertBox(150, 100, 350, 300, Drag(ResizeZone.Left, 50, 0));
        }

        [Fact]
        public void TheTopEdgeMovesTheTopEdgeAndKeepsTheBottomOne()
        {
            AssertBox(100, 70, 400, 330, Drag(ResizeZone.Top, 20, -30));
        }

        [Fact]
        public void TheBottomEdgeMovesOnlyTheBottomEdge()
        {
            AssertBox(100, 100, 400, 340, Drag(ResizeZone.Bottom, 20, 40));
        }

        [Fact]
        public void ACornerMovesBothItsEdges()
        {
            AssertBox(80, 90, 420, 310, Drag(ResizeZone.Left | ResizeZone.Top, -20, -10));
            AssertBox(100, 90, 430, 310, Drag(ResizeZone.Right | ResizeZone.Top, 30, -10));
            AssertBox(80, 100, 420, 330, Drag(ResizeZone.Left | ResizeZone.Bottom, -20, 30));
            AssertBox(100, 100, 430, 330, Drag(ResizeZone.Right | ResizeZone.Bottom, 30, 30));
        }

        [Fact]
        public void NoZoneChangesNothing()
        {
            AssertBox(100, 100, 400, 300, Drag(ResizeZone.None, 50, 50));
        }

        // ---- Minimum size ----

        [Fact]
        public void TheMinimumHoldsAndTheFarEdgeStaysPut()
        {
            AssertBox(100, 100, 200, 300, Drag(ResizeZone.Right, -300, 0));
            // Left: the right edge (500) stays, so x stops at 300.
            AssertBox(300, 100, 200, 300, Drag(ResizeZone.Left, 350, 0));
            // Top: the bottom edge (400) stays, so y stops at 250.
            AssertBox(100, 250, 400, 150, Drag(ResizeZone.Top, 0, 200));
            AssertBox(100, 100, 400, 150, Drag(ResizeZone.Bottom, 0, -250));
        }

        // ---- Screen ----

        [Fact]
        public void TheWindowCannotBeDraggedOffScreen()
        {
            AssertBox(0, 100, 500, 300, Drag(ResizeZone.Left, -500, 0));
            AssertBox(100, 0, 400, 400, Drag(ResizeZone.Top, 0, -500));
            AssertBox(100, 100, 900, 300, Drag(ResizeZone.Right, 900, 0));
            AssertBox(100, 100, 400, 700, Drag(ResizeZone.Bottom, 0, 900));
            AssertBox(0, 0, 500, 400, Drag(ResizeZone.Left | ResizeZone.Top, -999, -999));
        }

        [Fact]
        public void AMinimumLargerThanTheScreenYieldsToTheScreen()
        {
            var box = WindowResize.Resize(Start, ResizeZone.Right | ResizeZone.Bottom, 0, 0, 2000, 2000, ScreenW, ScreenH);
            Assert.True(box.X + box.Width <= ScreenW);
            Assert.True(box.Y + box.Height <= ScreenH);
        }
    }
}
