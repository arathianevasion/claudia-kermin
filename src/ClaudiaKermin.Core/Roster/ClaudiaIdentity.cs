// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Roster
{
    /// <summary>Who Claudia is on the crew roster: her name, role and fixed starting stats.</summary>
    public static class ClaudiaIdentity
    {
        public const string Name = "Claudia Kermin";

        /// <summary>The name fragment that clashes with hers; matched case-insensitively.</summary>
        public const string ClashFragment = "Claudia";

        /// <summary>The EXPERIENCE_TRAIT name of the Super Intelligence role.</summary>
        public const string TraitName = "SuperIntelligence";

        /// <summary>The traits a re-rolled Kerbal may end up with.</summary>
        public static readonly string[] HiringTraits = { "Pilot", "Engineer", "Scientist" };

        public const float Courage = 1f;
        public const float Stupidity = 0f;
        public const bool IsBadass = true;
        public const bool IsVeteran = false;
    }
}
