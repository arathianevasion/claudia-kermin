// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>Leaves a craft safe after its script is stopped: throttle 0, steering held.</summary>
    public static class CraftSafety
    {
        public static void MakeSafe(Vessel vessel)
        {
            if (vessel == null) return;

            vessel.ctrlState.mainThrottle = 0f;
            // The active vessel takes its throttle from the player's input once kOS lets go.
            if (vessel == FlightGlobals.ActiveVessel) FlightInputHandler.state.mainThrottle = 0f;

            // Steering held: SAS in stability assist keeps the current attitude.
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            var held = vessel.Autopilot.CanSetMode(VesselAutopilot.AutopilotMode.StabilityAssist)
                       && vessel.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            Debug.Log(ModInfo.LogTag + " made '" + vessel.vesselName + "' safe: throttle 0, " +
                      (held ? "SAS stability assist on" : "SAS unavailable, steering not held"));
        }
    }
}
