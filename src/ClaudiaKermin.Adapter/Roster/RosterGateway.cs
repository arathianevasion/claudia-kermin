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
                snapshots.Add(new KerbalSnapshot(kerbal.name, kerbal.trait, KindOf(kerbal), StandingOf(kerbal)));
            }
            return snapshots;
        }

        public static bool IsClaudia(ProtoCrewMember kerbal)
        {
            return kerbal != null && kerbal.trait == ClaudiaIdentity.TraitName;
        }

        /// <summary>Claudia's standing on this roster as the Game Snapshot names it (Available, Assigned, Missing, Dead); "" when she isn't on it.</summary>
        public static string ClaudiaStanding(KerbalRoster roster)
        {
            var claudia = Find(roster, ClaudiaIdentity.Name);
            return claudia == null ? "" : StandingOf(claudia).ToString();
        }

        /// <summary>
        /// Puts Claudia in the first open seat of the editor's crew manifest. Nobody else is moved: the Kerbals the player
        /// seated stay put and ride as passengers. False when she isn't available or no seat is free.
        /// </summary>
        public static bool SeatInEditor(KerbalRoster roster)
        {
            var claudia = Find(roster, ClaudiaIdentity.Name);
            var manifest = ShipConstruction.ShipManifest;
            if (claudia == null || manifest == null || claudia.rosterStatus != ProtoCrewMember.RosterStatus.Available) return false;
            foreach (var part in manifest.GetCrewableParts())
            {
                var seats = part.GetPartCrew();
                for (var seat = 0; seat < seats.Length; seat++)
                {
                    if (seats[seat] != null) continue;
                    part.AddCrewToSeat(claudia, seat);
                    if (KSP.UI.CrewAssignmentDialog.Instance != null) KSP.UI.CrewAssignmentDialog.Instance.RefreshCrewLists(manifest, true, true);
                    return true;
                }
            }
            return false;
        }

        /// <summary>The Kerbal with this exact name (KSP keeps roster names unique), or null. Never goes by persistentID, which is 0 for most roster Kerbals.</summary>
        public static ProtoCrewMember Find(KerbalRoster roster, string name)
        {
            if (name == null) return null;
            for (var i = 0; i < roster.Count; i++)
                if (roster[i].name == name) return roster[i];
            return null;
        }

        /// <summary>Applies one action; PromptRename goes to <paramref name="onPromptRename"/> since it needs the player.</summary>
        public static void Apply(KerbalRoster roster, RosterAction action, Action<ProtoCrewMember> onPromptRename)
        {
            if (action.Type == RosterActionType.AddClaudia)
            {
                AddClaudia(roster);
                return;
            }

            var kerbal = Find(roster, action.Target);
            if (kerbal == null)
            {
                Debug.LogWarning(ModInfo.LogTag + " skipped " + action.Type + ": no Kerbal named \"" + action.Target + "\" on the roster");
                return;
            }

            switch (action.Type)
            {
                case RosterActionType.RerollTrait:
                    KerbalRoster.SetExperienceTrait(kerbal, action.Trait);
                    break;
                case RosterActionType.ReplaceApplicant:
                    roster.Remove(kerbal);
                    break;
                case RosterActionType.PromptRename:
                    onPromptRename(kerbal);
                    break;
                case RosterActionType.RevertName:
                    kerbal.ChangeName(action.NewName);
                    break;
                case RosterActionType.SetMissing:
                    kerbal.rosterStatus = ProtoCrewMember.RosterStatus.Missing;
                    kerbal.UTaR = action.UT;
                    break;
                case RosterActionType.Respawn:
                    kerbal.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    kerbal.UTaR = 0;
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
