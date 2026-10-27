// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Adapter;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The runtime gates' state, shared by the bridge and <see cref="RuntimeGatePatches"/>. Kept apart from the
    /// patches so the bridge loads (and refuses to start runs) even if Harmony itself is missing.
    /// </summary>
    public static class KosGates
    {
        /// <summary>The processors with a mod-started program running; the bridge opens and closes them.</summary>
        public static readonly GatedRuns Runs = new GatedRuns();

        /// <summary>The gates' "physics warp allowed" input (ADR 0008): the <c>physics_warp</c> config and the Physics warp on Coasts Mission Setting, both on (the Mission driver keeps it).</summary>
        public static bool PhysicsWarpAllowed
        {
            get { return MissionDriver.PhysicsWarpAllowed; }
        }

        /// <summary>Why the gates are not in place, or null once they are. The bridge starts no run without them.</summary>
        public static string Failure { get; internal set; }

        static KosGates()
        {
            Failure = "the runtime gates were never installed (is Harmony installed?)";
        }
    }
}
