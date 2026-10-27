// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The Coast check in flight (ADR 0008): every physics frame while a mod-started program runs, physics warp above 1x
    /// is dropped to 1x if the craft isn't on a Coast, whoever set it. What counts as a Coast is decided in
    /// <see cref="CoastCheck"/>; this class reads the engines and RCS off the vessel. Manual Control is excluded because
    /// the bridge closes a run's gates when it begins, and this only watches gated runs.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CoastGuard : MonoBehaviour
    {
        private void Start()
        {
            KosGates.PhysicsWarpAllowed = ReadConfig();
        }

        /// <summary><c>physics_warp</c> in the player config: the gates' single "physics warp allowed" input (a Mission Setting will feed it later).</summary>
        private static bool ReadConfig()
        {
            try
            {
                return PlayerConfig.LoadOrCreate(ConfigDirectory.ResolveCurrent()).PhysicsWarp;
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't read physics_warp from the player config; physics warp stays on: " + e.Message);
                return true;
            }
        }

        private void FixedUpdate()
        {
            if (!KosGates.Runs.Any || TimeWarp.fetch == null) return;
            if (TimeWarp.WarpMode != TimeWarp.Modes.LOW || TimeWarp.CurrentRateIndex <= 0) return;
            foreach (var processor in KosGates.Runs.Processors)
            {
                Part part;
                if (!FlightGlobals.FindLoadedPart(processor, out part) || part.vessel == null) continue;
                if (!CoastCheck.ShouldDrop(true, OnCoast(part.vessel))) continue;
                TimeWarp.SetRate(0, true);
                Debug.Log(ModInfo.LogTag + " " + CoastCheck.DroppedLog);
                return;
            }
        }

        /// <summary>The vessel is on a Coast now: no engine thrust and no RCS thruster firing.</summary>
        public static bool OnCoast(Vessel vessel)
        {
            var thrusts = new List<double>();
            var rcsFiring = new List<bool>();
            foreach (var part in vessel.Parts)
            {
                foreach (var module in part.Modules)
                {
                    var engine = module as ModuleEngines;
                    if (engine != null) thrusts.Add(engine.finalThrust);
                    var rcs = module as ModuleRCS;
                    if (rcs == null) continue;
                    var firing = false;
                    if (rcs.thrustForces != null)
                        foreach (var force in rcs.thrustForces)
                            if (force > 0) firing = true;
                    rcsFiring.Add(firing);
                }
            }
            return CoastCheck.IsCoast(thrusts, rcsFiring);
        }
    }
}
