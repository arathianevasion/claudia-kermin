// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>The edges a resize drag moves: one for an edge, two for a corner.</summary>
    [Flags]
    public enum ResizeZone
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        All = Left | Right | Top | Bottom,
    }

    /// <summary>
    /// Resizing a window from any edge or corner. <see cref="Hit"/> says which zone a point is in; <see cref="Resize"/> gives
    /// the rect a drag from that zone produces: the dragged edges follow the pointer and the opposite ones stay put, within
    /// a minimum size and the screen. All in IMGUI pixels, y down.
    /// </summary>
    public static class WindowResize
    {
        /// <summary>
        /// The zone at (<paramref name="x"/>, <paramref name="y"/>) in a <paramref name="width"/> × <paramref name="height"/> area's
        /// own coordinates: an <paramref name="edge"/> band along each side, and a <paramref name="corner"/> square at each corner
        /// where two sides meet. Only <paramref name="allowed"/> edges count (a corner needs both of its edges); a point outside
        /// the area, or in the middle, is <see cref="ResizeZone.None"/>.
        /// </summary>
        public static ResizeZone Hit(float width, float height, float x, float y, float edge, float corner, ResizeZone allowed)
        {
            if (x < 0 || y < 0 || x > width || y > height) return ResizeZone.None;
            // In a small area the two zones of a side never overlap: each gets at most half.
            edge = Math.Min(edge, Math.Min(width, height) / 2);
            corner = Math.Min(corner, Math.Min(width, height) / 2);
            var horizontal = ResizeZone.None;
            var vertical = ResizeZone.None;
            if (Has(allowed, ResizeZone.Left) && x < corner) horizontal = ResizeZone.Left;
            else if (Has(allowed, ResizeZone.Right) && x > width - corner) horizontal = ResizeZone.Right;
            if (Has(allowed, ResizeZone.Top) && y < corner) vertical = ResizeZone.Top;
            else if (Has(allowed, ResizeZone.Bottom) && y > height - corner) vertical = ResizeZone.Bottom;
            if (horizontal != ResizeZone.None && vertical != ResizeZone.None) return horizontal | vertical;

            // Not in a corner square (or one of its edges can't move): only the thin band along a side counts.
            if (Has(allowed, ResizeZone.Left) && x < edge) return ResizeZone.Left;
            if (Has(allowed, ResizeZone.Right) && x > width - edge) return ResizeZone.Right;
            if (Has(allowed, ResizeZone.Top) && y < edge) return ResizeZone.Top;
            if (Has(allowed, ResizeZone.Bottom) && y > height - edge) return ResizeZone.Bottom;
            return ResizeZone.None;
        }

        /// <summary>
        /// <paramref name="start"/> dragged by (<paramref name="dx"/>, <paramref name="dy"/>) from <paramref name="zone"/>. A moved
        /// edge stops at the screen's edge and at the minimum size, and the edge opposite it never moves. A minimum larger than
        /// the screen gives way to the screen.
        /// </summary>
        public static WindowBox Resize(WindowBox start, ResizeZone zone, float dx, float dy, float minWidth, float minHeight, float screenWidth, float screenHeight)
        {
            float left = start.X, right = start.X + start.Width, top = start.Y, bottom = start.Y + start.Height;
            if (Has(zone, ResizeZone.Left)) left = Clamp(start.X + dx, 0, Math.Max(0, right - Math.Min(minWidth, screenWidth)));
            if (Has(zone, ResizeZone.Right)) right = Clamp(right + dx, Math.Min(left + minWidth, screenWidth), screenWidth);
            if (Has(zone, ResizeZone.Top)) top = Clamp(start.Y + dy, 0, Math.Max(0, bottom - Math.Min(minHeight, screenHeight)));
            if (Has(zone, ResizeZone.Bottom)) bottom = Clamp(bottom + dy, Math.Min(top + minHeight, screenHeight), screenHeight);
            return new WindowBox(left, top, right - left, bottom - top);
        }

        private static bool Has(ResizeZone set, ResizeZone flag)
        {
            return (set & flag) != 0;
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
