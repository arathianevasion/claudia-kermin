// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Roster
{
    public enum KerbalKind { Crew, Applicant, Tourist, Unowned }

    public enum KerbalStanding { Available, Assigned, Dead, Missing }

    /// <summary>
    /// A plain copy of one Kerbal on the roster, taken by the adapter. It carries no id: KSP leaves a Kerbal's
    /// persistentID at 0 unless the Kerbal is in a part, and roster names are unique, so the name is the address.
    /// </summary>
    public sealed class KerbalSnapshot
    {
        public KerbalSnapshot(string name, string trait, KerbalKind kind, KerbalStanding standing)
        {
            Name = name;
            Trait = trait;
            Kind = kind;
            Standing = standing;
        }

        public string Name { get; private set; }
        public string Trait { get; private set; }
        public KerbalKind Kind { get; private set; }
        public KerbalStanding Standing { get; private set; }
    }

    public enum RosterActionType
    {
        AddClaudia,
        RerollTrait,
        ReplaceApplicant,
        PromptRename,
        RevertName,
        SetMissing,
        Respawn,
    }

    /// <summary>One change the adapter must make to the roster.</summary>
    public sealed class RosterAction
    {
        public RosterAction(RosterActionType type, string target = null, string newName = null, string trait = null, double ut = 0)
        {
            Type = type;
            Target = target;
            NewName = newName;
            Trait = trait;
            UT = ut;
        }

        public RosterActionType Type { get; private set; }

        /// <summary>The current roster name of the Kerbal to change; null for AddClaudia.</summary>
        public string Target { get; private set; }

        /// <summary>The name to give the target (RevertName).</summary>
        public string NewName { get; private set; }

        /// <summary>The new trait (RerollTrait).</summary>
        public string Trait { get; private set; }

        /// <summary>The time she returns (SetMissing).</summary>
        public double UT { get; private set; }
    }

    /// <summary>What the roster scenario module remembers about Claudia in one save. A claudiaId from an older save is ignored.</summary>
    public sealed class RosterState
    {
        private const string DeclinedKey = "renameDeclined";
        private const string RespawnKey = "respawnAtUT";

        /// <summary>The player declined to rename a Kerbal who clashes with her name: she stays off the roster.</summary>
        public bool RenameDeclined { get; set; }

        /// <summary>When she returns from Missing; null when no return is pending.</summary>
        public double? RespawnAtUT { get; set; }

        /// <summary>A rename dialog is open (not saved: closing the game without an answer asks again).</summary>
        public bool PromptPending { get; set; }

        /// <summary>Records the player's answer to the rename dialog.</summary>
        public void RenameAnswered(bool declined)
        {
            PromptPending = false;
            RenameDeclined = declined;
        }

        public Dictionary<string, string> ToValues()
        {
            var values = new Dictionary<string, string>();
            if (RenameDeclined) values[DeclinedKey] = "True";
            if (RespawnAtUT.HasValue) values[RespawnKey] = RespawnAtUT.Value.ToString("R", CultureInfo.InvariantCulture);
            return values;
        }

        public static RosterState FromValues(IDictionary<string, string> values)
        {
            var state = new RosterState();
            string text;
            double ut;
            state.RenameDeclined = values.TryGetValue(DeclinedKey, out text) && text == "True";
            if (values.TryGetValue(RespawnKey, out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ut))
                state.RespawnAtUT = ut;
            return state;
        }
    }
}
