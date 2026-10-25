// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using KSP.UI.Screens;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Reads a Game Snapshot: plain facts for the core, from a vessel in Flight or the ship in the editor.
    /// It only reads; the Craft Check's facts (the stock simulator's per-stage figures, the part inventory,
    /// crew, experiments) are computed here and Claude judges them. In particular it never sets the delta-v
    /// app's body or any other delta-v setting. Main thread only.
    /// </summary>
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
                Altitude = vessel.altitude,
                VerticalSpeed = vessel.verticalSpeed,
            };
            snapshot.Craft.Name = vessel.vesselName ?? "";
            snapshot.Craft.VesselId = vessel.id.ToString("N");
            snapshot.Craft.MassTonnes = vessel.GetTotalMass();
            snapshot.Craft.Stage = vessel.currentStage;
            ReadParts(snapshot, vessel.parts, vessel.VesselDeltaV);
            snapshot.Crew.AddRange(vessel.GetVesselCrew().Select(Crew));
            ReadAboard(snapshot);
            // A save with CommNet off counts as linked, whatever the connection says.
            snapshot.HasLink = LinkedHome(vessel);

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

        /// <summary>
        /// A link to Kerbin; CommNet off in the save counts as linked (spec: Manual Control and Blackout). Aboard, this is the
        /// link of the vessel Claudia is in; remote, the probe's control path home to KSC.
        /// </summary>
        public static bool LinkedHome(Vessel vessel)
        {
            if (!CommNet.CommNetScenario.CommNetEnabled) return true;
            return vessel.connection != null && vessel.connection.IsConnectedHome;
        }

        /// <summary>The ship being built in the VAB/SPH; null when there is none or it has no parts yet.</summary>
        public static GameSnapshot ReadEditor(ShipConstruct ship)
        {
            if (ship == null || ship.parts.Count == 0) return null;
            var snapshot = new GameSnapshot
            {
                Ut = Planetarium.GetUniversalTime(),
                Situation = "PRELAUNCH",
                Body = DeltaVMap.DefaultHome,
            };
            snapshot.Craft.Name = ship.shipName ?? "";
            snapshot.Craft.MassTonnes = ship.parts.Sum(p => (double)(p.mass + p.GetResourceMass()));
            snapshot.Craft.Stage = StageManager.LastStage;
            ReadParts(snapshot, ship.parts, ship.vesselDeltaV);
            var manifest = ShipConstruction.ShipManifest;
            if (manifest != null) snapshot.Crew.AddRange(manifest.GetAllCrew(false).Where(c => c != null).Select(Crew));
            ReadAboard(snapshot);
            return snapshot;
        }

        /// <summary>Claudia's roster standing and whether the save has CommNet: the facts the aboard and remote rules judge.</summary>
        private static void ReadAboard(GameSnapshot snapshot)
        {
            var game = HighLogic.CurrentGame;
            if (game != null && game.CrewRoster != null) snapshot.ClaudiaStanding = Roster.RosterGateway.ClaudiaStanding(game.CrewRoster);
            snapshot.CommNetEnabled = CommNet.CommNetScenario.CommNetEnabled;
        }

        private static SnapshotCrewMember Crew(ProtoCrewMember crew)
        {
            return new SnapshotCrewMember
            {
                Name = crew.name ?? "",
                Trait = crew.experienceTrait != null ? crew.experienceTrait.TypeName : "",
            };
        }

        private static void ReadParts(GameSnapshot snapshot, IList<Part> parts, VesselDeltaV deltaV)
        {
            var mode = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode : Game.Modes.SANDBOX;
            snapshot.GameMode = mode.ToString();
            snapshot.DeltaVSimulator = GameSettings.DELTAV_CALCULATIONS_ENABLED;

            var resources = new Dictionary<string, SnapshotResource>();
            var order = new List<string>();
            foreach (var part in parts)
            {
                var info = new SnapshotPart
                {
                    Id = part.persistentId,
                    ParentId = part.parent != null ? (uint?)part.parent.persistentId : null,
                    Name = part.partInfo != null ? part.partInfo.name : part.name,
                    Title = part.partInfo != null ? part.partInfo.title : "",
                    Stage = part.inverseStage,
                    CrewCapacity = part.CrewCapacity,
                };
                if (part.CrewCapacity > 0) info.Tags.Add("crew_seat");
                snapshot.Craft.Seats += part.CrewCapacity;
                foreach (var module in part.Modules) Classify(snapshot, part, info, module);
                snapshot.Craft.Parts.Add(info);

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

            if (snapshot.DeltaVSimulator && deltaV != null) ReadStages(snapshot, deltaV);
            ReadBodies(snapshot);
        }

        /// <summary>The stock simulator's figures per stage. Vacuum delta-v does not depend on the delta-v app's chosen body; nothing is written back.</summary>
        private static void ReadStages(GameSnapshot snapshot, VesselDeltaV deltaV)
        {
            if (deltaV.OperatingStageInfo == null) return;
            foreach (var stage in deltaV.OperatingStageInfo)
            {
                snapshot.Stages.Add(new SnapshotStage
                {
                    Number = stage.stage,
                    DeltaVVac = stage.deltaVinVac,
                    DeltaVAsl = stage.deltaVatASL,
                    ThrustVacKn = stage.thrustVac,
                    ThrustAslKn = stage.thrustASL,
                    StartMassT = stage.startMass,
                    EndMassT = stage.endMass,
                });
            }
        }

        private static void ReadBodies(GameSnapshot snapshot)
        {
            foreach (var body in FlightGlobals.Bodies)
            {
                var orbit = body.orbit;
                snapshot.Bodies.Add(new SnapshotBody
                {
                    Name = body.bodyName,
                    Parent = body.referenceBody != null && body.referenceBody != body ? body.referenceBody.bodyName : "",
                    Gm = body.gravParameter,
                    Radius = body.Radius,
                    AtmosphereHeight = body.atmosphere ? body.atmosphereDepth : 0,
                    Sma = orbit != null && body.referenceBody != body ? orbit.semiMajorAxis : 0,
                    Inclination = orbit != null ? orbit.inclination : 0,
                });
            }
        }

        /// <summary>Tags what a part module makes the part able to do; the names are the stock (and kOS) module classes.</summary>
        private static void Classify(GameSnapshot snapshot, Part part, SnapshotPart info, PartModule module)
        {
            if (Is(module, KosProcessorModule))
            {
                info.HasKosProcessor = true;
                info.Tags.Add("kos_processor");
                info.BootFile = BootFile(module);
            }
            else if (Is(module, "ModuleParachute")) Tag(info, "parachute");
            else if (Is(module, "ModuleWheelBase"))
            {
                var wheel = module as ModuleWheelBase;
                if (wheel != null && wheel.wheelType == WheelType.LEG) Tag(info, "landing_leg");
            }
            else if (Is(module, "ModuleAblator")) Tag(info, "heat_shield");
            else if (Is(module, "ModuleDataTransmitter"))
            {
                Tag(info, "antenna");
                var transmitter = module as ModuleDataTransmitter;
                if (transmitter != null) info.AntennaPower = transmitter.antennaPower;
            }
            else if (Is(module, "ModuleCommand"))
            {
                Tag(info, "command");
                if (part.CrewCapacity == 0) Tag(info, "probe_core");
            }
            else if (Is(module, "ModuleDeployableSolarPanel") || Is(module, "ModuleGenerator") || Is(module, "ModuleResourceConverter"))
                Tag(info, "electric_generator");
            else if (Is(module, "ModuleReactionWheel")) Tag(info, "reaction_wheel");
            else if (Is(module, "ModuleRCS")) Tag(info, "rcs");
            else if (Is(module, "ModuleLiftingSurface")) Tag(info, "aero_stabiliser");
            else if (Is(module, "ModuleGimbal")) Tag(info, "gimbal");
            else if (Is(module, "ModuleEngines")) Tag(info, "engine");
            else if (Is(module, "ModuleScienceExperiment"))
            {
                Tag(info, "experiment");
                var experiment = module as ModuleScienceExperiment;
                if (experiment != null) snapshot.Experiments.Add(ReadExperiment(part, experiment));
            }
        }

        private static void Tag(SnapshotPart info, string tag)
        {
            if (!info.Tags.Contains(tag)) info.Tags.Add(tag);
        }

        /// <summary>The module is, or derives from, the named class.</summary>
        private static bool Is(PartModule module, string className)
        {
            for (var type = module.GetType(); type != null; type = type.BaseType)
                if (type.Name == className) return true;
            return false;
        }

        /// <summary>kOS keeps a processor's boot file in its public <c>bootFile</c> field; "None" means no boot file.</summary>
        private static string BootFile(PartModule processor)
        {
            var field = processor.GetType().GetField("bootFile");
            var value = field != null ? field.GetValue(processor) as string : null;
            return string.IsNullOrEmpty(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        /// <summary>
        /// An experiment part and, outside Sandbox, the science still to gain from the subjects of that
        /// experiment already started. Null means none of it has been collected yet (or Sandbox, which tracks nothing).
        /// </summary>
        private static SnapshotExperiment ReadExperiment(Part part, ModuleScienceExperiment module)
        {
            var experiment = new SnapshotExperiment { PartId = part.persistentId, ExperimentId = module.experimentID ?? "" };
            var definition = ResearchAndDevelopment.GetExperiment(experiment.ExperimentId);
            experiment.Title = definition != null ? definition.experimentTitle : part.partInfo != null ? part.partInfo.title : "";
            if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.Instance != null)
            {
                var prefix = experiment.ExperimentId + "@";
                var subjects = ResearchAndDevelopment.GetSubjects().Where(s => s.id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (subjects.Count > 0) experiment.ScienceLeft = subjects.Sum(s => (double)(s.scienceCap - s.science));
            }
            return experiment;
        }
    }
}
