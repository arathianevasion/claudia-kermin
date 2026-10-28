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
    /// <c>plan_ascent</c> (#367): flies the <c>launch</c> block's ascent in <see cref="AscentSim"/> for each turn_shape from 0.20
    /// to 0.80 and picks the one that reaches target_ap and orbit with the most delta-v left, labelling each stage a Boost stage
    /// or an Orbital stage by the trajectory when it lights; or says the craft can't reach orbit, naming the Boost stage that
    /// falls. Given a turn_shape it flies that one only. It reads only the Game Snapshot (ADR 0006): the stage figures, the
    /// craft's nose-first drag table and the body table's air.
    /// </summary>
    internal sealed class PlanAscent : IPlanningTool
    {
        private const double FirstShape = 0.20;
        private const double LastShape = 0.80;
        private const double ShapeStep = 0.05;

        public string Name
        {
            get { return "plan_ascent"; }
        }

        public string Description
        {
            get
            {
                return "Flies the launch block's ascent from this turn's Game Snapshot (stage figures, the craft's nose-first drag) for "
                    + "turn_shape 0.20 to 0.80 and gives the one reaching target_ap and orbit with the most delta-v left; or "
                    + "reaches_orbit false naming the Boost stage that falls. Per stage: its role (Boost stage if it lights suborbital, "
                    + "else Orbital stage) and the altitude, speed, apoapsis, periapsis and time to apoapsis when it lights; then delta-v "
                    + "to orbit, left and kept by stage_limit, and the margin (marginal when thin). Give turn_shape to fly one shape. "
                    + "Its block is launch: pass launch the turn_shape it gives, with the same target_ap, stage_limit, turn_start_speed "
                    + "and throttle_max_q. A point-mass sim, not KSP. Don't call it for a body without air (use stage_performance), "
                    + "or again when the craft and target_ap haven't changed since you last did.";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"target_ap\":{\"description\":\"Apoapsis to reach, m, or \\\"auto\\\" (the atmosphere top + 10 km), as launch.\"},"
                    + "\"stage_limit\":{\"type\":\"integer\",\"description\":\"As the blocks: the launch never stages below it (default 0).\"},"
                    + "\"turn_start_speed\":{\"type\":\"number\",\"description\":\"m/s, as launch (default 60).\"},"
                    + "\"throttle_max_q\":{\"description\":\"Pa, or \\\"none\\\" (default), as launch.\"},"
                    + "\"target_inc\":{\"type\":\"number\",\"description\":\"Inclination, degrees, as launch (default 0): how much of the body's turning helps.\"},"
                    + "\"turn_shape\":{\"type\":\"number\",\"description\":\"Fly this shape only instead of searching.\"}"
                    + "},\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return true; }
        }

        public string Activity
        {
            get { return "planning the ascent"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody planningBody)
        {
            if (snapshot.Bodies.Count == 0) return PlanningJson.Error("the Game Snapshot has no body table");
            var body = snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, snapshot.Body, StringComparison.OrdinalIgnoreCase));
            if (body == null) return PlanningJson.Error("the craft's body " + PlanningJson.Quote(snapshot.Body) + " is not in the body table");
            if (body.AtmosphereHeight <= 0 || body.SeaLevelAtm <= 0)
                return PlanningJson.Error(body.Name + " has no air, so there is no launch to plan: use stage_performance for an airless ascent");
            if (snapshot.Craft.NoseDrag.Count == 0) return PlanningJson.Error("the Game Snapshot has no drag table for the craft");
            if (!snapshot.DeltaVSimulator) return PlanningJson.Error("the stock delta-v simulator is off, so there are no stage figures");
            if (snapshot.Stages.Count == 0) return PlanningJson.Error("the Game Snapshot has no stages");

            double targetAp;
            var targetNode = input["target_ap"];
            if (targetNode == null || JsonNode.Str(targetNode) == "auto") targetAp = body.AtmosphereHeight + 10000;
            else if (targetNode.Kind == JsonKind.Number) targetAp = JsonNode.Double(targetNode).Value;
            else return PlanningJson.Error("target_ap must be \"auto\" or m");
            if (targetAp <= body.AtmosphereHeight)
                return PlanningJson.Error("target_ap " + Format(targetAp) + " m is not above the atmosphere top (" + Format(body.AtmosphereHeight) + " m): a hop does not reach orbit");

            var maxQ = -1.0;
            var maxQNode = input["throttle_max_q"];
            if (maxQNode != null && JsonNode.Str(maxQNode) != "none")
            {
                if (maxQNode.Kind != JsonKind.Number) return PlanningJson.Error("throttle_max_q must be \"none\" or Pa");
                maxQ = JsonNode.Double(maxQNode).Value;
            }
            var inc = JsonNode.Double(input["target_inc"]) ?? 0;
            var grounded = snapshot.Situation == "PRELAUNCH" || snapshot.Situation == "LANDED" || snapshot.Situation == "SPLASHED";
            var options = new AscentOptions
            {
                Gm = body.Gm,
                Radius = body.Radius,
                AtmosphereHeight = body.AtmosphereHeight,
                SeaLevelAtm = body.SeaLevelAtm,
                Spin = body.RotationPeriod > 0 ? 2 * Math.PI * body.Radius / body.RotationPeriod * Math.Cos(inc * Math.PI / 180) : 0,
                StartAltitude = grounded ? Math.Max(0, snapshot.Altitude) : 0,
                TargetAp = targetAp,
                TurnStartSpeed = JsonNode.Double(input["turn_start_speed"]) ?? 60,
                MaxQ = maxQ,
                StageLimit = (int)(JsonNode.Long(input["stage_limit"]) ?? 0),
            };

            var air = new BodyAir(body);
            var drag = AscentSim.Table(snapshot.Craft.NoseDrag);
            var stages = AscentStage.From(snapshot, body);
            var given = JsonNode.Double(input["turn_shape"]);
            var shapes = given.HasValue ? new List<double> { given.Value } : Shapes();
            var tried = new List<Tuple<double, AscentFlight>>();
            foreach (var shape in shapes)
            {
                options.TurnShape = shape;
                tried.Add(Tuple.Create(shape, AscentSim.Fly(options, air, drag, stages)));
            }

            var reaching = tried.Where(t => t.Item2.ReachesOrbit).ToList();
            var best = reaching.Count > 0
                ? reaching.OrderByDescending(t => t.Item2.DeltaVLeft).First()
                : tried.OrderByDescending(t => t.Item2.BestAp).First();
            var flight = best.Item2;

            var json = new JsonWriter().BeginObject()
                .Field("body", body.Name)
                .Field("target_ap", Math.Round(targetAp))
                .Field("stage_limit", options.StageLimit)
                .Field("reaches_orbit", flight.ReachesOrbit)
                .Field(flight.ReachesOrbit ? "turn_shape" : "best_turn_shape", PlanningJson.Round(best.Item1, 2));
            if (!flight.ReachesOrbit)
            {
                var role = flight.Lit.LastOrDefault(l => l.Stage == flight.FailedStage);
                json.Field("failed_stage", flight.FailedStage)
                    .Field("failed_role", role == null || role.Suborbital ? "Boost stage" : "Orbital stage")
                    .Field("failed_altitude", Math.Round(flight.FailedAltitude))
                    .Field("failure", flight.Failure);
            }
            Stages(json, flight, stages, options.StageLimit);
            if (flight.ReachesOrbit)
            {
                var fraction = flight.DeltaVToOrbit > 0 ? flight.DeltaVLeft / flight.DeltaVToOrbit : 0;
                var minVs = double.IsInfinity(flight.MinVerticalSpeed) ? (double?)null : Math.Round(flight.MinVerticalSpeed);
                json.Field("dv_to_orbit", Math.Round(flight.DeltaVToOrbit))
                    .Field("dv_left", Math.Round(flight.DeltaVLeft))
                    .Field("dv_kept", Math.Round(flight.DeltaVKept))
                    .Field("launch_ends_in_stage", flight.LaunchEndStage);
                json.Name("margin").BeginObject()
                    .Field("dv_fraction", PlanningJson.Round(fraction, 3))
                    .Field("min_boost_vertical_speed", minVs)
                    .Field("marginal", AscentMargin.Marginal(fraction, minVs ?? double.PositiveInfinity))
                    .EndObject();
            }
            if (air.Estimated) json.Field("air", "estimated from the atmosphere's height and sea-level pressure (the Game Snapshot has no air table)");
            if (!given.HasValue)
            {
                json.Name("tried").BeginArray();
                foreach (var t in tried)
                {
                    json.BeginObject().Field("turn_shape", PlanningJson.Round(t.Item1, 2));
                    if (t.Item2.ReachesOrbit) json.Field("dv_left", Math.Round(t.Item2.DeltaVLeft));
                    else json.Field("falls_in_stage", t.Item2.FailedStage);
                    json.EndObject();
                }
                json.EndArray();
            }
            json.EndObject();

            var summary = flight.ReachesOrbit
                ? "turn_shape " + Format(best.Item1, "0.00") + " reaches orbit with " + Format(flight.DeltaVLeft) + " m/s left"
                : "can't reach orbit: stage " + flight.FailedStage.ToString(CultureInfo.InvariantCulture) + " falls at " + Format(flight.FailedAltitude) + " m";
            return new ToolOutcome { Json = json.ToString(), Summary = summary };
        }

        private static List<double> Shapes()
        {
            var shapes = new List<double>();
            for (var i = 0; FirstShape + i * ShapeStep <= LastShape + 1e-9; i++) shapes.Add(Math.Round(FirstShape + i * ShapeStep, 2));
            return shapes;
        }

        /// <summary>A row per stage: the ones that lit with their role and the craft when they did, then the ones still to come with their delta-v.</summary>
        private static void Stages(JsonWriter json, AscentFlight flight, List<AscentStage> stages, int stageLimit)
        {
            json.Name("stages").BeginArray();
            foreach (var stage in stages.Where(s => s.Burns))
            {
                json.BeginObject().Field("stage", stage.Number);
                var lit = flight.Lit.FirstOrDefault(l => l.Stage == stage.Number);
                if (lit != null)
                {
                    json.Field("role", lit.Suborbital ? "Boost stage" : "Orbital stage")
                        .Name("lights").BeginObject()
                        .Field("altitude", Math.Round(lit.Altitude))
                        .Field("speed", Math.Round(lit.Speed))
                        .Field("apoapsis", double.IsInfinity(lit.Apoapsis) ? (double?)null : Math.Round(lit.Apoapsis))
                        .Field("periapsis", Math.Round(lit.Periapsis))
                        .Field("time_to_ap", double.IsNaN(lit.TimeToAp) ? (double?)null : Math.Round(lit.TimeToAp))
                        .EndObject()
                        .Field("twr", PlanningJson.Round(lit.Twr, 2));
                }
                else
                {
                    // Not lit by the ascent: it lights in orbit (if the ascent got there), kept by stage_limit or left over.
                    json.Field("role", flight.ReachesOrbit ? "Orbital stage" : "not lit");
                    if (stage.Number < stageLimit) json.Field("kept", true);
                }
                json.Field("burn_time", PlanningJson.Round(stage.BurnTime, 1)).Field("dv_vac", Math.Round(stage.DeltaVVac)).EndObject();
            }
            json.EndArray();
        }

        private static string Format(double value, string format = "0")
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
