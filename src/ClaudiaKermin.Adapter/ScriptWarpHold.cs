// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The in-script warp hold (ADR 0004): set from the core's <see cref="HoldScriptWarp"/> and read by the kOS addon as
    /// <c>ADDONS:CLAUDIA:WARPHOLD</c>, so a running script's wait stays at 1× while the Mission needs the player and
    /// warps on once it doesn't. The drop itself is a <see cref="DropWarp"/>.
    /// </summary>
    public static class ScriptWarpHold
    {
        /// <summary>Why warp is held at 1×; "" when a script may warp.</summary>
        public static string Reason { get; private set; } = "";

        public static bool Held
        {
            get { return Reason.Length > 0; }
        }

        internal static void Apply(HoldScriptWarp command)
        {
            Reason = command.Reason;
        }
    }
}
