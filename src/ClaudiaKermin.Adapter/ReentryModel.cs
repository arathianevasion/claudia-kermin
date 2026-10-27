// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;

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
    /// Main thread; reads only (the cube data is a local copy).
    /// </summary>
    public sealed class ReentryCraft : IReentryCraft
    {
        private static readonly double[] Machs = MachTable();
        private readonly double[] _area = new double[Machs.Length];
        private readonly double _mass;

        public ReentryCraft(Vessel vessel)
        {
            _mass = vessel.totalMass * 1000;
            // The way the craft moves through the air when its forward axis points at surface retrograde.
            var flow = -vessel.ReferenceTransform.up;
            foreach (var part in vessel.parts)
            {
                if (part.ShieldedFromAirstream || part.DragCubes == null || part.DragCubes.None) continue;
                var local = part.partTransform.InverseTransformDirection(flow);
                for (var i = 0; i < Machs.Length; i++)
                {
                    var data = default(DragCubeList.CubeData);
                    part.DragCubes.AddSurfaceDragDirection(local, (float)Machs[i], ref data);
                    _area[i] += data.areaDrag;
                }
            }
        }

        public double MassKg
        {
            get { return _mass; }
        }

        public double DragArea(double mach, double densitySpeed)
        {
            var area = Interpolate(mach);
            return area * PhysicsGlobals.DragCubeMultiplier * PhysicsGlobals.DragMultiplier
                * PhysicsGlobals.DragCurvePseudoReynolds.Evaluate((float)densitySpeed);
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
