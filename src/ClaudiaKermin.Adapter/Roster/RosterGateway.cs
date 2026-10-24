// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Roster;
using UnityEngine;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>The only place Claudia's roster rules touch KSP's crew roster: snapshots in, actions out.</summary>
    public static class RosterGateway
    {
        public static List<KerbalSnapshot> Snapshot(KerbalRoster roster)
        {
            var snapshots = new List<KerbalSnapshot>(roster.Count);
            for (var i = 0; i < roster.Count; i++)
            {
                var kerbal = roster[i];
                snapshots.Add(new KerbalSnapshot(kerbal.persistentID, kerbal.name, kerbal.trait, KindOf(kerbal), StandingOf(kerbal)));
            }
            return snapshots;
        }

        public static bool IsClaudia(ProtoCrewMember kerbal)
        {
            return kerbal != null && kerbal.trait == ClaudiaIdentity.TraitName;
        }

        public static ProtoCrewMember Find(KerbalRoster roster, uint id)
        {
            for (var i = 0; i < roster.Count; i++)
                if (roster[i].persistentID == id) return roster[i];
            return null;
        }

        /// <summary>Applies one action. Returns true if it needs the player (a dialog) rather than the roster.</summary>
        public static void Apply(KerbalRoster roster, RosterAction action, Action<ProtoCrewMember> onPromptRename)
        {
            var kerbal = action.KerbalId == 0 ? null : Find(roster, action.KerbalId);
            switch (action.Type)
            {
                case RosterActionType.AddClaudia:
                    AddClaudia(roster);
                    break;
                case RosterActionType.RerollTrait:
                    if (kerbal != null) KerbalRoster.SetExperienceTrait(kerbal, action.Trait);
                    break;
                case RosterActionType.ReplaceApplicant:
                    if (kerbal != null) roster.Remove(kerbal);
                    break;
                case RosterActionType.PromptRename:
                    if (kerbal != null) onPromptRename(kerbal);
                    break;
                case RosterActionType.RevertName:
                    if (kerbal != null) kerbal.ChangeName(action.Name);
                    break;
                case RosterActionType.SetMissing:
                    if (kerbal != null)
                    {
                        kerbal.rosterStatus = ProtoCrewMember.RosterStatus.Missing;
                        kerbal.UTaR = action.UT;
                    }
                    break;
                case RosterActionType.Respawn:
                    if (kerbal != null)
                    {
                        kerbal.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        kerbal.UTaR = 0;
                    }
                    break;
            }
        }

        /// <summary>Adds her straight to the roster, so it costs nothing and ignores the Astronaut Complex limit.</summary>
        private static void AddClaudia(KerbalRoster roster)
        {
            var claudia = new ProtoCrewMember(ProtoCrewMember.KerbalType.Crew, ClaudiaIdentity.Name);
            claudia.gender = ProtoCrewMember.Gender.Female;
            claudia.courage = ClaudiaIdentity.Courage;
            claudia.stupidity = ClaudiaIdentity.Stupidity;
            claudia.isBadass = ClaudiaIdentity.IsBadass;
            claudia.veteran = ClaudiaIdentity.IsVeteran;
            if (!roster.AddCrewMember(claudia))
            {
                Debug.LogWarning(ModInfo.LogTag + " the roster refused " + ClaudiaIdentity.Name);
                return;
            }
            KerbalRoster.SetExperienceTrait(claudia, ClaudiaIdentity.TraitName);
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER) KerbalRoster.SetExperienceLevel(claudia, 0);
            Debug.Log(ModInfo.LogTag + " added " + ClaudiaIdentity.Name + " to the roster");
        }

        private static KerbalKind KindOf(ProtoCrewMember kerbal)
        {
            switch (kerbal.type)
            {
                case ProtoCrewMember.KerbalType.Applicant: return KerbalKind.Applicant;
                case ProtoCrewMember.KerbalType.Tourist: return KerbalKind.Tourist;
                case ProtoCrewMember.KerbalType.Unowned: return KerbalKind.Unowned;
                default: return KerbalKind.Crew;
            }
        }

        private static KerbalStanding StandingOf(ProtoCrewMember kerbal)
        {
            switch (kerbal.rosterStatus)
            {
                case ProtoCrewMember.RosterStatus.Dead: return KerbalStanding.Dead;
                case ProtoCrewMember.RosterStatus.Missing: return KerbalStanding.Missing;
                case ProtoCrewMember.RosterStatus.Assigned: return KerbalStanding.Assigned;
                default: return KerbalStanding.Available;
            }
        }
    }
}
