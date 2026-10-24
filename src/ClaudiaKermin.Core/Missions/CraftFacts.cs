// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
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
