// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Roster
{
    /// <summary>
    /// The roster rules: given a snapshot of the roster, decides what the adapter must change so
    /// Claudia is on it, nobody else has her role or name, and she comes back after dying.
    /// Updates <paramref name="state"/> as it goes; running it again on the result is a no-op.
    /// </summary>
    public static class RosterPlanner
    {
        /// <param name="pick">Returns a number in [0, max); used to choose a replacement trait.</param>
        public static List<RosterAction> Plan(IList<KerbalSnapshot> kerbals, RosterState state, double nowUT, double respawnTimer, Func<int, int> pick)
        {
            var actions = new List<RosterAction>();
            var claudia = FindClaudia(kerbals);

            KerbalSnapshot hiredClash = null;
            foreach (var kerbal in kerbals)
            {
                if (kerbal == claudia) continue;

                if (kerbal.Trait == ClaudiaIdentity.TraitName)
                {
                    var trait = ClaudiaIdentity.HiringTraits[pick(ClaudiaIdentity.HiringTraits.Length)];
                    actions.Add(new RosterAction(RosterActionType.RerollTrait, kerbal.Name, trait: trait));
                }

                if (!ClashesWithClaudia(kerbal.Name)) continue;
                if (kerbal.Kind == KerbalKind.Applicant)
                    actions.Add(new RosterAction(RosterActionType.ReplaceApplicant, kerbal.Name));
                else if (kerbal.Kind == KerbalKind.Crew && hiredClash == null)
                    hiredClash = kerbal;
            }

            // A clash only decides whether she may be added; one already on the roster is still looked after.
            var mayAdd = hiredClash == null;
            if (hiredClash == null)
            {
                state.RenameDeclined = false;
            }
            else if (!state.RenameDeclined && !state.PromptPending)
            {
                state.PromptPending = true;
                actions.Add(new RosterAction(RosterActionType.PromptRename, hiredClash.Name));
            }

            if (claudia == null)
            {
                if (mayAdd) actions.Add(new RosterAction(RosterActionType.AddClaudia));
                return actions;
            }

            if (claudia.Name != ClaudiaIdentity.Name)
                actions.Add(new RosterAction(RosterActionType.RevertName, claudia.Name, ClaudiaIdentity.Name));

            PlanRespawn(claudia, state, nowUT, respawnTimer, actions);
            return actions;
        }

        public static bool ClashesWithClaudia(string name)
        {
            return name != null && name.IndexOf(ClaudiaIdentity.ClashFragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Her trait is unique by construction (out of the hiring pool, re-rolled on anyone else), so it survives a
        // rename; her name is the fallback when a mod changed her trait. Kerbal ids are not saved by KSP: never used.
        private static KerbalSnapshot FindClaudia(IList<KerbalSnapshot> kerbals)
        {
            KerbalSnapshot firstWithTrait = null;
            KerbalSnapshot byName = null;
            foreach (var kerbal in kerbals)
            {
                if (kerbal.Kind == KerbalKind.Applicant) continue;
                if (kerbal.Trait == ClaudiaIdentity.TraitName)
                {
                    if (kerbal.Name == ClaudiaIdentity.Name) return kerbal;
                    if (firstWithTrait == null) firstWithTrait = kerbal;
                }
                else if (kerbal.Name == ClaudiaIdentity.Name && byName == null) byName = kerbal;
            }
            return firstWithTrait ?? byName;
        }

        private static void PlanRespawn(KerbalSnapshot claudia, RosterState state, double nowUT, double respawnTimer, List<RosterAction> actions)
        {
            switch (claudia.Standing)
            {
                case KerbalStanding.Dead:
                    state.RespawnAtUT = nowUT + respawnTimer;
                    actions.Add(new RosterAction(RosterActionType.SetMissing, claudia.Name, ut: state.RespawnAtUT.Value));
                    break;
                case KerbalStanding.Missing:
                    if (!state.RespawnAtUT.HasValue)
                        state.RespawnAtUT = nowUT + respawnTimer;
                    else if (state.RespawnAtUT.Value <= nowUT)
                    {
                        state.RespawnAtUT = null;
                        actions.Add(new RosterAction(RosterActionType.Respawn, claudia.Name));
                    }
                    break;
                default:
                    state.RespawnAtUT = null;
                    break;
            }
        }
    }
}
