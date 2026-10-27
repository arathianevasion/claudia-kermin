// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Tools;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// A body's air for the reentry predictor (#282), from KSP's own curves: pressure, then temperature with the latitude's
    /// day-night mean offset (the sun's place over the fall is not predicted, as the Trajectories mod also averages it), then
    /// stock's density and speed of sound. Main thread; reads only.
    /// </summary>
    public sealed class ReentryAir : IReentryAir
    {
        private readonly CelestialBody _body;

        public ReentryAir(CelestialBody body)
        {
            _body = body;
        }

        public void Air(double altitude, double latitude, out double density, out double speedOfSound)
        {
            density = 0;
            speedOfSound = 0;
            if (!_body.atmosphere || altitude >= _body.atmosphereDepth) return;
            altitude = Math.Max(0, altitude);
            var pressure = _body.GetPressure(altitude);
            if (pressure <= 0) return;
            var abs = (float)Math.Abs(latitude);
            // Stock's offset is the latitude bias plus the sun's share, times how high the sun is (0 to 1): 0.5 over a day.
            var offset = _body.latitudeTemperatureBiasCurve.Evaluate(abs) + _body.latitudeTemperatureSunMultCurve.Evaluate(abs) * 0.5;
            var temperature = _body.GetFullTemperature(altitude, offset);
            density = _body.GetDensity(pressure, temperature);
            speedOfSound = _body.GetSpeedOfSound(pressure, density);
        }
    }

    /// <summary>
    /// A vessel for the reentry predictor (#282), held surface retrograde as <c>reentry</c> flies it: its forward axis points
    /// back along the airflow, so the air comes from below. Each part's drag cubes are read once, with KSP's own
    /// <c>DragCubeList.AddSurfaceDragDirection</c> (as the Trajectories mod does), at a table of Mach numbers; drag between them
    /// is interpolated, times stock's multipliers and pseudo-Reynolds curve. Parts shielded from the airstream add none.
    /// Parts the block will drop (#326, by kOS UID, which is the part's flightID) are left out, mass and drag: a part that
    /// stays faces the air bare where one of them is attached, as it will once they have gone.
    /// Main thread; reads only (the cube data is a local copy).
    /// </summary>
    public sealed class ReentryCraft : IReentryCraft
    {
        private static readonly double[] Machs = MachTable();
        private static readonly Vector3[] Faces = DragCubeDrag.Faces;
        private readonly double[] _area = new double[Machs.Length];
        private readonly double _mass;

        public ReentryCraft(Vessel vessel, ICollection<string> leaveOut = null)
        {
            var gone = new HashSet<string>(leaveOut ?? new string[0]);
            // The way the craft moves through the air when its forward axis points at surface retrograde.
            var flow = -vessel.ReferenceTransform.up;
            if (gone.Count == 0) _mass = vessel.totalMass * 1000;
            foreach (var part in vessel.parts)
            {
                if (Gone(part, gone)) continue;
                if (gone.Count > 0) _mass += (part.mass + part.GetResourceMass()) * 1000;
                if (!DragCubeDrag.MeetsAir(part)) continue;
                var local = part.partTransform.InverseTransformDirection(flow);
                var bared = BaredFaces(part, gone);
                for (var i = 0; i < Machs.Length; i++)
                {
                    _area[i] += bared != null
                        ? DragCubeDrag.Area(part.DragCubes, bared, local, (float)Machs[i])
                        : DragCubeDrag.Area(part, local, (float)Machs[i]);
                }
            }
        }

        private static bool Gone(Part part, HashSet<string> gone)
        {
            return gone.Count > 0 && gone.Contains(part.flightID.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The part's face areas with every face toward an attached part that will be dropped bared (unoccluded), or null when
        /// none is: stock occludes a face by what is attached at its node (DragCubeList.SetPartOcclusion), and that goes.
        /// </summary>
        private static float[] BaredFaces(Part part, HashSet<string> gone)
        {
            float[] areas = null;
            foreach (var node in part.attachNodes)
            {
                if (node == null || node.attachedPart == null || !Gone(node.attachedPart, gone)) continue;
                var direction = node.orientation.normalized;
                for (var f = 0; f < Faces.Length; f++)
                {
                    if (Vector3.Dot(direction, Faces[f]) < 0.7f) continue;
                    if (areas == null) areas = (float[])part.DragCubes.AreaOccluded.Clone();
                    areas[f] = part.DragCubes.WeightedArea[f];
                }
            }
            return areas;
        }

        public double MassKg
        {
            get { return _mass; }
        }

        public double DragArea(double mach, double densitySpeed)
        {
            var area = Interpolate(mach);
            return area * DragCubeDrag.Multiplier * PhysicsGlobals.DragCurvePseudoReynolds.Evaluate((float)densitySpeed);
        }

        private double Interpolate(double mach)
        {
            if (mach <= Machs[0]) return _area[0];
            for (var i = 1; i < Machs.Length; i++)
            {
                if (mach > Machs[i]) continue;
                var t = (mach - Machs[i - 1]) / (Machs[i] - Machs[i - 1]);
                return _area[i - 1] + t * (_area[i] - _area[i - 1]);
            }
            return _area[Machs.Length - 1];
        }

        /// <summary>Mach numbers to read the cubes at: close where stock's curves bend (round Mach 1), wider at hypersonic speeds.</summary>
        private static double[] MachTable()
        {
            var machs = new System.Collections.Generic.List<double>();
            for (var m = 0.0; m < 3; m += 0.05) machs.Add(m);
            for (var m = 3.0; m < 10; m += 0.25) machs.Add(m);
            for (var m = 10.0; m <= 30; m += 1) machs.Add(m);
            return machs.ToArray();
        }
    }
}
