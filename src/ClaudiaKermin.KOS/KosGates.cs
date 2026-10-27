// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

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

        /// <summary>The gates' "physics warp allowed" input (ADR 0008): the <c>physics_warp</c> config, read by <see cref="CoastGuard"/>.</summary>
        public static bool PhysicsWarpAllowed = true;

        /// <summary>Why the gates are not in place, or null once they are. The bridge starts no run without them.</summary>
        public static string Failure { get; internal set; }

        static KosGates()
        {
            Failure = "the runtime gates were never installed (is Harmony installed?)";
        }
    }
}
