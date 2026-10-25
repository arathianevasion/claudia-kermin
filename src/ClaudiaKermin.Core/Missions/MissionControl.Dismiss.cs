// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;

namespace ClaudiaKermin.Core.Missions
{
    public sealed partial class MissionControl
    {
        /// <summary>
        /// Clear on a finished Mission: it stops being the current Mission. Like the tail of End Mission, it marks
        /// the folder ended and clears a leftover Due Time alarm; the folder and transcript stay. Does nothing for an
        /// active Mission or when there is none.
        /// </summary>
        private void DismissRequested()
        {
            var mission = _mission;
            if (mission == null || !MissionOver) return;
            try
            {
                if (!File.Exists(Path.Combine(mission.Folder, MissionFolder.EndedFile)))
                    MissionFolder.WriteEnded(mission.Folder, mission.Id, mission.Stage.ToString());
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The Mission is still dismissed in memory; the folder just lacks the marker.
            }
            DropFinished(mission);
        }

        /// <summary>
        /// A finished Mission stops being the current one, whether dismissed (its folder stays as history) or deleted
        /// from the Missions list (its folder goes): a late reply is cancelled and a leftover Due Time alarm cleared.
        /// </summary>
        private void DropFinished(Mission mission)
        {
            if (mission.InFlight) mission.Pending.Cancel.Cancel();
            if (mission.PendingSequence != null && mission.PendingSequence.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            _mission = null;
        }

        /// <summary>A saved finished Mission the player dismissed: its folder is marked ended, so a load doesn't bring it back.</summary>
        private bool Dismissed(MissionSaveState state)
        {
            if (state.Stage != MissionStage.Succeeded && state.Stage != MissionStage.Ended && state.Stage != MissionStage.Lost) return false;
            return File.Exists(Path.Combine(MissionFolder.For(_saveDirectory, state.Id), MissionFolder.EndedFile));
        }
    }
}
