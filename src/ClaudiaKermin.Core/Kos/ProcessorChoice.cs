// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>One part of a vessel, as far as choosing a kOS processor goes.</summary>
    public sealed class VesselPart
    {
        /// <summary>The part's <c>persistentId</c>, which survives scene loads (kOS addon instances don't).</summary>
        public uint PersistentId { get; private set; }

        /// <summary>The parent part's <c>persistentId</c>; null for the vessel's root part.</summary>
        public uint? ParentId { get; private set; }

        public bool HasProcessor { get; private set; }

        public VesselPart(uint persistentId, uint? parentId, bool hasProcessor)
        {
            PersistentId = persistentId;
            ParentId = parentId;
            HasProcessor = hasProcessor;
        }
    }

    /// <summary>Which kOS processor Claudia uses on a vessel.</summary>
    public static class ProcessorChoice
    {
        /// <summary>
        /// The player's choice if it is still a processor on the vessel, otherwise the processor fewest
        /// part-to-part steps from the root (ties go to the part listed first). Null if the vessel
        /// has no processor.
        /// </summary>
        public static uint? Choose(IList<VesselPart> parts, uint? preferred)
        {
            var byId = new Dictionary<uint, VesselPart>();
            foreach (var part in parts) byId[part.PersistentId] = part;

            VesselPart chosen;
            if (preferred.HasValue && byId.TryGetValue(preferred.Value, out chosen) && chosen.HasProcessor)
                return chosen.PersistentId;

            uint? best = null;
            var bestDepth = int.MaxValue;
            foreach (var part in parts)
            {
                if (!part.HasProcessor) continue;
                var depth = Depth(part, byId);
                if (depth < bestDepth)
                {
                    best = part.PersistentId;
                    bestDepth = depth;
                }
            }
            return best;
        }

        private static int Depth(VesselPart part, Dictionary<uint, VesselPart> byId)
        {
            var depth = 0;
            // Bounded by the part count, so a malformed snapshot with a cycle can't loop forever.
            while (part.ParentId.HasValue && depth <= byId.Count && byId.TryGetValue(part.ParentId.Value, out part))
                depth++;
            return depth;
        }
    }
}
