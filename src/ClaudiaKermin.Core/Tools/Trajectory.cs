// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>How a patch ends.</summary>
    internal enum PatchEnd
    {
        /// <summary>A closed orbit with nothing in its way: it goes round again.</summary>
        Stable,
        /// <summary>It leaves the body's sphere of influence for its parent's.</summary>
        Escape,
        /// <summary>It enters a moon's (or planet's) sphere of influence: <see cref="Patch.Next"/>.</summary>
        Encounter,
        /// <summary>It reaches the ground of an airless body.</summary>
        Impact,
        /// <summary>It reaches the top of the atmosphere; the impulsive model has no drag, so nothing after is predicted.</summary>
        Atmosphere,
    }

    /// <summary>One conic of a patched-conic trajectory: the orbit round one body between two transitions.</summary>
    internal sealed class Patch
    {
        public SnapshotBody Body;
        public Conic Orbit;
        public double StartUt;
        /// <summary>When it ends; one period after the start for a stable orbit, as stock has it, and infinity for a hyperbola round the star.</summary>
        public double EndUt;
        public PatchEnd End;
        /// <summary>The body whose sphere of influence it enters, on an encounter.</summary>
        public SnapshotBody Next;
        /// <summary>Why the search for this patch's end gave up (an expected failure, not a bug), or null; the patch is then the last of its path.</summary>
        public string Error;
    }

    /// <summary>
    /// Patched conics over the Game Snapshot's body table, in Core so they are tested without the game: the same model as the
    /// stock map view (a two-body conic per sphere of influence, switching at the SOI edge), worked on detached orbits, never the
    /// live vessel's. The bodies move on their own orbits from the table's elements.
    /// </summary>
    internal sealed class BodySystem
    {
        /// <summary>The shortest step of the encounter search, s; and the most steps one patch may take before the search gives up.</summary>
        private const double MinStep = 1;
        private const int MaxSteps = 200000;

        private readonly int _maxSteps = MaxSteps;
        private readonly List<SnapshotBody> _bodies;
        private readonly Dictionary<string, Conic> _orbits = new Dictionary<string, Conic>(StringComparer.OrdinalIgnoreCase);

        public BodySystem(IEnumerable<SnapshotBody> bodies)
        {
            _bodies = bodies.ToList();
        }

        /// <summary>A system whose encounter search gives up after <paramref name="maxSteps"/> steps, so a test can make it.</summary>
        internal BodySystem(IEnumerable<SnapshotBody> bodies, int maxSteps) : this(bodies)
        {
            _maxSteps = maxSteps;
        }

        public SnapshotBody Find(string name)
        {
            return _bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public SnapshotBody Parent(SnapshotBody body)
        {
            return string.IsNullOrEmpty(body.Parent) ? null : Find(body.Parent);
        }

        private IEnumerable<SnapshotBody> Children(SnapshotBody body)
        {
            return _bodies.Where(b => string.Equals(b.Parent, body.Name, StringComparison.OrdinalIgnoreCase) && b.SoiRadius > 0 && b.Sma > 0);
        }

        /// <summary>A body's orbit round its parent, from the table's elements.</summary>
        public Conic OrbitOf(SnapshotBody body)
        {
            Conic orbit;
            if (_orbits.TryGetValue(body.Name, out orbit)) return orbit;
            var parent = Parent(body);
            orbit = Conic.FromElements(parent.Gm, body.Sma, body.Eccentricity, body.Inclination, body.Lan, body.ArgumentOfPeriapsis,
                body.MeanAnomalyAtEpoch, body.Epoch);
            _orbits[body.Name] = orbit;
            return orbit;
        }

        /// <summary>
        /// The trajectory from <paramref name="orbit"/> round <paramref name="body"/>, starting at <paramref name="startUt"/>, up to
        /// <paramref name="maxPatches"/> patches; it stops early at a stable orbit, an impact or the atmosphere.
        /// </summary>
        public List<Patch> Follow(SnapshotBody body, Conic orbit, double startUt, int maxPatches)
        {
            var patches = new List<Patch>();
            SnapshotBody left = null;
            while (patches.Count < maxPatches)
            {
                var patch = Next(body, orbit, startUt, left);
                patches.Add(patch);
                Vec3 r, v;
                patch.Orbit.State(patch.EndUt, out r, out v);
                if (patch.End == PatchEnd.Escape)
                {
                    var parent = Parent(body);
                    Vec3 br, bv;
                    OrbitOf(body).State(patch.EndUt, out br, out bv);
                    left = body;
                    body = parent;
                    orbit = Conic.FromState(parent.Gm, r + br, v + bv, patch.EndUt);
                }
                else if (patch.End == PatchEnd.Encounter)
                {
                    Vec3 br, bv;
                    OrbitOf(patch.Next).State(patch.EndUt, out br, out bv);
                    left = null;
                    body = patch.Next;
                    orbit = Conic.FromState(body.Gm, r - br, v - bv, patch.EndUt);
                }
                else break;
                startUt = patch.EndUt;
            }
            return patches;
        }

        /// <summary>One patch: where the orbit first meets the atmosphere or ground, the SOI edge, or a moon's SOI.</summary>
        private Patch Next(SnapshotBody body, Conic orbit, double startUt, SnapshotBody justLeft)
        {
            var patch = new Patch { Body = body, Orbit = orbit, StartUt = startUt, End = PatchEnd.Stable };
            var floor = body.Radius + body.AtmosphereHeight;
            var down = orbit.PeR < floor ? orbit.TimeAtRadius(floor, startUt, false) : null;
            var escape = body.SoiRadius > 0 && orbit.ApR > body.SoiRadius && Parent(body) != null ? orbit.TimeAtRadius(body.SoiRadius, startUt, true) : null;
            var horizon = Math.Min(down ?? double.PositiveInfinity, escape ?? double.PositiveInfinity);
            if (orbit.Closed) horizon = Math.Min(horizon, startUt + orbit.Period);

            SnapshotBody hit;
            string error;
            var encounter = FirstEncounter(body, orbit, startUt, horizon, justLeft, out hit, out error);
            if (error != null)
            {
                patch.Error = error;
                patch.EndUt = startUt;
            }
            else if (encounter.HasValue)
            {
                patch.End = PatchEnd.Encounter;
                patch.Next = hit;
                patch.EndUt = encounter.Value;
            }
            else if (down.HasValue && down.Value <= horizon)
            {
                patch.End = body.AtmosphereHeight > 0 ? PatchEnd.Atmosphere : PatchEnd.Impact;
                patch.EndUt = down.Value;
            }
            else if (escape.HasValue && escape.Value <= horizon)
            {
                patch.End = PatchEnd.Escape;
                patch.EndUt = escape.Value;
            }
            else patch.EndUt = orbit.Closed ? startUt + orbit.Period : double.PositiveInfinity;
            return patch;
        }

        /// <summary>
        /// The first time in [start, horizon] the craft is inside a child body's sphere of influence, stepping in time no faster
        /// than the gap to each SOI could close at the current closing speed (so a brief pass is not stepped over), then
        /// bisecting the crossing. The body just left is ignored until the craft is clear of its SOI. When the search runs out of
        /// steps it gives <paramref name="error"/> (null otherwise) and no encounter: an expected failure, not a bug.
        /// </summary>
        internal double? FirstEncounter(SnapshotBody body, Conic orbit, double start, double horizon, SnapshotBody justLeft, out SnapshotBody hit, out string error)
        {
            hit = null;
            error = null;
            var children = Children(body).ToList();
            if (children.Count == 0) return null;
            var armed = children.Select(c => justLeft == null || !string.Equals(c.Name, justLeft.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var end = double.IsInfinity(horizon) ? start + LongestSearch(children) : horizon;
            var maxStep = (end - start) / 100;
            var t = start;
            var previous = start;
            for (var steps = 0; steps < _maxSteps; steps++)
            {
                Vec3 r, v;
                orbit.State(t, out r, out v);
                var step = maxStep;
                double? first = null;
                for (var i = 0; i < children.Count; i++)
                {
                    Vec3 cr, cv;
                    OrbitOf(children[i]).State(t, out cr, out cv);
                    var gap = (r - cr).Length - children[i].SoiRadius;
                    if (!armed[i])
                    {
                        if (gap > children[i].SoiRadius * 0.01) armed[i] = true;
                        step = Math.Min(step, Math.Max(MinStep, Math.Abs(gap) * 0.5 / Math.Max(1, (v - cv).Length)));
                        continue;
                    }
                    if (gap <= 0 && t > start)
                    {
                        var at = Bisect(orbit, children[i], previous, t);
                        if (!first.HasValue || at < first.Value)
                        {
                            first = at;
                            hit = children[i];
                        }
                        continue;
                    }
                    step = Math.Min(step, Math.Max(MinStep, gap * 0.5 / Math.Max(1, (v - cv).Length)));
                }
                if (first.HasValue) return first;
                if (t >= end) return null;
                previous = t;
                t = Math.Min(end, t + step);
            }
            error = "the encounter search took too many steps";
            return null;
        }

        /// <summary>How far ahead to look for an encounter on a hyperbola round the star, which never leaves: one orbit of its slowest child.</summary>
        private double LongestSearch(IEnumerable<SnapshotBody> children)
        {
            return children.Max(c => OrbitOf(c).Period);
        }

        /// <summary>The time the craft crosses into <paramref name="child"/>'s SOI, between a time outside and one inside.</summary>
        private double Bisect(Conic orbit, SnapshotBody child, double outside, double inside)
        {
            var childOrbit = OrbitOf(child);
            for (var i = 0; i < 60 && inside - outside > 1e-3; i++)
            {
                var mid = (outside + inside) / 2;
                if ((orbit.Position(mid) - childOrbit.Position(mid)).Length <= child.SoiRadius) inside = mid;
                else outside = mid;
            }
            return inside;
        }

        /// <summary>
        /// The craft's closest approach to <paramref name="target"/> over a patch round the target's parent: centre-to-centre
        /// distance and its UT, found by stepping and then a golden-section search round the nearest step.
        /// </summary>
        public void ClosestApproach(Patch patch, SnapshotBody target, out double distance, out double ut)
        {
            var targetOrbit = OrbitOf(target);
            Func<double, double> gap = t => (patch.Orbit.Position(t) - targetOrbit.Position(t)).Length;
            var end = double.IsInfinity(patch.EndUt) ? patch.StartUt + targetOrbit.Period : patch.EndUt;
            var maxStep = (end - patch.StartUt) / 200;
            double bestT = patch.StartUt, best = gap(patch.StartUt), before = patch.StartUt, after = patch.StartUt;
            var t = patch.StartUt;
            for (var steps = 0; steps < MaxSteps && t < end; steps++)
            {
                Vec3 r, v, tr, tv;
                patch.Orbit.State(t, out r, out v);
                targetOrbit.State(t, out tr, out tv);
                var step = Math.Min(maxStep, Math.Max(MinStep, 0.2 * (r - tr).Length / Math.Max(1, (v - tv).Length)));
                var next = Math.Min(end, t + step);
                var d = gap(next);
                if (d < best)
                {
                    best = d;
                    bestT = next;
                    before = t;
                    after = Math.Min(end, next + step);
                }
                t = next;
            }
            // Golden-section search for the minimum between the steps either side of the nearest one.
            var golden = (Math.Sqrt(5) - 1) / 2;
            double a = before, b = after;
            for (var i = 0; i < 100 && b - a > 1e-3; i++)
            {
                var c = b - golden * (b - a);
                var d = a + golden * (b - a);
                if (gap(c) < gap(d)) b = d;
                else a = c;
            }
            var mid = (a + b) / 2;
            if (gap(mid) < best)
            {
                best = gap(mid);
                bestT = mid;
            }
            distance = best;
            ut = bestT;
        }

        /// <summary>The craft's lowest point in a patch round the target itself: periapsis if it is reached in the patch, else the nearer end.</summary>
        public static void Lowest(Patch patch, out double distance, out double ut)
        {
            var pe = patch.Orbit.NextPeriapsis(patch.StartUt);
            if (pe.HasValue && pe.Value <= patch.EndUt)
            {
                distance = patch.Orbit.PeR;
                ut = pe.Value;
                return;
            }
            var end = double.IsInfinity(patch.EndUt) ? patch.StartUt : patch.EndUt;
            var atStart = patch.Orbit.Radius(patch.StartUt);
            var atEnd = patch.Orbit.Radius(end);
            distance = Math.Min(atStart, atEnd);
            ut = atStart <= atEnd ? patch.StartUt : end;
        }
    }
}
