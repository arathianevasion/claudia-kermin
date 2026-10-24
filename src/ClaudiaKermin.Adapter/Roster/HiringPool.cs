// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Roster;
using UnityEngine;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>Keeps Super Intelligence out of the pool KSP draws new applicants' roles from.</summary>
    public static class HiringPool
    {
        /// <summary>Called by ModuleManager after it has patched every config (found by name, no dependency on it).</summary>
        public static void ModuleManagerPostLoad()
        {
            RemoveSuperIntelligence();
        }

        /// <summary>Idempotent, and safe to call before the configs exist.</summary>
        public static void RemoveSuperIntelligence()
        {
            var configs = GameDatabase.Instance == null ? null : GameDatabase.Instance.ExperienceConfigs;
            if (configs == null || configs.TraitNamesNoTourist == null) return;
            if (configs.TraitNamesNoTourist.RemoveAll(name => name == ClaudiaIdentity.TraitName) > 0)
                Debug.Log(ModInfo.LogTag + " removed " + ClaudiaIdentity.TraitName + " from the hiring pool");
        }
    }

    /// <summary>Second chance at the main menu in case the ModuleManager hook ran before the pool existed.</summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class HiringPoolAtMainMenu : MonoBehaviour
    {
        private void Start()
        {
            HiringPool.RemoveSuperIntelligence();
        }
    }
}
