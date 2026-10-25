// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>What the Missions list says about a row, as plain rules the adapter only draws.</summary>
    public static class MissionListRules
    {
        public static StatusChip Status(MissionListEntry entry)
        {
            switch (entry.Status)
            {
                case MissionStatus.Active: return new StatusChip("Active", ChipTone.Info);
                case MissionStatus.Paused: return new StatusChip("Paused", ChipTone.Attention);
                case MissionStatus.Succeeded: return new StatusChip("Succeeded", ChipTone.Success);
                case MissionStatus.Lost: return new StatusChip("Lost", ChipTone.Danger);
                default: return new StatusChip("Ended", ChipTone.Danger);
            }
        }

        /// <summary>"$1.23 of $10.00", the cost against the Spending Cap.</summary>
        public static string Cost(MissionListEntry entry)
        {
            return "$" + entry.Cost.ToString("0.00", CultureInfo.InvariantCulture) + " of $" + entry.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>The line under a finished Mission's goal: its final Debrief's title, or that it ended without one.</summary>
        public static string DebriefTitle(MissionListEntry entry)
        {
            if (!entry.Finished) return "";
            return entry.FinalDebrief != null ? entry.FinalDebrief.Title : "No Debrief";
        }

        /// <summary>The empty list says what the list is for.</summary>
        public const string Empty = "No Missions in this save yet. Start one from the VAB, the SPH or a craft in Flight.";
    }
}
