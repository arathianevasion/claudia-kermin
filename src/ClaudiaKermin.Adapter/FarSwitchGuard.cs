// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Reflection;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Missions;
using HarmonyLib;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// A far switch while a Maneuver Sequence is Running unloads the craft and cuts its script off, so the player is asked
    /// first (spec story 118). Switching to a vessel that isn't loaded goes through <c>FlightGlobals.setActiveVessel</c>,
    /// which saves and reloads Flight around the new vessel (the map's Switch To, the Alarm Clock's jump, the [ and ] keys
    /// never reach an unloaded one). A prefix holds that switch and shows a dialog; Switch tells the core (the run is
    /// interrupted and Debriefed when the player is back in the craft) and repeats it, so the save it writes already has the
    /// interruption. Nearby switches to loaded vessels, and any switch with nothing Running, pass straight through. The mod
    /// itself never switches vessels.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class FarSwitchGuard : MonoBehaviour
    {
        private static bool patched;
        private static bool switching;
        private static MethodInfo original;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                original = AccessTools.Method(typeof(FlightGlobals), "setActiveVessel", new[] { typeof(Vessel), typeof(bool), typeof(bool) });
                var harmony = new Harmony("claudiakermin.farswitch");
                harmony.Patch(original, prefix: new HarmonyMethod(typeof(FarSwitchGuard), nameof(AskFirst)));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch vessel switching; a far switch while Running won't ask first: " + e);
            }
        }

        // Returning false holds the switch until the player answers.
        private static bool AskFirst(FlightGlobals __instance, Vessel __0, bool __1, bool __2, ref bool __result)
        {
            var driver = MissionDriver.Current;
            var target = __0;
            if (switching || driver == null || target == null || target.loaded || !HighLogic.LoadedSceneIsFlight) return true;
            if (!driver.LeavingFlightNeedsConfirmation || target == FlightGlobals.ActiveVessel) return true;

            var name = target.GetDisplayName();
            var dialog = new MultiOptionDialog(
                "ClaudiaKerminFarSwitch",
                "A Maneuver Sequence is Running. " + name + " is far away: switching to it unloads the craft, so its script is "
                + "cut off and the sequence is marked interrupted. Claudia will Debrief it when you are back in the craft.",
                "Claudia Kermin",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUIHorizontalLayout(
                        new DialogGUIButton("Switch", () =>
                        {
                            driver.Send(new FarSwitchConfirmed(name));
                            switching = true;
                            try
                            {
                                original.Invoke(__instance, new object[] { target, __1, __2 });
                            }
                            finally
                            {
                                switching = false;
                            }
                        }, true),
                        new DialogGUIButton("Stay", () => { }, true)),
                });
            PopupDialog.SpawnPopupDialog(dialog, false, HighLogic.UISkin);
            __result = false;
            return false;
        }
    }
}
