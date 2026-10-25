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
                    if (vessel != null && vessel.id.ToString("N") == id) { craft = vessel; break; }
            // An unloaded craft has no parts to steer and no processor running; it can't be made safe from here.
            if (craft == null || !craft.loaded)
            {
                Debug.Log(ModInfo.LogTag + " couldn't make the Mission's craft safe (" + command.Reason + "): vessel " + id +
                          (craft == null ? " not found" : " is not loaded"));
                return;
            }

            // The throttle and SAS below must apply even if the lock release throws.
            try
            {
                var runner = ScriptRunner.Current;
                if (runner != null && command.ProcessorPartId.HasValue) runner.ReleaseControls(command.ProcessorPartId.Value);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't release the kOS locks: " + e.Message);
            }
            MakeSafe(craft);
        }

        /// <summary>The Mission's craft (by id), loaded or not: SAS off, so it can't fight kOS steering on the next run.</summary>
        public static void SasOff(TurnSasOff command)
        {
            var active = FlightGlobals.ActiveVessel;
            var id = command.Target(active != null ? active.id.ToString("N") : null);
            if (id == null || FlightGlobals.Vessels == null) return;
            foreach (var vessel in FlightGlobals.Vessels)
                if (vessel != null && vessel.id.ToString("N") == id)
                {
                    SasOff(vessel, "hand back");
                    return;
                }
            Debug.Log(ModInfo.LogTag + " couldn't turn SAS off (hand back): vessel " + id + " not found");
        }

        /// <summary>SAS off, logged. Every run starts this way: SAS and kOS cooked steering pull against each other.</summary>
        public static void SasOff(Vessel vessel, string why)
        {
            if (vessel == null) return;
            if (!vessel.loaded)
            {
                Debug.Log(ModInfo.LogTag + " couldn't turn SAS off (" + why + "): '" + vessel.vesselName + "' is not loaded");
                return;
            }
            var was = vessel.ActionGroups[KSPActionGroup.SAS];
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Debug.Log(ModInfo.LogTag + " SAS turned off on '" + vessel.vesselName + "' (" + why + "), was " + (was ? "on" : "off"));
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
