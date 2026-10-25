// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>Where a Mission in the Missions list stands.</summary>
    public enum MissionStatus
    {
        /// <summary>The Mission in play: the one Claudia is flying (or waiting on the player for).</summary>
        Active,
        /// <summary>Stopped, by a switch to another Mission at a sequence boundary or by an error; can be resumed.</summary>
        Paused,
        Succeeded,
        /// <summary>Ended: the player ended it, or a Failed Debrief ended it.</summary>
        Ended,
        Lost,
    }

    /// <summary>One row of the read-only Missions list in the KSC and Tracking Station.</summary>
    public sealed class MissionListEntry
    {
        public string Id = "";
        public string Goal = "";
        public MissionStatus Status;
        /// <summary>The Mission in memory, whatever its status: a finished Mission stays here until a new one starts or it is dismissed.</summary>
        public bool Current;
        /// <summary>What it cost: real usage plus the worst-case charges kept for cancelled and lost requests, USD.</summary>
        public decimal Cost;
        /// <summary>The Mission's cost log could not be read, so what it cost is unknown, not zero.</summary>
        public bool CostUnknown;
        public decimal SpendingCap;
        /// <summary>The Mission's craft (its vessel id and name) as last known; "" when never in Flight.</summary>
        public string VesselId = "";
        public string VesselName = "";
        /// <summary>A finished Mission's final Debrief; null for one that is still going, or that ended without any.</summary>
        public LogEntry FinalDebrief;
        /// <summary>A Paused Mission the player can resume (which pauses the active one at its next sequence boundary).</summary>
        public bool CanResume;
        /// <summary>A finished Mission, which can be deleted (its folder goes with it).</summary>
        public bool CanDelete;
        /// <summary>The Mission has a craft the game still has, so the list can switch to its vessel.</summary>
        public bool CanGoToVessel;

        public bool Finished
        {
            get { return Status == MissionStatus.Succeeded || Status == MissionStatus.Ended || Status == MissionStatus.Lost; }
        }
    }

    /// <summary>
    /// Resume a Paused Mission from the Missions list. The active Mission pauses at its next sequence boundary, never
    /// mid-sequence, and the resumed one takes over. Ignored for a finished Mission.
    /// </summary>
    public sealed class ResumeMission : MissionInput
    {
        public readonly string Id;
        public ResumeMission(string id) { Id = id; }
    }

    /// <summary>
    /// Pause the active Mission at its next sequence boundary, leaving none active, so the start form is offered for a new
    /// one. Starting a Mission while one is active does the same thing by itself.
    /// </summary>
    public sealed class PauseMission : MissionInput
    {
    }

    /// <summary>Delete a finished Mission from the history: its folder (transcript, cost log, scripts) is removed. Ignored for one that is still going.</summary>
    public sealed class DeleteMission : MissionInput
    {
        public readonly string Id;
        public DeleteMission(string id) { Id = id; }
    }

    /// <summary>Switch to the vessel of a Mission in the Missions list (it need not be the active one).</summary>
    public sealed class GoToMissionVessel : MissionInput
    {
        public readonly string Id;
        public GoToMissionVessel(string id) { Id = id; }
    }
}
