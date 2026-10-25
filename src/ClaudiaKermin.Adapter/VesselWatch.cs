// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
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

        private readonly Action<MissionInput> _send;
        private bool _dirty = true;

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
        }

        /// <summary>Call each frame: in Flight, reports the vessels once something about them changed.</summary>
        public void Update(bool inFlight)
        {
            if (!inFlight)
            {
                _dirty = true;
                return;
            }
            if (!_dirty || FlightGlobals.Vessels == null) return;
            _dirty = false;
            var vessels = FlightGlobals.Vessels.Where(Counts).Select(v => Seen(v, false)).ToList();
            _send(new VesselsSeen(vessels));
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
            var seen = new SeenVessel { Id = Id(vessel), Name = vessel.GetDisplayName(), Eva = vessel.isEVA };
            List<ProtoCrewMember> crew;
            if (vessel.loaded)
            {
                crew = vessel.GetVesselCrew();
                foreach (var part in vessel.parts)
                {
                    if (part.Modules.Contains(KosProcessorModule)) seen.ProcessorPartIds.Add(part.persistentId);
                    if (withParts && part.partInfo != null) seen.PartTitles.Add(part.partInfo.title);
                }
            }
            else
            {
                crew = vessel.protoVessel != null ? vessel.protoVessel.GetVesselCrew() : new List<ProtoCrewMember>();
                if (vessel.protoVessel != null)
                    foreach (var part in vessel.protoVessel.protoPartSnapshots)
                        if (part.modules.Any(m => m.moduleName == KosProcessorModule)) seen.ProcessorPartIds.Add(part.persistentId);
            }
            seen.ClaudiaAboard = crew.Any(Roster.RosterGateway.IsClaudia);
            seen.HasKosProcessor = seen.ProcessorPartIds.Count > 0;
            return seen;
        }
    }
}
