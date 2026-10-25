// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>What the Claudia Kermin section of Settings shows. The API key is always reachable, so a Pause that says to fix the key can be fixed in game.</summary>
    public static class SettingsRules
    {
        /// <summary>The API key is shown in every context, including during a Mission.</summary>
        public static bool ShowApiKey(bool duringMission)
        {
            return true;
        }

        /// <summary>Prepare to uninstall and the debug chat stay out of the way while a Mission is active.</summary>
        public static bool ShowUninstall(bool duringMission)
        {
            return !duringMission;
        }

        /// <summary>The section's subtitle, naming only what it holds in this context.</summary>
        public static string Subtitle(bool duringMission)
        {
            return duringMission ? "API key" : "API key · Prepare to uninstall";
        }
    }
}
