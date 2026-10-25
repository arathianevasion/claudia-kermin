// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Config;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Missions in a save's folder that are not in play, read back for the Missions list: Paused ones that can be resumed
    /// and finished ones kept as read-only history with their final Debrief and cost. Everything comes from the Mission
    /// folder, which saves never rewind, so the history is the same whatever save is loaded.
    /// </summary>
    internal static class MissionHistory
    {
        /// <summary>The folder name of every Mission folder in the save, newest first (ids start with a UTC timestamp).</summary>
        public static IList<string> Ids(string saveDirectory)
        {
            var missions = Path.Combine(MissionFolder.Root(saveDirectory), "missions");
            if (!Directory.Exists(missions)) return new List<string>();
            return Directory.GetDirectories(missions).Select(Path.GetFileName).OrderByDescending(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>A Mission that is over, from its folder; null when the folder is not a finished Mission.</summary>
        public static MissionListEntry Finished(string saveDirectory, string id, PlayerConfig config)
        {
            var folder = MissionFolder.For(saveDirectory, id);
            var stage = MissionStore.FinishedStage(folder);
            var info = MissionStore.ReadInfo(folder);
            if (!stage.HasValue || info == null) return null;
            var entry = Entry(folder, info, stage.Value == MissionStage.Succeeded ? MissionStatus.Succeeded : stage.Value == MissionStage.Lost ? MissionStatus.Lost : MissionStatus.Ended);
            entry.CanDelete = true;
            var log = MissionLogRebuild.From(MissionStore.ReadTranscript(folder), info.Goal, entry.VesselName, config, MissionStore.ReadNotices(folder));
            entry.FinalDebrief = MissionLogRebuild.FinalDebrief(log);
            // Claudia's death is the mod's own Debrief, which no transcript holds.
            if (entry.Status == MissionStatus.Lost)
                entry.FinalDebrief = new LogEntry
                {
                    Kind = LogKind.Debrief,
                    Title = MissionCards.LostTitle,
                    Text = MissionCards.LostBody(info.Goal, "", entry.Cost, info.SpendingCap, entry.CostUnknown),
                    Why = MissionCards.LostWhy,
                    Outcome = "Failed",
                };
            return entry;
        }

        /// <summary>A Mission a save holds a state for, parked Paused (or one with a folder and no state, which resumes from its folder).</summary>
        public static MissionListEntry Paused(string saveDirectory, string id, MissionSaveState state)
        {
            var folder = MissionFolder.For(saveDirectory, id);
            var info = MissionStore.ReadInfo(folder) ?? new MissionInfo { Id = id, Goal = state != null ? state.Goal : "", SpendingCap = state != null ? state.SpendingCap : 0m };
            var entry = Entry(folder, info, MissionStatus.Paused);
            entry.CanResume = true;
            if (state != null && state.VesselId.Length > 0)
            {
                entry.VesselId = state.VesselId;
                entry.VesselName = state.VesselName;
                entry.CanGoToVessel = true;
            }
            return entry;
        }

        /// <summary>A Mission folder that is not over and that no state in the save accounts for.</summary>
        public static bool IsResumable(string saveDirectory, string id)
        {
            var folder = MissionFolder.For(saveDirectory, id);
            return Directory.Exists(folder) && !MissionStore.IsOver(folder) && MissionStore.ReadInfo(folder) != null;
        }

        private static MissionListEntry Entry(string folder, MissionInfo info, MissionStatus status)
        {
            var cost = MissionStore.ReadCostTotals(folder);
            var entry = new MissionListEntry
            {
                Id = info.Id,
                Goal = info.Goal,
                Status = status,
                Cost = cost.Spent + cost.Held,
                CostUnknown = cost.Unreadable,
                SpendingCap = info.SpendingCap,
            };
            MissionStore.ReadVessel(folder, out entry.VesselId, out entry.VesselName);
            entry.CanGoToVessel = entry.VesselId.Length > 0;
            return entry;
        }

        /// <summary>Removes a finished Mission's folder; false when there is no such Mission or it is not over.</summary>
        public static bool Delete(string saveDirectory, string id)
        {
            // Only a name this save's Mission folders really have: never a path built from the id.
            if (!Ids(saveDirectory).Contains(id)) return false;
            var folder = MissionFolder.For(saveDirectory, id);
            if (!MissionStore.IsOver(folder)) return false;
            Directory.Delete(folder, true);
            return true;
        }
    }
}
