// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>One stage as the stock delta-v simulator reports it. Mass in tonnes, thrust in kN, delta-v in m/s.</summary>
    public sealed class SnapshotStage
    {
        public int Number;
        /// <summary>Delta-v in vacuum; it does not depend on the simulator's chosen body.</summary>
        public double DeltaVVac;
        public double DeltaVAsl;
        public double ThrustVacKn;
        public double ThrustAslKn;
        public double StartMassT;
        public double EndMassT;
    }

    public sealed class SnapshotCrewMember
    {
        public string Name = "";
        /// <summary>The Kerbal's experience trait, e.g. Pilot or SuperIntelligence.</summary>
        public string Trait = "";
    }

    /// <summary>An experiment part on the craft, and the science still to gain from it.</summary>
    public sealed class SnapshotExperiment
    {
        public uint PartId;
        public string ExperimentId = "";
        public string Title = "";
        /// <summary>Science still available from the subjects started so far; null in Sandbox, where science isn't tracked.</summary>
        public double? ScienceLeft;
    }

    /// <summary>
    /// A body's data from the game, the Delta-v Map's input. Distances in metres, <see cref="Gm"/> in m³/s²,
    /// <see cref="Inclination"/> in degrees.
    /// </summary>
    public sealed class SnapshotBody
    {
        public string Name = "";
        /// <summary>The body this one orbits; "" for the star.</summary>
        public string Parent = "";
        public double Gm;
        public double Radius;
        /// <summary>The top of the atmosphere above the surface; 0 for an airless body.</summary>
        public double AtmosphereHeight;
        /// <summary>The semi-major axis of its orbit round <see cref="Parent"/>.</summary>
        public double Sma;
        public double Inclination;

        /// <summary>Surface gravity in m/s².</summary>
        public double SurfaceGravity
        {
            get { return Radius > 0 ? Gm / (Radius * Radius) : 0; }
        }
    }
}
