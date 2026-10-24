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
            var claudia = FindClaudia(kerbals, state);
            if (claudia != null) state.ClaudiaId = claudia.Id;

            KerbalSnapshot hiredClash = null;
            foreach (var kerbal in kerbals)
            {
                if (kerbal == claudia) continue;

                if (kerbal.Trait == ClaudiaIdentity.TraitName)
                {
                    var trait = ClaudiaIdentity.HiringTraits[pick(ClaudiaIdentity.HiringTraits.Length)];
                    actions.Add(new RosterAction(RosterActionType.RerollTrait, kerbal.Id, kerbal.Name, trait));
                }

                if (!ClashesWithClaudia(kerbal.Name)) continue;
                if (kerbal.Kind == KerbalKind.Applicant)
                    actions.Add(new RosterAction(RosterActionType.ReplaceApplicant, kerbal.Id, kerbal.Name));
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
                actions.Add(new RosterAction(RosterActionType.PromptRename, hiredClash.Id, hiredClash.Name));
            }

            if (claudia == null)
            {
                if (mayAdd) actions.Add(new RosterAction(RosterActionType.AddClaudia));
                return actions;
            }

            if (claudia.Name != ClaudiaIdentity.Name)
                actions.Add(new RosterAction(RosterActionType.RevertName, claudia.Id, ClaudiaIdentity.Name));

            PlanRespawn(claudia, state, nowUT, respawnTimer, actions);
            return actions;
        }

        public static bool ClashesWithClaudia(string name)
        {
            return name != null && name.IndexOf(ClaudiaIdentity.ClashFragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static KerbalSnapshot FindClaudia(IList<KerbalSnapshot> kerbals, RosterState state)
        {
            KerbalSnapshot byName = null;
            foreach (var kerbal in kerbals)
            {
                if (state.ClaudiaId.HasValue && kerbal.Id == state.ClaudiaId.Value) return kerbal;
                if (kerbal.Name == ClaudiaIdentity.Name && kerbal.Kind != KerbalKind.Applicant) byName = kerbal;
            }
            return byName;
        }

        private static void PlanRespawn(KerbalSnapshot claudia, RosterState state, double nowUT, double respawnTimer, List<RosterAction> actions)
        {
            switch (claudia.Standing)
            {
                case KerbalStanding.Dead:
                    state.RespawnAtUT = nowUT + respawnTimer;
                    actions.Add(new RosterAction(RosterActionType.SetMissing, claudia.Id, ut: state.RespawnAtUT.Value));
                    break;
                case KerbalStanding.Missing:
                    if (!state.RespawnAtUT.HasValue)
                        state.RespawnAtUT = nowUT + respawnTimer;
                    else if (state.RespawnAtUT.Value <= nowUT)
                    {
                        state.RespawnAtUT = null;
                        actions.Add(new RosterAction(RosterActionType.Respawn, claudia.Id));
                    }
                    break;
                default:
                    state.RespawnAtUT = null;
                    break;
            }
        }
    }
}
