// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// A part's drag from its KSP drag cubes, as the Trajectories mod reads it: shared by the reentry predictor (held retrograde)
    /// and the Game Snapshot's nose-first table. Areas are stock's areaDrag, before its multipliers and pseudo-Reynolds curve.
    /// </summary>
    internal static class DragCubeDrag
    {
        /// <summary>A drag cube's faces in stock's order (DragCubeList.faceDirections, internal there).</summary>
        public static readonly Vector3[] Faces = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        /// <summary>Whether the part meets the air at all: not shielded (in a fairing or bay) and with drag cubes.</summary>
        public static bool MeetsAir(Part part)
        {
            return !part.ShieldedFromAirstream && part.DragCubes != null && !part.DragCubes.None;
        }

        /// <summary>The part's areaDrag at <paramref name="mach"/> moving along <paramref name="local"/> (the part's own axes), occluded as stock has it.</summary>
        public static double Area(Part part, Vector3 local, float mach)
        {
            var data = default(DragCubeList.CubeData);
            part.DragCubes.AddSurfaceDragDirection(local, mach, ref data);
            return data.areaDrag;
        }

        /// <summary>Stock's DragCubeList.AddSurfaceDragDirection's areaDrag (decompiled), with the face areas given.</summary>
        public static double Area(DragCubeList cubes, float[] areas, Vector3 direction, float mach)
        {
            double sum = 0;
            for (var f = 0; f < Faces.Length; f++)
            {
                var dot = Vector3.Dot(direction, Faces[f]);
                var area = areas[f] * PhysicsGlobals.DragCurveValue(cubes.SurfaceCurves, (dot + 1) * 0.5f, mach);
                var cd = cubes.WeightedDrag[f];
                sum += area * (cd < 1 ? Mathf.Pow(cubes.DragCurveCd.Evaluate(cd), cubes.DragCurveCdPower.Evaluate(mach)) : cd);
            }
            return sum;
        }

        /// <summary>Stock's multipliers on areaDrag, all but the pseudo-Reynolds curve, which depends on the air.</summary>
        public static double Multiplier
        {
            get { return PhysicsGlobals.DragCubeMultiplier * PhysicsGlobals.DragMultiplier; }
        }
    }

    /// <summary>
    /// The craft's drag flying nose first (#369), for plan_ascent: (Mach, m²) keys over the speeds a launch climbs through,
    /// the whole craft as it stands (launch clamps, which stay on the pad, left out), stock's multipliers included.
    /// Main thread.
    /// </summary>
    public static class NoseDrag
    {
        private static readonly double[] Machs = MachTable();

        /// <summary>
        /// The table for <paramref name="parts"/> with the nose along <paramref name="nose"/> (world); empty if the cubes can't be
        /// read. <paramref name="inEditor"/>: the editor never works out drag-cube weights and occlusion (flight does, every
        /// physics frame), so they are worked out first with stock's own calls; that is the only thing written, and flight
        /// works them out again.
        /// </summary>
        public static List<double[]> Read(IList<Part> parts, Vector3 nose, bool inEditor)
        {
            var table = new List<double[]>();
            try
            {
                if (inEditor) WorkOutCubes(parts);
                var area = new double[Machs.Length];
                foreach (var part in parts)
                {
                    if (!DragCubeDrag.MeetsAir(part) || part.FindModuleImplementing<LaunchClamp>() != null) continue;
                    var local = part.partTransform.InverseTransformDirection(nose);
                    for (var i = 0; i < Machs.Length; i++) area[i] += DragCubeDrag.Area(part, local, (float)Machs[i]);
                }
                var multiplier = DragCubeDrag.Multiplier;
                for (var i = 0; i < Machs.Length; i++) table.Add(new[] { Machs[i], area[i] * multiplier });
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't read the craft's nose-first drag: " + e.Message);
                table.Clear();
            }
            return table;
        }

        // Weights first for every part: a part's occlusion reads the weighted areas of the parts attached to it.
        private static void WorkOutCubes(IList<Part> parts)
        {
            foreach (var part in parts)
            {
                if (part.DragCubes == null || part.DragCubes.None) continue;
                part.DragCubes.SetPart(part);
                part.DragCubes.ForceUpdate(true, true);
                part.DragCubes.SetDragWeights();
            }
            foreach (var part in parts)
            {
                if (part.DragCubes == null || part.DragCubes.None) continue;
                part.DragCubes.SetPartOcclusion();
            }
        }

        /// <summary>Mach numbers to read the cubes at: close round Mach 1, where stock's curves bend, wider above.</summary>
        private static double[] MachTable()
        {
            var machs = new List<double>();
            for (var m = 0; m <= 15; m++) machs.Add(m / 10.0);
            machs.AddRange(new[] { 1.75, 2, 2.5, 3, 4, 5, 6, 8, 10 });
            return machs.ToArray();
        }
    }
}
