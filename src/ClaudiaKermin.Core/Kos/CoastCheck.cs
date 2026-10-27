// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// The Coast check (GLOSSARY, ADR 0008): a stretch with no engine or RCS thrust. A pure function over what the
    /// addon reads off the craft every physics frame, so it runs without KSP.
    /// </summary>
    public static class CoastCheck
    {
        /// <summary>The line the addon logs when it drops physics warp.</summary>
        public const string DroppedLog = "physics warp dropped: thrust";

        /// <summary>
        /// True when every engine's thrust is 0 and no RCS thruster fires. An engine at throttle 0 has no thrust, so it
        /// is still a Coast; a craft with no engines and no RCS always is.
        /// </summary>
        /// <param name="engineThrusts">Each engine's current thrust, in kN (null: none).</param>
        /// <param name="rcsFiring">Whether each RCS thruster is firing (null: none).</param>
        public static bool IsCoast(IEnumerable<double> engineThrusts, IEnumerable<bool> rcsFiring)
        {
            if (engineThrusts != null)
                foreach (var thrust in engineThrusts)
                    if (thrust > 0) return false;
            if (rcsFiring != null)
                foreach (var firing in rcsFiring)
                    if (firing) return false;
            return true;
        }

        /// <summary>
        /// Whether the addon drops warp to 1× this frame: physics warp is above 1× and the craft is off a Coast. Whoever
        /// set the warp, the player included.
        /// </summary>
        public static bool ShouldDrop(bool physicsWarpAbove1x, bool onCoast)
        {
            return physicsWarpAbove1x && !onCoast;
        }
    }
}
