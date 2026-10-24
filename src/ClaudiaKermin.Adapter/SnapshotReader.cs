// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Adapter
{
    /// <summary>Reads a Game Snapshot of a vessel: plain facts for the core. Main thread only.</summary>
    public static class SnapshotReader
    {
        /// <summary>kOS's processor part module, found by name so the adapter needn't reference kOS.</summary>
        private const string KosProcessorModule = "kOSProcessor";

        public static GameSnapshot Read(Vessel vessel)
        {
            var snapshot = new GameSnapshot
            {
                Ut = Planetarium.GetUniversalTime(),
                Situation = vessel.situation.ToString(),
                Body = vessel.mainBody != null ? vessel.mainBody.bodyName : "",
            };
            snapshot.Craft.Name = vessel.vesselName ?? "";
            snapshot.Craft.MassTonnes = vessel.GetTotalMass();
            snapshot.Craft.Stage = vessel.currentStage;

            var resources = new Dictionary<string, SnapshotResource>();
            var order = new List<string>();
            foreach (var part in vessel.parts)
            {
                snapshot.Craft.Parts.Add(new SnapshotPart
                {
                    Id = part.persistentId,
                    ParentId = part.parent != null ? (uint?)part.parent.persistentId : null,
                    Name = part.partInfo != null ? part.partInfo.name : part.name,
                    Title = part.partInfo != null ? part.partInfo.title : "",
                    HasKosProcessor = part.Modules.Contains(KosProcessorModule),
                });
                foreach (var resource in part.Resources)
                {
                    SnapshotResource total;
                    if (!resources.TryGetValue(resource.resourceName, out total))
                    {
                        total = new SnapshotResource { Name = resource.resourceName };
                        resources.Add(resource.resourceName, total);
                        order.Add(resource.resourceName);
                    }
                    total.Amount += resource.amount;
                    total.Max += resource.maxAmount;
                }
            }
            foreach (var name in order) snapshot.Resources.Add(resources[name]);

            var orbit = vessel.orbit;
            if (orbit != null)
            {
                snapshot.Orbit = new SnapshotOrbit
                {
                    Apoapsis = orbit.ApA,
                    Periapsis = orbit.PeA,
                    Inclination = orbit.inclination,
                    Eccentricity = orbit.eccentricity,
                    Period = orbit.period,
                    SemiMajorAxis = orbit.semiMajorAxis,
                };
            }
            return snapshot;
        }
    }
}
