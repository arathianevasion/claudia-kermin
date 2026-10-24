// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Roster
{
    public enum KerbalKind { Crew, Applicant, Tourist, Unowned }

    public enum KerbalStanding { Available, Assigned, Dead, Missing }

    /// <summary>A plain copy of one Kerbal on the roster, taken by the adapter.</summary>
    public sealed class KerbalSnapshot
    {
        public KerbalSnapshot(uint id, string name, string trait, KerbalKind kind, KerbalStanding standing)
        {
            Id = id;
            Name = name;
            Trait = trait;
            Kind = kind;
            Standing = standing;
        }

        public uint Id { get; private set; }
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
        public RosterAction(RosterActionType type, uint kerbalId = 0, string name = null, string trait = null, double ut = 0)
        {
            Type = type;
            KerbalId = kerbalId;
            Name = name;
            Trait = trait;
            UT = ut;
        }

        public RosterActionType Type { get; private set; }
        public uint KerbalId { get; private set; }

        /// <summary>The Kerbal's current name (PromptRename, ReplaceApplicant) or the name to restore (RevertName).</summary>
        public string Name { get; private set; }

        /// <summary>The new trait (RerollTrait).</summary>
        public string Trait { get; private set; }

        /// <summary>The time she returns (SetMissing).</summary>
        public double UT { get; private set; }
    }

    /// <summary>What the roster scenario module remembers about Claudia in one save.</summary>
    public sealed class RosterState
    {
        private const string IdKey = "claudiaId";
        private const string DeclinedKey = "renameDeclined";
        private const string RespawnKey = "respawnAtUT";

        /// <summary>The persistent id of her roster entry, so a rename can't lose her.</summary>
        public uint? ClaudiaId { get; set; }

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
            if (ClaudiaId.HasValue) values[IdKey] = ClaudiaId.Value.ToString(CultureInfo.InvariantCulture);
            if (RenameDeclined) values[DeclinedKey] = "True";
            if (RespawnAtUT.HasValue) values[RespawnKey] = RespawnAtUT.Value.ToString("R", CultureInfo.InvariantCulture);
            return values;
        }

        public static RosterState FromValues(IDictionary<string, string> values)
        {
            var state = new RosterState();
            string text;
            uint id;
            double ut;
            if (values.TryGetValue(IdKey, out text) && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id))
                state.ClaudiaId = id;
            state.RenameDeclined = values.TryGetValue(DeclinedKey, out text) && text == "True";
            if (values.TryGetValue(RespawnKey, out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ut))
                state.RespawnAtUT = ut;
            return state;
        }
    }
}
