// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>Leaves a craft safe after its script is stopped: throttle 0, steering held.</summary>
    public static class CraftSafety
    {
        /// <summary>
        /// The Mission's craft (by id, loaded or not in the scene), wherever the player is: its kOS locks are let go, then
        /// throttle 0 and SAS. With no craft on record, the active vessel.
        /// </summary>
        public static void MakeSafe(MakeCraftSafe command)
        {
            var active = FlightGlobals.ActiveVessel;
            var id = command.Target(active != null ? active.id.ToString("N") : null);
            if (id == null) return;

            Vessel craft = null;
            if (FlightGlobals.Vessels != null)
                foreach (var vessel in FlightGlobals.Vessels)
                    if (vessel != null && vessel.id.ToString("N") == id) craft = vessel;
            if (craft == null)
            {
                Debug.Log(ModInfo.LogTag + " couldn't make the Mission's craft safe (" + command.Reason + "): no vessel " + id);
                return;
            }

            var runner = ScriptRunner.Current;
            if (runner != null && command.ProcessorPartId.HasValue) runner.ReleaseControls(command.ProcessorPartId.Value);
            MakeSafe(craft);
        }

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
