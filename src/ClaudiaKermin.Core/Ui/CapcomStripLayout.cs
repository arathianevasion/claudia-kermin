// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>
    /// The Capcom strip's segment track laid out in a track <see cref="Width"/> wide: one segment per sequence of the
    /// Mission Plan, sharing the track equally, but never narrower than a minimum. When they don't fit at that width the
    /// track scrolls sideways; a scroll is how far the track's content is moved left, from 0 to <see cref="MaxScroll"/>.
    /// </summary>
    public struct SegmentTrack
    {
        public readonly int Count;
        public readonly float Width;
        public readonly float Gap;
        public readonly float SegmentWidth;

        public SegmentTrack(int count, float width, float minSegmentWidth, float gap)
        {
            Count = Math.Max(0, count);
            Width = Math.Max(0, width);
            Gap = gap;
            SegmentWidth = Count == 0 ? 0 : Math.Max(minSegmentWidth, (Width - gap * (Count - 1)) / Count);
        }

        /// <summary>All the segments and the gaps between them.</summary>
        public float ContentWidth
        {
            get { return Count == 0 ? 0 : Count * SegmentWidth + (Count - 1) * Gap; }
        }

        public float MaxScroll
        {
            get { return Math.Max(0, ContentWidth - Width); }
        }

        /// <summary>More segments than fit: the track can scroll.</summary>
        public bool Scrolls
        {
            get { return MaxScroll > 0; }
        }

        /// <summary>Where segment <paramref name="index"/> starts, from the start of the content.</summary>
        public float X(int index)
        {
            return index * (SegmentWidth + Gap);
        }

        public float Clamp(float scroll)
        {
            return Math.Min(Math.Max(scroll, 0), MaxScroll);
        }

        /// <summary>The segments any part of which shows at <paramref name="scroll"/>; an empty track has none (Last below First).</summary>
        public VisibleRange Visible(float scroll)
        {
            if (Count == 0) return new VisibleRange(0, -1);
            scroll = Clamp(scroll);
            var first = 0;
            while (first < Count - 1 && X(first) + SegmentWidth <= scroll) first++;
            var last = Count - 1;
            while (last > first && X(last) >= scroll + Width) last--;
            return new VisibleRange(first, last);
        }

        /// <summary>
        /// The scroll nearest <paramref name="scroll"/> that shows the <paramref name="current"/> segment and the next one.
        /// When the track can't hold both, the current one starts the view so the next one peeks in. No current segment
        /// (none yet, or all done) keeps the scroll.
        /// </summary>
        public float ScrollToShow(float scroll, int current)
        {
            scroll = Clamp(scroll);
            if (current < 0 || current >= Count) return scroll;
            var left = X(current);
            var right = X(Math.Min(current + 1, Count - 1)) + SegmentWidth;
            if (right - left > Width) return Clamp(left);
            if (left < scroll) scroll = left;
            if (right > scroll + Width) scroll = right - Width;
            return Clamp(scroll);
        }
    }

    /// <summary>The first and last segment (inclusive) that show.</summary>
    public struct VisibleRange : IEquatable<VisibleRange>
    {
        public readonly int First;
        public readonly int Last;

        public VisibleRange(int first, int last)
        {
            First = first;
            Last = last;
        }

        public bool Equals(VisibleRange other)
        {
            return First == other.First && Last == other.Last;
        }

        public override bool Equals(object obj)
        {
            return obj is VisibleRange && Equals((VisibleRange)obj);
        }

        public override int GetHashCode()
        {
            return First * 397 ^ Last;
        }

        public override string ToString()
        {
            return First + ".." + Last;
        }
    }
}
