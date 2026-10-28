// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>solve_burn</c>: the burn that reaches a goal, as maneuver-node components with its magnitude, burn time and the
    /// <c>predict_burn</c> result for it. <c>circularize</c>, <c>set_ap</c> and <c>set_pe</c> are closed-form (vis-viva, a
    /// prograde burn at an apsis); <c>next_patch_pe</c> searches the burn size with <see cref="PredictBurn"/>'s own propagation.
    /// Plane changes are not included: the normal and radial components are 0.
    /// </summary>
    internal sealed class SolveBurn : IPlanningTool
    {
        /// <summary>The finest burn step the search scans with, m/s: a transfer window is tens of m/s wide.</summary>
        private const double ScanStep = 2;
        private const int MaxScanSteps = 1500;
        /// <summary>
        /// How many burn sizes the next_patch_pe scan tries before it gives up: one past the scan's own step count (it runs from 0
        /// to its end inclusive), so a normal search is never cut short. A count, not a clock, so the same input gives the same
        /// answer under any machine load; it bounds the scan's cost in game, inside the tool loop's 5 s cap.
        /// </summary>
        internal const int DefaultMaxScanEvaluations = MaxScanSteps + 1;
        private const string DefaultAt = "in 60";
        /// <summary>BrentRoot's tolerance on the burn size, relative to max(|burn|, 1) m/s: far finer than the metre the Pe is held to.</summary>
        private const double RefineTolerance = 1e-9;
        private const int RefineIterations = 80;

        private readonly int _maxScanEvaluations;

        public SolveBurn() : this(DefaultMaxScanEvaluations)
        {
        }

        /// <summary>A solver whose next_patch_pe scan gives up after <paramref name="maxScanEvaluations"/> burn sizes (tests inject a small one).</summary>
        internal SolveBurn(int maxScanEvaluations)
        {
            _maxScanEvaluations = maxScanEvaluations;
        }

        public string Name
        {
            get { return "solve_burn"; }
        }

        public string Description
        {
            get
            {
                return "The burn that reaches an orbit goal, worked out from this turn's Game Snapshot: its delta-v in maneuver-node "
                    + "components (prograde, normal, radial m/s), its magnitude, its burn time, and the predict_burn result for that burn. "
                    + "Goals: circularize (at \"ap\" or \"pe\"); set_ap or set_pe (value, m above sea level; set_ap burns at the next Pe, "
                    + "set_pe at the next Ap); next_patch_pe (value, m above the next patch's body, from a prograde or retrograde burn at "
                    + "\"at\", default in 60 s; the smaller burn when two reach it). Plane changes are not included. Its blocks are "
                    + "circularize, exec_node and tune_pe: call it for the delta-v to budget their burn instead of working it out "
                    + "yourself, then pass the block the goal (tune_pe's value, circularize's at), not the burn. Don't call it to check "
                    + "a burn you already have (use predict_burn), or for the craft's delta-v (stage_performance).";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"goal\":{\"type\":\"string\",\"enum\":[\"circularize\",\"set_ap\",\"set_pe\",\"next_patch_pe\"]},"
                    + "\"at\":{\"description\":\"circularize: \\\"ap\\\" or \\\"pe\\\". next_patch_pe: \\\"ap\\\", \\\"pe\\\", a UT in seconds or \\\"in <s>\\\"; default \\\"in 60\\\".\"},"
                    + "\"value\":{\"type\":\"number\",\"description\":\"set_ap, set_pe, next_patch_pe: the altitude in metres above sea level.\"},"
                    + "\"direction\":{\"type\":\"string\",\"enum\":[\"prograde\",\"retrograde\"],\"description\":\"next_patch_pe only: which way to burn. Default prograde.\"}"
                    + "},\"required\":[\"goal\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "solving the burn"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
        {
            var goal = JsonNode.Str(input["goal"]);
            var value = JsonNode.Double(input["value"]);
            var at = input["at"];
            var direction = JsonNode.Str(input["direction"]);
            if (goal != "next_patch_pe" && direction != null) return PlanningJson.Error("direction is for next_patch_pe only");
            if (goal == "set_ap" || goal == "set_pe")
            {
                if (at != null) return PlanningJson.Error(goal + " burns at the " + (goal == "set_ap" ? "periapsis" : "apoapsis") + "; leave at out");
            }
            if (goal == "circularize")
            {
                if (value.HasValue) return PlanningJson.Error("circularize takes no value; it makes the orbit circular at the burn's altitude");
                var where = at != null && at.Kind == JsonKind.String ? at.StringValue.Trim().ToLowerInvariant() : null;
                if (where != "ap" && where != "pe") return PlanningJson.Error("circularize needs at to be \"ap\" or \"pe\"");
            }
            else if (!value.HasValue) return PlanningJson.Error(goal + " needs a value, the altitude in metres above sea level");

            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return error;
            var path = setup.System.Follow(setup.Body, setup.Start, setup.Now, 8);
            return goal == "next_patch_pe"
                ? SolvePatchPe(setup, path, value.Value, at, direction ?? "prograde")
                : SolveClosedForm(setup, path, goal, value, at);
        }

        /// <summary>A prograde burn at an apsis to the speed that puts the orbit's other end at the goal, by vis-viva.</summary>
        private static ToolOutcome SolveClosedForm(PredictBurn.Setup setup, List<Patch> path, string goal, double? value, JsonNode at)
        {
            var apsis = goal == "circularize" ? at.StringValue.Trim().ToLowerInvariant() : goal == "set_ap" ? "pe" : "ap";
            string problem;
            var ut = PredictBurn.BurnUt(JsonReader.Parse(PlanningJson.Quote(apsis)), path, setup.Now, setup.Now, "the burn", out problem);
            if (problem != null) return PlanningJson.Error(problem);
            var patch = PredictBurn.Containing(path, ut.Value);
            if (patch == null) return PlanningJson.Error("the burn is after the craft reaches " + PredictBurn.Ending(path[path.Count - 1]) + "; nothing after that is predicted");
            var mu = patch.Body.Gm;
            Vec3 r, v;
            patch.Orbit.State(ut.Value, out r, out v);
            var radius = r.Length;
            double wanted;
            if (goal == "circularize")
                wanted = Math.Sqrt(mu / radius);
            else
            {
                var far = value.Value + patch.Body.Radius;
                if (far <= patch.Body.Radius)
                    return PlanningJson.Error("a " + (goal == "set_ap" ? "Ap" : "Pe") + " of " + Num(value.Value) + " m is not above " + patch.Body.Name + "'s sea level");
                // A burn at radius r leaves r on the new orbit, so the far end can only be on the far side of it.
                if (goal == "set_ap" && far < radius)
                    return PlanningJson.Error("an Ap of " + Num(value.Value) + " m is below the Pe of " + Num(radius - patch.Body.Radius) + " m; a burn can't put the Ap under the point it burns at");
                if (goal == "set_pe" && far > radius)
                    return PlanningJson.Error("a Pe of " + Num(value.Value) + " m is above the Ap of " + Num(radius - patch.Body.Radius) + " m; a burn can't put the Pe over the point it burns at");
                wanted = Math.Sqrt(mu * (2 / radius - 2 / (radius + far)));
            }
            return Answer(setup, goal, ut.Value, wanted - v.Length, null);
        }

        /// <summary>
        /// The burn at <c>at</c>, prograde or retrograde, whose next patch has the periapsis asked for: scans the burn size in steps
        /// with the predict_burn propagation, finds where the next patch's Pe crosses the goal, and refines it with <see cref="BrentRoot"/>.
        /// </summary>
        private ToolOutcome SolvePatchPe(PredictBurn.Setup setup, List<Patch> path, double value, JsonNode at, string direction)
        {
            string problem;
            var ut = PredictBurn.BurnUt(at ?? JsonReader.Parse(PlanningJson.Quote(DefaultAt)), path, setup.Now, setup.Now, "the burn", out problem);
            if (problem != null) return PlanningJson.Error(problem);
            var patch = PredictBurn.Containing(path, ut.Value);
            if (patch == null) return PlanningJson.Error("the burn is after the craft reaches " + PredictBurn.Ending(path[path.Count - 1]) + "; nothing after that is predicted");
            Vec3 r, v;
            patch.Orbit.State(ut.Value, out r, out v);
            var sign = direction == "retrograde" ? -1.0 : 1.0;
            var speed = v.Length;
            var most = sign > 0 ? 2 * Math.Sqrt(patch.Body.Gm / r.Length) : speed;
            var fixedAt = JsonReader.Parse(ut.Value.ToString("R", CultureInfo.InvariantCulture));

            // The next patch's Pe (m above its body's sea level) and body for a burn of s m/s; false when there is no next patch.
            Func<double, Tuple<double, string>> pe = s =>
            {
                var request = new PredictBurn.BurnRequest { At = fixedAt, Prograde = sign * s };
                List<PredictBurn.BurnDone> done;
                List<Patch> after;
                if (PredictBurn.Simulate(setup, new List<PredictBurn.BurnRequest> { request }, out done, out after, false) != null || after.Count < 2) return null;
                return Tuple.Create(after[1].Orbit.PeR - after[1].Body.Radius, after[1].Body.Name);
            };

            var step = Math.Max(ScanStep, most / MaxScanSteps);
            double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
            var anyNext = false;
            double? last = null;
            string lastBody = null;
            var tried = 0;
            for (var s = 0.0; s <= most; s += step)
            {
                if (tried++ >= _maxScanEvaluations)
                    return PlanningJson.Error("the search for a burn to a next patch Pe of " + Num(value) + " m gave up after " + (tried - 1).ToString(CultureInfo.InvariantCulture)
                        + " burn sizes, at " + Num(s) + " m/s; try an earlier or narrower burn time");
                var got = pe(s);
                if (got == null)
                {
                    last = null;
                    continue;
                }
                anyNext = true;
                lowest = Math.Min(lowest, got.Item1);
                highest = Math.Max(highest, got.Item1);
                if (last.HasValue && got.Item2 == lastBody && (last.Value - value) * (got.Item1 - value) <= 0)
                {
                    var found = Refine(pe, s - step, s, last.Value, got.Item1, value, got.Item2);
                    if (found == null)
                        return PlanningJson.Error("found a burn near " + Num(s) + " m/s that reaches " + got.Item2 + ", but the search could not narrow it to the Pe asked for (the encounter drops out or the Pe jumps there); try another burn time");
                    return Answer(setup, "next_patch_pe", ut.Value, sign * found.Value, fixedAt);
                }
                last = got.Item1;
                lastBody = got.Item2;
            }
            var where = at == null ? "in 60 s" : "that time";
            if (!anyNext)
                return PlanningJson.Error("no " + direction + " burn of up to " + Num(most) + " m/s at " + where + " takes the craft out of " + patch.Body.Name
                    + "'s sphere of influence or to an encounter, so there is no next patch; try another burn time");
            return PlanningJson.Error("the next patch's Pe only reaches " + Num(lowest) + " to " + Num(highest) + " m with " + direction + " burns of up to "
                + Num(most) + " m/s at " + where + ", not " + Num(value) + " m; try another burn time");
        }

        /// <summary>
        /// Finds the burn size between <paramref name="lo"/> and <paramref name="hi"/> (Pe errors straddle zero) with
        /// <see cref="BrentRoot"/>, to a Pe within a metre; null if the encounter drops out or the Pe jumps instead of crossing the goal.
        /// The Pe at both ends is known from the scan, so those two evaluations are not repeated.
        /// </summary>
        private static double? Refine(Func<double, Tuple<double, string>> pe, double lo, double hi, double peLo, double peHi, double value, string body)
        {
            var dropped = false;
            Func<double, double> error = s =>
            {
                if (s == lo) return peLo - value;
                if (s == hi) return peHi - value;
                var got = pe(s);
                if (got == null || got.Item2 != body)
                {
                    dropped = true;
                    return double.NaN;
                }
                return got.Item1 - value;
            };
            var found = BrentRoot.Solve(error, lo, hi, RefineTolerance, RefineIterations);
            if (dropped || !found.Converged || Math.Abs(found.Fx) >= 1) return null;
            return found.X;
        }

        /// <summary>The result: the burn's components, magnitude and time, with the predict_burn result for it.</summary>
        private static ToolOutcome Answer(PredictBurn.Setup setup, string goal, double ut, double prograde, JsonNode fixedAt)
        {
            var request = new PredictBurn.BurnRequest { At = fixedAt ?? JsonReader.Parse(ut.ToString("R", CultureInfo.InvariantCulture)), Prograde = prograde };
            List<PredictBurn.BurnDone> burns;
            List<Patch> path;
            var problem = PredictBurn.Simulate(setup, new List<PredictBurn.BurnRequest> { request }, out burns, out path);
            if (problem != null) return PlanningJson.Error(problem);
            var predicted = PredictBurn.Result(setup.System, burns, path, null, setup.Now);
            var burn = burns[0];
            var json = new JsonWriter().BeginObject().Field("goal", goal);
            PlanningJson.Time(json, "at", ut, setup.Now);
            json.Name("dv").BeginObject()
                .Field("prograde", PlanningJson.Round(prograde, 2)).Field("normal", 0.0).Field("radial", 0.0)
                .EndObject()
                .Field("magnitude", PlanningJson.Round(Math.Abs(prograde), 2))
                .Field("burn_time", burn.BurnTime.HasValue ? PlanningJson.Round(burn.BurnTime.Value, 1) : (double?)null);
            if (!burn.BurnTime.HasValue) json.Field("burn_time_note", "the craft's stages don't give it");
            else if (burn.BurnTime.Value > PredictBurn.LongBurnSeconds) json.Field("long_burn", true);
            json.Name("predict_burn").Raw(predicted.Json).EndObject();
            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = goal + ": " + Math.Round(Math.Abs(prograde), 1).ToString("0.#", CultureInfo.InvariantCulture) + " m/s "
                    + (prograde < 0 ? "retrograde" : "prograde") + " at UT " + Num(ut) + "; " + predicted.Summary,
            };
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
