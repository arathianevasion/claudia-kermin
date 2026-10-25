// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>Decides whether a game event belongs in the Mission's run events: only the Mission craft's own events do.</summary>
    public static class RunEventFilter
    {
        /// <summary>
        /// True when an event from <paramref name="eventVesselId"/> is the Mission craft's. The craft is the vessel the run flies:
        /// the Child Vessel a sequence runs on when there is one, else <see cref="MissionView.CraftVesselId"/>. No craft, or an
        /// event with no known vessel, is never recorded.
        /// </summary>
        public static bool Accepts(string eventVesselId, string craftVesselId, string runningChildVesselId)
        {
            if (string.IsNullOrEmpty(eventVesselId)) return false;
            var target = string.IsNullOrEmpty(runningChildVesselId) ? craftVesselId : runningChildVesselId;
            return !string.IsNullOrEmpty(target) && eventVesselId == target;
        }
    }
}
