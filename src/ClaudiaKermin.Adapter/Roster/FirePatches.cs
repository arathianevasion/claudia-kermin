// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Reflection;
using ClaudiaKermin.Core;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>
    /// Stops the player firing Claudia: her X button is hidden in the Astronaut Complex, and a
    /// prefix swallows the click if some other mod shows it anyway. The scenario module's
    /// OnCrewmemberSacked handling is the backup.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class FirePatches : MonoBehaviour
    {
        private static bool patched;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                var harmony = new Harmony("claudiakermin.roster");
                harmony.Patch(AccessTools.Method(typeof(CrewListItem), "SetKerbal"), postfix: new HarmonyMethod(typeof(FirePatches), nameof(HideFireButton)));
                foreach (var name in new[] { "Xbutton_AvailableCrew", "Xbutton_MIA" })
                    harmony.Patch(AccessTools.Method(typeof(AstronautComplex), name), prefix: new HarmonyMethod(typeof(FirePatches), nameof(SwallowFireClick)));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch the Astronaut Complex; firing is only caught after the fact: " + e);
            }
        }

        private static readonly HashSet<int> hidden = new HashSet<int>();

        // Arguments are read from __args because the stock parameter names aren't stable API.
        private static void HideFireButton(CrewListItem __instance, object[] __args)
        {
            if (__instance == null || __instance.button == null) return;
            var id = __instance.GetInstanceID();
            if (RosterGateway.IsClaudia(__args[0] as ProtoCrewMember))
            {
                __instance.button.gameObject.SetActive(false);
                hidden.Add(id);
            }
            else if (hidden.Remove(id))
            {
                // The list recycles its rows: give the button back to the next Kerbal.
                __instance.button.gameObject.SetActive(true);
            }
        }

        private static bool SwallowFireClick(object[] __args)
        {
            var item = __args[1] as CrewListItem;
            return item == null || !RosterGateway.IsClaudia(item.GetCrewRef());
        }
    }
}
