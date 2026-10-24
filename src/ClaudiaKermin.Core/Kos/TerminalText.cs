// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>Reads a kOS terminal buffer: its lines are NUL-padded and it scrolls.</summary>
    public static class TerminalText
    {
        /// <summary>The most lines of terminal tail kept as error text.</summary>
        public const int TailLines = 15;

        /// <summary>
        /// A buffer line as text. kOS pads each line with NULs, and Unity's log cuts a message off at
        /// the first NUL, so they become spaces and trailing space is dropped.
        /// </summary>
        public static string CleanLine(string raw)
        {
            return raw == null ? "" : raw.Replace('\0', ' ').TrimEnd();
        }

        /// <summary>
        /// The non-blank lines printed since <paramref name="before"/> was read, at most
        /// <see cref="TailLines"/> of the latest. The buffer only scrolls up, so the new lines follow
        /// the longest overlap of the end of <paramref name="before"/> with the start of
        /// <paramref name="after"/>; after a clear screen there is no overlap and every line is new.
        /// </summary>
        public static IList<string> LinesSince(IList<string> before, IList<string> after)
        {
            var old = before.Select(CleanLine).ToList();
            while (old.Count > 0 && old[old.Count - 1].Length == 0) old.RemoveAt(old.Count - 1);
            var now = after.Select(CleanLine).ToList();

            var offset = 0;
            while (offset < old.Count && !Overlaps(old, offset, now)) offset++;
            var firstNew = old.Count - offset;

            var lines = now.Skip(firstNew).Where(l => l.Length > 0).ToList();
            return lines.Skip(System.Math.Max(0, lines.Count - TailLines)).ToList();
        }

        private static bool Overlaps(List<string> old, int offset, List<string> now)
        {
            var length = old.Count - offset;
            if (length > now.Count) return false;
            for (var i = 0; i < length; i++)
                if (old[offset + i] != now[i]) return false;
            return true;
        }
    }
}
