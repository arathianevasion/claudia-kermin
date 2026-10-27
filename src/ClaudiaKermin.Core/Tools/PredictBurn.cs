// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>predict_burn</c>: the orbit after up to three impulsive burns, as patched conics, with how each patch ends, the closest
    /// approach to a target body and an estimated burn time per burn. It propagates a detached copy of the Game Snapshot's craft
    /// orbit over the snapshot's body table (<see cref="BodySystem"/>), never the live vessel's orbit or a real maneuver node.
    /// Burns are in stock's maneuver-node frame: prograde along the velocity, normal along the orbit's angular momentum, and
    /// radial out of the body in the orbit plane, square to the velocity.
    /// </summary>
    internal sealed class PredictBurn : IPlanningTool
    {
        private const double G0 = 9.80665;
        /// <summary>Patches in the result, the first being the orbit after the last burn.</summary>
        private const int MaxPatches = 4;
        /// <summary>Patches followed to place a later burn on the path (the result itself shows only <see cref="MaxPatches"/>).</summary>
        private const int PathPatches = 8;
        /// <summary>A burn longer than this is spread far enough round its time that the impulsive answer is flagged.</summary>
        public const double LongBurnSeconds = 60;
        public const string Model = "impulsive burns on patched conics from this turn's Game Snapshot; no drag, so nothing after reaching an "
            + "atmosphere is predicted; a burn over 60 s is spread round its time and is less accurate";

        public string Name
        {
            get { return "predict_burn"; }
        }

        public string Description
        {
            get
            {
                return "The orbit after one to three burns, worked out with the game's patched conics from this turn's Game Snapshot: up "
                    + "to 4 patches, each with its body, Ap and Pe (m above sea level), inclination, eccentricity, start and end UT, and "
                    + "how it ends (escape, encounter <body>, impact, atmosphere or stable), plus the closest approach to a target body "
                    + "and each burn's estimated burn time. Burns are impulsive, in maneuver-node components (prograde, normal, radial "
                    + "m/s), at ap, pe, a UT or \"in <s>\". Call it to check what a burn for exec_node, transfer_to or tune_pe does "
                    + "before you put it in a plan, instead of working the orbit out yourself. Don't call it for what to burn to reach "
                    + "an orbit, or for the craft's delta-v.";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"burns\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":3,\"description\":\"The burns, in time order.\",\"items\":{\"type\":\"object\",\"properties\":{"
                    + "\"at\":{\"description\":\"When: \\\"ap\\\" or \\\"pe\\\" (the next one after the burn before), a UT in seconds, or \\\"in <s>\\\" seconds from now.\"},"
                    + "\"dv\":{\"type\":\"object\",\"properties\":{\"prograde\":{\"type\":\"number\"},\"normal\":{\"type\":\"number\"},\"radial\":{\"type\":\"number\"}},"
                    + "\"additionalProperties\":false,\"description\":\"m/s in maneuver-node components; a missing one is 0.\"}"
                    + "},\"required\":[\"at\",\"dv\"],\"additionalProperties\":false}},"
                    + "\"target\":{\"type\":\"string\",\"description\":\"A body to give the closest approach to.\"}"
                    + "},\"required\":[\"burns\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "checking the burn"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
        {
            ToolOutcome error;
            var setup = Setup.Prepare(snapshot, JsonNode.Str(input["target"]), out error);
            if (setup == null) return error;
            var requests = new List<BurnRequest>();
            foreach (var burn in input["burns"].Items)
            {
                var dv = burn["dv"];
                requests.Add(new BurnRequest
                {
                    At = burn["at"],
                    Prograde = JsonNode.Double(dv["prograde"]) ?? 0,
                    Normal = JsonNode.Double(dv["normal"]) ?? 0,
                    Radial = JsonNode.Double(dv["radial"]) ?? 0,
                });
            }
            List<BurnDone> burns;
            List<Patch> path;
            var problem = Simulate(setup, requests, out burns, out path);
            if (problem != null) return PlanningJson.Error(problem);
            return Result(setup.System, burns, path, setup.Target, setup.Now);
        }

        /// <summary>One burn asked for: when (an <c>at</c> as predict_burn takes it) and its maneuver-node components, m/s.</summary>
        internal sealed class BurnRequest
        {
            public JsonNode At;
            public double Prograde;
            public double Normal;
            public double Radial;
        }

        /// <summary>What every propagation starts from: the snapshot's craft orbit over its body table, checked to be in flight and above the atmosphere.</summary>
        internal sealed class Setup
        {
            public GameSnapshot Snapshot;
            public BodySystem System;
            public SnapshotBody Body;
            public Conic Start;
            public double Now;
            /// <summary>The body asked for the closest approach to; null for none.</summary>
            public SnapshotBody Target;

            /// <summary>The setup, or null with <paramref name="error"/> saying why the snapshot can't be propagated.</summary>
            public static Setup Prepare(GameSnapshot snapshot, string targetName, out ToolOutcome error)
            {
                error = null;
                if (snapshot.Bodies.Count == 0) { error = PlanningJson.Error("the Game Snapshot has no body table"); return null; }
                var orbit = snapshot.Orbit;
                if (orbit == null || orbit.SemiMajorAxis == 0) { error = PlanningJson.Error("the Game Snapshot has no orbit for the craft"); return null; }
                var situation = snapshot.Situation ?? "";
                if (situation == "PRELAUNCH" || situation == "LANDED" || situation == "SPLASHED")
                {
                    error = PlanningJson.Error("the craft is " + situation.ToLowerInvariant() + ", not in flight; there is no orbit to burn from");
                    return null;
                }
                var system = new BodySystem(snapshot.Bodies);
                var at = system.Find(string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body);
                if (at == null || at.Gm <= 0) { error = PlanningJson.Error("the body table has no " + PlanningJson.Quote(orbit.Body) + " to propagate round"); return null; }
                SnapshotBody target = null;
                if (!string.IsNullOrEmpty(targetName))
                {
                    target = system.Find(targetName);
                    if (target == null)
                    {
                        error = PlanningJson.Error("there is no body named " + PlanningJson.Quote(targetName) + "; the bodies are "
                            + string.Join(", ", snapshot.Bodies.Select(b => b.Name).ToArray()));
                        return null;
                    }
                }
                var now = snapshot.Ut;
                var start = Conic.FromElements(at.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan,
                    orbit.ArgumentOfPeriapsis, orbit.MeanAnomalyAtEpoch, orbit.Epoch);
                if (start.Radius(now) < at.Radius + at.AtmosphereHeight)
                {
                    error = PlanningJson.Error("the craft is " + (at.AtmosphereHeight > 0 ? "inside " + at.Name + "'s atmosphere" : "below " + at.Name + "'s surface")
                        + "; the impulsive model has no drag, so wait until it is above " + Math.Round(at.AtmosphereHeight).ToString("0", CultureInfo.InvariantCulture) + " m");
                    return null;
                }
                return new Setup { Snapshot = snapshot, System = system, Body = at, Start = start, Now = now, Target = target };
            }
        }

        /// <summary>
        /// The burns applied one after another to the setup's orbit: the burns done and the patches after the last. Null when it
        /// worked, otherwise why a burn can't be placed. This is the propagation both predict_burn and solve_burn use.
        /// </summary>
        internal static string Simulate(Setup setup, List<BurnRequest> requests, out List<BurnDone> burns, out List<Patch> path, bool withBurnTime = true)
        {
            // The search in solve_burn runs this hundreds of times and doesn't need the times.
            var stages = withBurnTime ? BurnStages(setup.Snapshot) : null;
            var system = setup.System;
            var now = setup.Now;
            burns = new List<BurnDone>();
            path = system.Follow(setup.Body, setup.Start, now, PathPatches);
            for (var i = 0; i < requests.Count; i++)
            {
                var label = "burn " + (i + 1).ToString(CultureInfo.InvariantCulture);
                var after = burns.Count > 0 ? burns[burns.Count - 1].Ut : now;
                string problem;
                var ut = BurnUt(requests[i].At, path, after, now, label, out problem);
                if (problem != null) return problem;
                var patch = Containing(path, ut.Value);
                if (patch == null)
                {
                    var last = path[path.Count - 1];
                    return label + " is after the craft reaches " + Ending(last) + " at UT " + Math.Round(last.EndUt).ToString("0", CultureInfo.InvariantCulture)
                        + "; nothing after that is predicted";
                }
                var prograde = requests[i].Prograde;
                var normal = requests[i].Normal;
                var radial = requests[i].Radial;
                Vec3 r, v;
                patch.Orbit.State(ut.Value, out r, out v);
                var along = v.Normalized;
                var up = Vec3.Cross(r, v).Normalized;
                var outward = Vec3.Cross(along, up);
                var burned = Conic.FromState(patch.Body.Gm, r, v + along * prograde + up * normal + outward * radial, ut.Value);
                var magnitude = Math.Sqrt(prograde * prograde + normal * normal + radial * radial);
                burns.Add(new BurnDone { Ut = ut.Value, DeltaV = magnitude, Body = patch.Body, Orbit = burned, BurnTime = stages != null ? stages.Take(magnitude) : null });
                path = system.Follow(patch.Body, burned, ut.Value, i == requests.Count - 1 ? MaxPatches : PathPatches);
            }
            return null;
        }

        internal sealed class BurnDone
        {
            public double Ut;
            public double DeltaV;
            public SnapshotBody Body;
            public Conic Orbit;
            /// <summary>Seconds; null when the stages can't give it.</summary>
            public double? BurnTime;
        }

        /// <summary>The burn's UT from its <c>at</c>, or why it can't be placed.</summary>
        internal static double? BurnUt(JsonNode at, List<Patch> path, double after, double now, string label, out string problem)
        {
            problem = null;
            double ut;
            var text = at != null && at.Kind == JsonKind.String ? at.StringValue.Trim().ToLowerInvariant() : null;
            if (text == "ap" || text == "pe")
            {
                var patch = Containing(path, after);
                if (patch == null)
                {
                    problem = label + " is at " + text + ", but the craft has left the predicted path by then";
                    return null;
                }
                var next = text == "ap" ? patch.Orbit.NextApoapsis(after + 1e-3) : patch.Orbit.NextPeriapsis(after + 1e-3);
                if (!next.HasValue)
                {
                    problem = label + " is at " + text + ", but the orbit round " + patch.Body.Name + " is "
                        + (text == "ap" ? "an escape with no apoapsis" : "past its periapsis");
                    return null;
                }
                if (next.Value > patch.EndUt)
                {
                    problem = label + " is at " + text + ", but the craft reaches " + Ending(patch) + " first";
                    return null;
                }
                return next.Value;
            }
            if (at != null && at.Kind == JsonKind.Number)
                ut = JsonNode.Double(at).Value;
            else if (text != null && text.StartsWith("in ", StringComparison.Ordinal)
                && double.TryParse(text.Substring(3).Trim().TrimEnd('s').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ut))
                ut += now;
            else if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ut)) { }
            else
            {
                problem = label + ".at must be \"ap\", \"pe\", a UT in seconds, or \"in <s>\"";
                return null;
            }
            if (ut < after - 0.5)
            {
                problem = label + " is at UT " + Math.Round(ut).ToString("0", CultureInfo.InvariantCulture) + ", before "
                    + (after == now ? "now" : "the burn before it");
                return null;
            }
            return Math.Max(ut, after);
        }

        /// <summary>The patch flown at <paramref name="ut"/>; null when the path has ended (an impact, the atmosphere) before it.</summary>
        internal static Patch Containing(List<Patch> path, double ut)
        {
            foreach (var patch in path)
                if (ut <= patch.EndUt || patch.End == PatchEnd.Stable) return patch;
            return null;
        }

        internal static string Ending(Patch patch)
        {
            switch (patch.End)
            {
                case PatchEnd.Escape: return "the edge of " + patch.Body.Name + "'s sphere of influence";
                case PatchEnd.Encounter: return patch.Next.Name + "'s sphere of influence";
                case PatchEnd.Impact: return patch.Body.Name + "'s surface";
                case PatchEnd.Atmosphere: return patch.Body.Name + "'s atmosphere";
                default: return "the end of the prediction";
            }
        }

        internal static string EndWord(Patch patch)
        {
            switch (patch.End)
            {
                case PatchEnd.Escape: return "escape";
                case PatchEnd.Encounter: return "encounter " + patch.Next.Name;
                case PatchEnd.Impact: return "impact";
                case PatchEnd.Atmosphere: return "atmosphere";
                default: return "stable";
            }
        }

        internal static ToolOutcome Result(BodySystem system, List<BurnDone> burns, List<Patch> path, SnapshotBody target, double now)
        {
            var json = new JsonWriter().BeginObject().Field("model", Model);
            json.Name("burns").BeginArray();
            foreach (var burn in burns)
            {
                json.BeginObject();
                PlanningJson.Time(json, "at", burn.Ut, now);
                json.Field("dv", PlanningJson.Round(burn.DeltaV, 1))
                    .Field("burn_time", burn.BurnTime.HasValue ? PlanningJson.Round(burn.BurnTime.Value, 1) : (double?)null);
                if (!burn.BurnTime.HasValue) json.Field("burn_time_note", "the craft's stages don't give it");
                else if (burn.BurnTime.Value > LongBurnSeconds) json.Field("long_burn", true);
                json.Field("body", burn.Body.Name);
                Apsides(json, burn.Orbit, burn.Body);
                json.EndObject();
            }
            json.EndArray();

            json.Name("patches").BeginArray();
            foreach (var patch in path)
            {
                json.BeginObject().Field("body", patch.Body.Name);
                Apsides(json, patch.Orbit, patch.Body);
                json.Field("inclination", PlanningJson.Round(patch.Orbit.Inclination, 2))
                    .Field("eccentricity", PlanningJson.Round(patch.Orbit.E, 4));
                PlanningJson.Time(json, "start", patch.StartUt, now);
                if (!double.IsInfinity(patch.EndUt)) PlanningJson.Time(json, "end", patch.EndUt, now);
                json.Field("ends", EndWord(patch)).EndObject();
            }
            json.EndArray();

            if (target != null)
            {
                double distance, ut;
                if (Closest(system, path, target, out distance, out ut))
                {
                    json.Name("closest_approach").BeginObject().Field("body", target.Name)
                        .Field("distance", PlanningJson.Round(distance, 0))
                        .Field("altitude", PlanningJson.Round(distance - target.Radius, 0));
                    PlanningJson.Time(json, "at", ut, now);
                    json.EndObject();
                }
                else
                {
                    json.Field("closest_approach", (string)null);
                    json.Field("closest_approach_note", "the predicted patches never orbit " + target.Name + " or share its parent's sphere of influence");
                }
            }
            if (burns.Any(b => b.BurnTime > LongBurnSeconds))
                json.Field("note", "a burn over " + LongBurnSeconds.ToString("0", CultureInfo.InvariantCulture) + " s is less accurate than this impulsive answer");
            json.EndObject();

            var last = path[path.Count - 1];
            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = burns.Count.ToString(CultureInfo.InvariantCulture) + (burns.Count == 1 ? " burn: " : " burns: ")
                    + string.Join(", then ", path.Select(p => p.Body.Name + (p.Orbit.Closed ? " Ap " + Km(p.Orbit.ApR - p.Body.Radius) : "") + " Pe " + Km(p.Orbit.PeR - p.Body.Radius)).ToArray())
                    + "; ends " + EndWord(last),
            };
        }

        private static string Km(double metres)
        {
            return Math.Round(metres / 1000).ToString("0", CultureInfo.InvariantCulture) + " km";
        }

        /// <summary>Ap (null on an escape) and Pe as altitudes above sea level, m.</summary>
        private static void Apsides(JsonWriter json, Conic orbit, SnapshotBody body)
        {
            json.Field("ap", orbit.Closed ? PlanningJson.Round(orbit.ApR - body.Radius, 0) : (double?)null)
                .Field("pe", PlanningJson.Round(orbit.PeR - body.Radius, 0));
        }

        /// <summary>The nearest the predicted path comes to <paramref name="target"/>; false when no patch is round it or its parent.</summary>
        private static bool Closest(BodySystem system, List<Patch> path, SnapshotBody target, out double distance, out double ut)
        {
            distance = double.PositiveInfinity;
            ut = 0;
            var found = false;
            foreach (var patch in path)
            {
                double d, t;
                if (string.Equals(patch.Body.Name, target.Name, StringComparison.OrdinalIgnoreCase)) BodySystem.Lowest(patch, out d, out t);
                else if (string.Equals(patch.Body.Name, target.Parent, StringComparison.OrdinalIgnoreCase)) system.ClosestApproach(patch, target, out d, out t);
                else continue;
                found = true;
                if (d < distance)
                {
                    distance = d;
                    ut = t;
                }
            }
            return found;
        }

        /// <summary>The stages that can burn, first to last, used up burn by burn for the burn times.</summary>
        private static StageBudget BurnStages(GameSnapshot snapshot)
        {
            var rows = snapshot.DeltaVSimulator ? StagePerformance.Rows(snapshot, 0, 0) : new List<StagePerformance.StageRow>();
            return new StageBudget(rows.Where(r => r.Thrust > 0 && r.Isp > 0 && r.StartMass > r.EndMass && r.EndMass > 0).OrderByDescending(r => r.Stage));
        }

        /// <summary>The craft's stages in firing order, with how much of the current one the burns so far have used.</summary>
        private sealed class StageBudget
        {
            private readonly List<StagePerformance.StageRow> _stages;
            private int _index;
            private double _mass;

            public StageBudget(IEnumerable<StagePerformance.StageRow> stages)
            {
                _stages = stages.ToList();
                _mass = _stages.Count > 0 ? _stages[0].StartMass : 0;
            }

            /// <summary>Seconds to burn <paramref name="dv"/> m/s from where the last burn left off, by the rocket equation; null when the stages run out.</summary>
            public double? Take(double dv)
            {
                double seconds = 0;
                while (dv > 1e-9)
                {
                    if (_index >= _stages.Count) return null;
                    var stage = _stages[_index];
                    var exhaust = stage.Isp * G0;
                    var available = exhaust * Math.Log(_mass / stage.EndMass);
                    if (dv <= available)
                    {
                        var end = _mass / Math.Exp(dv / exhaust);
                        seconds += (_mass - end) * exhaust / stage.Thrust;
                        _mass = end;
                        return seconds;
                    }
                    seconds += (_mass - stage.EndMass) * exhaust / stage.Thrust;
                    dv -= available;
                    _index++;
                    if (_index < _stages.Count) _mass = _stages[_index].StartMass;
                }
                return seconds;
            }
        }
    }
}
