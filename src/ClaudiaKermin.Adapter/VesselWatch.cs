// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The vessel events the core needs to keep the Mission's craft (spec stories 116 to 119): after a switch, docking,
    /// undocking, separation, EVA or boarding, the vessels in Flight as plain <see cref="SeenVessel"/>s (at most once a
    /// frame); a docking, with what each side brings; a vessel unloading; and, while the player is in another vessel,
    /// how far the craft is and when it would unload. The core decides what any of it means; this only reads the game.
    /// </summary>
    internal sealed class VesselWatch
    {
        /// <summary>kOS's processor part module, found by name so the adapter needn't reference kOS.</summary>
        private const string KosProcessorModule = "kOSProcessor";

        /// <summary>CommNet links change without a vessel event, so the vessels are reported again this often (real seconds).</summary>
        private const float RefreshSeconds = 1f;

        private readonly Action<MissionInput> _send;
        private bool _dirty = true;
        private float _nextRefresh;

        public VesselWatch(Action<MissionInput> send)
        {
            _send = send;
            GameEvents.onVesselChange.Add(v => _dirty = true);
            GameEvents.onVesselWasModified.Add(v => _dirty = true);
            GameEvents.onVesselCreate.Add(v => _dirty = true);
            GameEvents.onVesselDestroy.Add(v => _dirty = true);
            GameEvents.onPartUndockComplete.Add(p => _dirty = true);
            GameEvents.onCrewOnEva.Add(a => _dirty = true);
            GameEvents.onCrewBoardVessel.Add(a => _dirty = true);
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onVesselUnloaded.Add(OnVesselUnloaded);
            GameEvents.onPartDeCoupleNewVesselComplete.Add((original, made) => OnSplit(original, made, false));
            GameEvents.onVesselsUndocking.Add((original, made) => OnSplit(original, made, true));
        }

        /// <summary>Call each frame: in Flight, reports the vessels once something about them changed.</summary>
        public void Update(bool inFlight)
        {
            if (!inFlight)
            {
                _dirty = true;
                return;
            }
            if (UnityEngine.Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = UnityEngine.Time.unscaledTime + RefreshSeconds;
                _dirty = true;
            }
            if (!_dirty || FlightGlobals.Vessels == null) return;
            _dirty = false;
            var counted = FlightGlobals.Vessels.Where(Counts).ToList();
            var vessels = counted.Select(v => Seen(v, false)).ToList();
            Links(counted, vessels);
            _send(new VesselsSeen(vessels));
        }

        /// <summary>
        /// Each loaded vessel's CommNet paths to the other loaded vessels, for a script sent to a Child Vessel from the craft
        /// Claudia is aboard. With CommNet off every vessel reaches every other.
        /// </summary>
        private static void Links(IList<Vessel> vessels, IList<SeenVessel> seen)
        {
            var commNet = CommNet.CommNetScenario.CommNetEnabled && CommNet.CommNetNetwork.Instance != null ? CommNet.CommNetNetwork.Instance.CommNet : null;
            for (var i = 0; i < vessels.Count; i++)
            {
                for (var j = 0; j < vessels.Count; j++)
                {
                    if (i == j) continue;
                    if (!CommNet.CommNetScenario.CommNetEnabled) seen[i].LinkedTo.Add(seen[j].Id);
                    else if (commNet != null && vessels[i].loaded && vessels[j].loaded && Node(vessels[i]) != null && Node(vessels[j]) != null
                        && commNet.FindPath(Node(vessels[i]), new CommNet.CommPath(), Node(vessels[j])))
                        seen[i].LinkedTo.Add(seen[j].Id);
                }
            }
        }

        private static CommNet.CommNode Node(Vessel vessel)
        {
            return vessel.connection != null ? vessel.connection.Comm : null;
        }

        // Staging or undocking split a vessel in two; both exist now, so each side's parts can be named.
        private void OnSplit(Vessel original, Vessel made, bool undocked)
        {
            _dirty = true;
            if (original == null || made == null || original == made) return;
            _send(new VesselsSeparated(Seen(original, true), Seen(made, true), undocked));
        }

        /// <summary>
        /// Call with each Game Snapshot: with the player in another vessel and the craft still loaded, how far away it is,
        /// where it unloads in its situation, and how fast the gap opens.
        /// </summary>
        public void Range(string craftId)
        {
            var active = FlightGlobals.ActiveVessel;
            if (string.IsNullOrEmpty(craftId) || active == null || Id(active) == craftId) return;
            var craft = FlightGlobals.Vessels.FirstOrDefault(v => v != null && v.loaded && Id(v) == craftId);
            if (craft == null) return;
            var offset = craft.GetWorldPos3D() - active.GetWorldPos3D();
            var distance = offset.magnitude;
            var rate = distance > 0 ? Vector3d.Dot(craft.obt_velocity - active.obt_velocity, offset / distance) : 0;
            var unload = craft.vesselRanges.GetSituationRanges(craft.situation).unload;
            _send(new CraftRange(distance, unload, rate));
        }

        /// <summary>
        /// Call with each Game Snapshot: with the player in another vessel, the craft's link to Kerbin (Blackout), which its
        /// own snapshots carry otherwise. CommNet keeps an unloaded vessel's connection too.
        /// </summary>
        public void Link(string craftId)
        {
            var active = FlightGlobals.ActiveVessel;
            if (string.IsNullOrEmpty(craftId) || active == null || Id(active) == craftId) return;
            var craft = FlightGlobals.Vessels.FirstOrDefault(v => v != null && Id(v) == craftId);
            if (craft != null) _send(new CraftLinkSeen(craftId, SnapshotReader.LinkedHome(craft)));
        }

        // Docking: both vessels still exist, so each side's parts can be named before they merge.
        private void OnPartCouple(GameEvents.FromToAction<Part, Part> action)
        {
            _dirty = true;
            var first = action.from != null ? action.from.vessel : null;
            var second = action.to != null ? action.to.vessel : null;
            if (first == null || second == null || first == second) return;
            _send(new VesselsDocked(Seen(first, true), Seen(second, true)));
        }

        private void OnVesselUnloaded(Vessel vessel)
        {
            _dirty = true;
            if (vessel != null) _send(new VesselUnloaded(Id(vessel)));
        }

        private static string Id(Vessel vessel)
        {
            return vessel.id.ToString("N");
        }

        /// <summary>Flags and asteroids are never a craft.</summary>
        private static bool Counts(Vessel vessel)
        {
            return vessel != null && vessel.vesselType != VesselType.Flag && vessel.vesselType != VesselType.SpaceObject;
        }

        private static SeenVessel Seen(Vessel vessel, bool withParts)
        {
            var seen = new SeenVessel { Id = Id(vessel), Name = vessel.GetDisplayName(), Eva = vessel.isEVA, Situation = vessel.situation.ToString() };
            seen.LinkedHome = !CommNet.CommNetScenario.CommNetEnabled || (vessel.connection != null && vessel.connection.IsConnectedHome);
            List<ProtoCrewMember> crew;
            if (vessel.loaded)
            {
                crew = vessel.GetVesselCrew();
                foreach (var part in vessel.parts)
                {
                    var hasProcessor = part.Modules.Contains(KosProcessorModule);
                    if (hasProcessor) seen.ProcessorPartIds.Add(part.persistentId);
                    seen.Parts.Add(new VesselPart(part.persistentId, part.parent == null ? (uint?)null : part.parent.persistentId, hasProcessor));
                    if (withParts && part.partInfo != null) seen.PartTitles.Add(part.partInfo.title);
                }
            }
            else
            {
                crew = vessel.protoVessel != null ? vessel.protoVessel.GetVesselCrew() : new List<ProtoCrewMember>();
                if (vessel.protoVessel != null)
                    foreach (var part in vessel.protoVessel.protoPartSnapshots)
                    {
                        var hasProcessor = part.modules.Any(m => m.moduleName == KosProcessorModule);
                        if (hasProcessor) seen.ProcessorPartIds.Add(part.persistentId);
                        seen.Parts.Add(new VesselPart(part.persistentId, part.parent == null ? (uint?)null : part.parent.persistentId, hasProcessor));
                    }
            }
            seen.ClaudiaAboard = crew.Any(Roster.RosterGateway.IsClaudia);
            seen.HasKosProcessor = seen.ProcessorPartIds.Count > 0;
            return seen;
        }
    }
}
