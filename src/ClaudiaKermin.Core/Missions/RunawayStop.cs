// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The runaway hard stop: a Maneuver Sequence still Running at the runaway multiplier (300% by default,
    /// in the player config) of its Sequence Plan's planned duration, waits included, is aborted and made
    /// safe, and Debriefed as "timed out". It measures game time (UT), so time warp counts and a paused game
    /// doesn't. It never touches the kOS IPU setting.
    /// </summary>
    public static class RunawayStop
    {
        /// <summary>The Debrief reason for a hard stop.</summary>
        public const string Reason = "timed out";

        /// <summary>A plan with no (or a tiny) planned duration is held to this many seconds before the multiplier.</summary>
        public const double MinimumPlannedSeconds = 60;

        /// <summary>The UT at which a run started at <paramref name="startUt"/> is stopped.</summary>
        public static double Deadline(double startUt, double plannedSeconds, double multiplier)
        {
            var planned = double.IsNaN(plannedSeconds) ? 0 : plannedSeconds;
            return startUt + Math.Max(planned, MinimumPlannedSeconds) * multiplier;
        }
    }
}
