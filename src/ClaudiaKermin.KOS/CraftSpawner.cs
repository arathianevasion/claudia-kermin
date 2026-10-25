// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Puts a <c>.craft</c> into a circular orbit as a new vessel in the current game, for the debug
    /// block test runner. The parts are built from the craft file (so missing module data takes the
    /// part's defaults), snapshotted to PART nodes and added as a ProtoVessel: the way Contract
    /// Configurator spawns craft files.
    /// </summary>
    internal static class CraftSpawner
    {
        public static ProtoVessel IntoOrbit(string craftPath, string bodyName, double altitude, double inclination, double? periapsis = null)
        {
            var game = HighLogic.CurrentGame;
            var body = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == bodyName);
            if (body == null) throw new InvalidOperationException("no body named " + bodyName);

            var ship = ShipConstruction.LoadShip(craftPath);
            if (ship == null || ship.parts.Count == 0) throw new InvalidOperationException("KSP wouldn't load " + craftPath);

            var missionId = (uint)Guid.NewGuid().GetHashCode();
            var launchId = game.launchID++;
            foreach (var part in ship.parts)
            {
                part.flightID = ShipConstruction.GetUniqueFlightID(game.flightState);
                part.missionID = missionId;
                part.launchID = launchId;
                part.flagURL = game.flagURL;
                part.temperature = 1.0;
            }

            // Snapshot the parts through a throwaway vessel, then let them go.
            var holder = new GameObject("claudia block test spawn");
            var dummyVessel = holder.AddComponent<Vessel>();
            dummyVessel.parts = ship.parts;
            var dummyProto = new ProtoVessel(new ConfigNode(), null) { vesselRef = dummyVessel };
            foreach (var part in ship.parts) dummyProto.protoPartSnapshots.Add(new ProtoPartSnapshot(part, dummyProto));
            foreach (var snapshot in dummyProto.protoPartSnapshots) snapshot.storePartRefs();
            var partNodes = dummyProto.protoPartSnapshots.Select(snapshot =>
            {
                var node = new ConfigNode("PART");
                snapshot.Save(node);
                return node;
            }).ToArray();
            foreach (var part in ship.parts) UnityEngine.Object.Destroy(part.gameObject);
            UnityEngine.Object.Destroy(holder);

            // Circular at altitude; or, with a periapsis, an orbit whose apoapsis is altitude, started at the apoapsis.
            var apRadius = body.Radius + altitude;
            var peRadius = periapsis.HasValue ? body.Radius + periapsis.Value : apRadius;
            var orbit = periapsis.HasValue
                ? new Orbit(inclination, (apRadius - peRadius) / (apRadius + peRadius), (apRadius + peRadius) / 2, 0, 0, Math.PI, Planetarium.GetUniversalTime(), body)
                : new Orbit(inclination, 0, apRadius, 0, 0, 0, Planetarium.GetUniversalTime(), body);
            var discovery = ProtoVessel.CreateDiscoveryNode(DiscoveryLevels.Owned, UntrackedObjectClass.A, double.PositiveInfinity, double.PositiveInfinity);
            var vesselNode = ProtoVessel.CreateVesselNode(ship.shipName, VesselType.Probe, orbit, 0, partNodes, discovery);
            return game.AddVessel(vesselNode);
        }
    }
}
