// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The remote Mission's Blocker the mod counts for itself (#126): a craft Claudia flies from Mission Control needs a probe
    /// core to command it and an antenna that can reach home. Claude is still told the rule, but doesn't have to notice it.
    /// </summary>
    internal static class RemoteBlocker
    {
        public const string Code = "no_probe_core_or_antenna";

        public const string Fix = "Add a probe core and an antenna (one with a transmitter, so the craft can reach Kerbin). "
            + "A crewed Mission with Claudia aboard needs neither.";

        /// <summary>What the craft lacks for a remote Mission, as a sentence; "" when it has what it needs. Facts are the adapter's part tags and antenna power.</summary>
        public static string Missing(GameSnapshot snapshot)
        {
            var parts = snapshot.Craft.Parts;
            var lacks = new List<string>();
            if (!parts.Any(p => p.Tags.Contains("probe_core"))) lacks.Add("no probe core");
            // With CommNet off every craft counts as linked, so no antenna is needed to reach home.
            if (snapshot.CommNetEnabled && !parts.Any(p => p.Tags.Contains("antenna") && p.AntennaPower > 0))
                lacks.Add("no antenna with a transmitter to reach home");
            return lacks.Count == 0 ? "" : "Mod check: the craft has " + string.Join(" and ", lacks.ToArray()) + ".";
        }
    }
}
