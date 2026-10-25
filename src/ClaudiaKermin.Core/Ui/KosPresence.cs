// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>
    /// Whether kOS 1.6 or newer is installed, judged from the main ClaudiaKermin assembly (which loads without kOS).
    /// KSP refuses ClaudiaKermin.KOS.dll when kOS is missing or older and only logs its own warning, so the
    /// adapter says so itself.
    /// </summary>
    public static class KosPresence
    {
        public const int RequiredMajor = 1;
        public const int RequiredMinor = 6;

        /// <summary>What to tell the player about the loaded kOS version (null if none is loaded); "" when it is 1.6 or newer.</summary>
        public static string Check(Version loadedKos)
        {
            var required = RequiredMajor + "." + RequiredMinor;
            if (loadedKos == null)
                return "kOS is not installed: Claudia Kermin needs kOS " + required +
                       " to run scripts, so Claudia won't run them. Install kOS " + required + ".";
            if (loadedKos.Major > RequiredMajor || (loadedKos.Major == RequiredMajor && loadedKos.Minor >= RequiredMinor))
                return "";
            return "kOS " + loadedKos.Major + "." + loadedKos.Minor + " is too old: Claudia Kermin needs kOS " +
                   required + " or newer, so Claudia won't run scripts. Update kOS.";
        }

        /// <summary>The newest version among the entries named "kOS" (case-insensitive), or null if there is none.</summary>
        public static Version Find(IEnumerable<KeyValuePair<string, Version>> loaded)
        {
            Version best = null;
            foreach (var entry in loaded)
            {
                if (!string.Equals(entry.Key, "kOS", StringComparison.OrdinalIgnoreCase) || entry.Value == null) continue;
                if (best == null || entry.Value > best) best = entry.Value;
            }
            return best;
        }
    }
}
