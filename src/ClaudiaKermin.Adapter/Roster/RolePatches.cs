// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Roster;
using FinePrint.Utilities;
using HarmonyLib;
using UnityEngine;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>
    /// Makes Claudia count as a Pilot, Engineer and Scientist where stock matches the trait name:
    /// contracts, EVA welding and the construction combine bonus all go through these three
    /// FinePrint methods. Postfixes only add her; the decision itself is the core's RoleRules.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RolePatches : MonoBehaviour
    {
        private static bool patched;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                var harmony = new Harmony("claudiakermin.roles");
                harmony.Patch(AccessTools.Method(typeof(VesselUtilities), nameof(VesselUtilities.VesselCrewWithTraitCount)), postfix: new HarmonyMethod(typeof(RolePatches), nameof(CountPostfix)));
                harmony.Patch(AccessTools.Method(typeof(VesselUtilities), nameof(VesselUtilities.VesselCrewWithTrait)), postfix: new HarmonyMethod(typeof(RolePatches), nameof(ListPostfix)));
                harmony.Patch(AccessTools.Method(typeof(VesselUtilities), nameof(VesselUtilities.CrewTraitMissionAvailability)), postfix: new HarmonyMethod(typeof(RolePatches), nameof(AvailabilityPostfix)));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch the role checks; Claudia will not count as Pilot, Engineer or Scientist: " + e);
            }
        }

        // int VesselCrewWithTraitCount(string trait, Vessel v)
        private static void CountPostfix(string trait, Vessel v, ref int __result)
        {
            var crew = CrewOf(v);
            if (crew == null) return;
            var traits = new List<string>(crew.Count);
            foreach (var kerbal in crew) traits.Add(kerbal.trait);
            __result += RoleRules.ExtraCount(trait, traits);
        }

        // List<ProtoCrewMember> VesselCrewWithTrait(string trait, Vessel v)
        private static void ListPostfix(string trait, Vessel v, ref List<ProtoCrewMember> __result)
        {
            var crew = CrewOf(v);
            if (crew == null) return;
            foreach (var kerbal in crew)
            {
                if (kerbal.trait != ClaudiaIdentity.TraitName || !RoleRules.CountsAs(kerbal.trait, trait)) continue;
                if (__result == null) __result = new List<ProtoCrewMember>();
                if (!__result.Contains(kerbal)) __result.Add(kerbal);
            }
        }

        // int CrewTraitMissionAvailability(string trait, CelestialBody targetBody, Vessel excludeVessel)
        private static void AvailabilityPostfix(string trait, ref int __result)
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.CrewRoster == null) return;
            var traits = new List<string>();
            foreach (var kerbal in game.CrewRoster.Crew)
                if (kerbal.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                    traits.Add(kerbal.trait);
            __result += RoleRules.ExtraCount(trait, traits);
        }

        private static List<ProtoCrewMember> CrewOf(Vessel v)
        {
            return v == null ? null : v.GetVesselCrew();
        }
    }
}
