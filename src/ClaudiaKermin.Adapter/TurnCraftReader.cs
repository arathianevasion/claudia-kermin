// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The numbers the turn-time model (<see cref="TurnTime"/>) needs, read from a vessel: the torque its running reaction wheels
    /// and its RCS thrusters can give (stock's <c>ITorqueProvider.GetPotentialTorque</c>, the weaker direction on each axis), whether
    /// RCS is on, and stock's moment of inertia (<c>Vessel.MOI</c>). Both are in the vessel's control frame: x is pitch, z is yaw.
    /// Gimbals and control surfaces are left out, as there's no thrust or air on a Coast. Main thread; reads only.
    /// </summary>
    public static class TurnCraftReader
    {
        public static TurnCraft Read(Vessel vessel)
        {
            var craft = new TurnCraft
            {
                RcsOn = vessel.ActionGroups[KSPActionGroup.RCS],
                MoiPitch = vessel.MOI.x,
                MoiYaw = vessel.MOI.z,
            };
            foreach (var part in vessel.parts)
            {
                foreach (var module in part.Modules)
                {
                    var wheel = module as ModuleReactionWheel;
                    var rcs = module as ModuleRCS;
                    if (wheel != null && wheel.isEnabled && wheel.State == ModuleReactionWheel.WheelState.Active)
                    {
                        craft.WheelPitch += Weaker(wheel, 0);
                        craft.WheelYaw += Weaker(wheel, 2);
                    }
                    else if (rcs != null && rcs.isEnabled && rcs.rcsEnabled)
                    {
                        craft.RcsPitch += Weaker(rcs, 0);
                        craft.RcsYaw += Weaker(rcs, 2);
                    }
                }
            }
            return craft;
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
