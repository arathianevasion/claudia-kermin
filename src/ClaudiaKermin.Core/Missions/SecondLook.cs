// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Second look (#272): a reply whose required <c>unsure_because</c> is not empty is discarded and the same turn is
    /// sent again at high effort with the reason added. This holds what is the same everywhere it happens: which reply kinds
    /// get one, the schema field, and the words. Mission Control and the Craft Check decide when.
    /// </summary>
    internal static class SecondLook
    {
        /// <summary>The reply kinds (effort table keys) that get a Second look. The Sequence Plan joins here, once it runs at medium (#287).</summary>
        private static readonly string[] Kinds = { "debrief", "craft_check", "margin_recheck" };

        public const string Effort = "high";

        /// <summary>The Capcom strip's "Claudia is ...".</summary>
        public const string Activity = "taking a second look";

        /// <summary>The schema's property, which every Mission reply schema and the Craft Check schema carry (not nullable: "" is sure).</summary>
        public const string FieldJson =
            "\"unsure_because\":{\"type\":\"string\",\"description\":\"Why you are unsure of this reply, in one short sentence; \\\"\\\" if you are sure.\"}";

        /// <summary>Whether a reply of effort-table kind <paramref name="kind"/>, sent at <paramref name="effort"/>, can get one; a reply already at high has nothing harder to try.</summary>
        public static bool Eligible(string kind, string effort)
        {
            return Kinds.Contains(kind) && effort != Effort;
        }

        /// <summary>The reply's reason for doubt, trimmed; "" when it gave none.</summary>
        public static string Reason(JsonNode reply)
        {
            return (JsonNode.Str(reply["unsure_because"]) ?? "").Trim();
        }

        /// <summary>The line added to the resent turn.</summary>
        public static string Note(string reason)
        {
            return "A first look was unsure because: " + reason.TrimEnd('.', ' ') + ". Think it through carefully.";
        }
    }
}
