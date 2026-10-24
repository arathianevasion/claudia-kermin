// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Roster
{
    /// <summary>
    /// Prepare to uninstall: a save that still has a Kerbal with the Super Intelligence role
    /// breaks once the trait config is gone, so every such Kerbal is switched to Pilot.
    /// </summary>
    public static class UninstallPlanner
    {
        public const string ReplacementTrait = "Pilot";

        public static List<RosterAction> Plan(IList<KerbalSnapshot> kerbals)
        {
            var actions = new List<RosterAction>();
            foreach (var kerbal in kerbals)
                if (kerbal.Trait == ClaudiaIdentity.TraitName)
                    actions.Add(new RosterAction(RosterActionType.RerollTrait, kerbal.Id, kerbal.Name, ReplacementTrait));
            return actions;
        }
    }
}
