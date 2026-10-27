// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The numbers the turn-time model (<see cref="TurnTime"/>) needs, read from a vessel: the torque its running reaction wheels
    /// (none with no electric charge aboard) can give (stock's <c>ITorqueProvider.GetPotentialTorque</c>, the weaker direction on
    /// each axis); its RCS nozzles, worked into torque as kOS does (<see cref="RcsTorque"/>, kOS #2814: stock's call is broken for
    /// <c>ModuleRCS</c>); whether it has MonoPropellant; and stock's moment of inertia (<c>Vessel.MOI</c>). All are in the
    /// vessel's control frame: x is pitch, z is yaw. Gimbals and control surfaces are left out, as there's no thrust or air on a
    /// Coast. Main thread; reads only.
    /// </summary>
    public static class TurnCraftReader
    {
        public static TurnCraft Read(Vessel vessel)
        {
            var craft = new TurnCraft
            {
                MoiPitch = vessel.MOI.x,
                MoiYaw = vessel.MOI.z,
            };
            // Wheels turn on electric charge: a craft with none left can't use them. RCS fires on MonoPropellant.
            var charge = 0.0;
            var monoprop = 0.0;
            foreach (var part in vessel.parts)
            {
                var ec = part.Resources.Get("ElectricCharge");
                if (ec != null) charge += ec.amount;
                var mp = part.Resources.Get("MonoPropellant");
                if (mp != null) monoprop += mp.amount;
            }
            craft.RcsUsable = monoprop > 0;
            var nozzles = new List<RcsNozzle>();
            foreach (var part in vessel.parts)
            {
                foreach (var module in part.Modules)
                {
                    var wheel = module as ModuleReactionWheel;
                    var rcs = module as ModuleRCS;
                    if (wheel != null && charge > 0 && wheel.isEnabled && wheel.State == ModuleReactionWheel.WheelState.Active)
                    {
                        craft.WheelPitch += Weaker(wheel, 0);
                        craft.WheelYaw += Weaker(wheel, 2);
                    }
                    else if (rcs != null)
                    {
                        AddNozzles(vessel, rcs, nozzles);
                    }
                }
            }
            var torque = RcsTorque.Sum(nozzles);
            craft.RcsPitch = torque.Pitch;
            craft.RcsYaw = torque.Yaw;
            return craft;
        }

        /// <summary>
        /// The module's nozzles, as kOS's <c>CorrectedGetPotentialTorque</c> reads them: a part toggled off in its menu
        /// (<c>rcsEnabled</c>, <c>isEnabled</c>) or shielded counts for nothing. kOS also skips a module while the RCS action group
        /// is off (<c>!rcs_active</c>) or flamed out; here the action group doesn't matter, and propellant is
        /// <see cref="TurnCraft.RcsUsable"/>'s (<c>flameout</c> is only set while a nozzle is asked to fire). The thrust direction
        /// (<c>-forward</c>, or <c>up</c> for an older part) is kOS's as recalled, not checked against its assembly.
        /// </summary>
        private static void AddNozzles(Vessel vessel, ModuleRCS rcs, List<RcsNozzle> nozzles)
        {
            if (rcs.part.ShieldedFromAirstream || !rcs.rcsEnabled || !rcs.isEnabled || rcs.isJustForShow) return;
            var com = vessel.CurrentCoM;
            var frame = vessel.ReferenceTransform;
            var thrust = rcs.thrusterPower * rcs.thrustPercentage * 0.01;
            foreach (var nozzle in rcs.thrusterTransforms)
            {
                if (nozzle == null) continue;
                var at = frame.InverseTransformDirection(nozzle.position - com);
                var dir = frame.InverseTransformDirection(rcs.useZaxis ? -nozzle.forward : nozzle.up);
                nozzles.Add(new RcsNozzle { X = at.x, Y = at.y, Z = at.z, DirX = dir.x, DirY = dir.y, DirZ = dir.z, Thrust = thrust });
            }
        }

        /// <summary>The smaller of the torque one way and the other about <paramref name="axis"/> (kN·m).</summary>
        private static double Weaker(ITorqueProvider provider, int axis)
        {
            Vector3 pos, neg;
            provider.GetPotentialTorque(out pos, out neg);
            return Math.Min(Math.Abs(pos[axis]), Math.Abs(neg[axis]));
        }
    }
}
