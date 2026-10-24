// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Roster;
using UnityEngine;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>
    /// Present in every game. Keeps Claudia on the crew roster by running the core's RosterPlanner
    /// whenever the roster changes and at least once a second of real time (for the respawn timer).
    /// Idempotent: planning an already-correct roster does nothing.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR, GameScenes.FLIGHT)]
    public class RosterScenario : ScenarioModule
    {
        private const float CheckInterval = 1f;

        private RosterState state = new RosterState();
        private readonly System.Random random = new System.Random();
        private bool dirty = true;
        private bool reverting;
        private float nextCheck;

        public override void OnLoad(ConfigNode node)
        {
            var values = new System.Collections.Generic.Dictionary<string, string>();
            foreach (ConfigNode.Value value in node.values) values[value.name] = value.value;
            state = RosterState.FromValues(values);
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var pair in state.ToValues()) node.AddValue(pair.Key, pair.Value);
        }

        public override void OnAwake()
        {
            base.OnAwake();
            HiringPool.RemoveSuperIntelligence();
            GameEvents.onKerbalAdded.Add(OnKerbalAdded);
            GameEvents.onKerbalAddComplete.Add(OnKerbalChanged);
            GameEvents.onKerbalRemoved.Add(OnKerbalChanged);
            GameEvents.onKerbalNameChanged.Add(OnKerbalNameChanged);
            GameEvents.onKerbalStatusChange.Add(OnKerbalStatusChange);
            GameEvents.OnCrewmemberSacked.Add(OnCrewmemberSacked);
        }

        private void OnDestroy()
        {
            GameEvents.onKerbalAdded.Remove(OnKerbalAdded);
            GameEvents.onKerbalAddComplete.Remove(OnKerbalChanged);
            GameEvents.onKerbalRemoved.Remove(OnKerbalChanged);
            GameEvents.onKerbalNameChanged.Remove(OnKerbalNameChanged);
            GameEvents.onKerbalStatusChange.Remove(OnKerbalStatusChange);
            GameEvents.OnCrewmemberSacked.Remove(OnCrewmemberSacked);
        }

        private void Update()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.CrewRoster == null) return;
            if (!dirty && Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + CheckInterval;
            dirty = false;
            Reconcile();
        }

        private void Reconcile()
        {
            var roster = HighLogic.CurrentGame.CrewRoster;
            var timer = HighLogic.CurrentGame.Parameters.Difficulty.RespawnTimer;
            var actions = RosterPlanner.Plan(RosterGateway.Snapshot(roster), state, Planetarium.GetUniversalTime(), timer, random.Next);
            if (actions.Count == 0) return;

            reverting = true;
            try
            {
                foreach (var action in actions) RosterGateway.Apply(roster, action, OfferRename);
            }
            finally
            {
                reverting = false;
            }
            dirty = true;
        }

        private void OfferRename(ProtoCrewMember kerbal)
        {
            RenameDialog.Show(kerbal, Answered);
        }

        private void Answered(bool declined)
        {
            state.RenameAnswered(declined);
            dirty = true;
        }

        // A Kerbal who has just been added with her role is re-rolled at once; everything else waits for Update.
        private void OnKerbalAdded(ProtoCrewMember kerbal)
        {
            if (RosterGateway.IsClaudia(kerbal) && kerbal.name != ClaudiaIdentity.Name)
            {
                var trait = ClaudiaIdentity.HiringTraits[random.Next(ClaudiaIdentity.HiringTraits.Length)];
                KerbalRoster.SetExperienceTrait(kerbal, trait);
            }
            dirty = true;
        }

        private void OnKerbalChanged(ProtoCrewMember kerbal)
        {
            dirty = true;
        }

        private void OnKerbalNameChanged(ProtoCrewMember kerbal, string oldName, string newName)
        {
            if (!reverting) dirty = true;
        }

        private void OnKerbalStatusChange(ProtoCrewMember kerbal, ProtoCrewMember.RosterStatus from, ProtoCrewMember.RosterStatus to)
        {
            if (RosterGateway.IsClaudia(kerbal)) dirty = true;
        }

        // Backup for the hidden fire button: she is simply added again.
        private void OnCrewmemberSacked(ProtoCrewMember kerbal, int remaining)
        {
            dirty = true;
        }
    }
}
