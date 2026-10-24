// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Roster
{
    /// <summary>Which role checks Claudia's Super Intelligence role satisfies, for the stock-check postfixes.</summary>
    public static class RoleRules
    {
        /// <summary>Does a Kerbal with <paramref name="trait"/> count as <paramref name="role"/>?</summary>
        public static bool CountsAs(string trait, string role)
        {
            if (trait == null || role == null) return false;
            if (trait == ClaudiaIdentity.TraitName) return Array.IndexOf(ClaudiaIdentity.HiringTraits, role) >= 0;
            return trait == role;
        }

        /// <summary>
        /// How many of the crew with these traits a stock check for <paramref name="role"/> missed:
        /// the Super Intelligence ones, since stock matches the trait name exactly.
        /// </summary>
        public static int ExtraCount(string role, IEnumerable<string> crewTraits)
        {
            var extra = 0;
            foreach (var trait in crewTraits)
                if (trait == ClaudiaIdentity.TraitName && CountsAs(trait, role)) extra++;
            return extra;
        }
    }
}
