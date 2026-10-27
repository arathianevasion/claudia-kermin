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
    /// <c>stage_performance</c>: a row per stage with delta-v, TWR at a body's surface gravity, Isp, burn time and start and end
    /// mass, plus totals, in vacuum or at the body's sea-level pressure. It works from the stock simulator's stage figures in
    /// the Game Snapshot. A stage whose reading is unreliable (stock reads 0 at a trickle throttle with an engine lit, #234) is
    /// recomputed with the rocket equation from its engines; when stock gives no end mass for it either, the end mass is the
    /// propellant aboard less what the later stages burn.
    /// </summary>
    internal sealed class StagePerformance : IPlanningTool
    {
        private const double G0 = 9.80665;

        public string Name
        {
            get { return "stage_performance"; }
        }

        public string Description
        {
            get
            {
                return "Each stage's delta-v, thrust-to-weight ratio at a body's surface gravity, Isp, burn time, thrust and start and "
                    + "end mass, plus totals, in vacuum or at that body's sea-level pressure, worked out from the craft's stage figures "
                    + "in this turn's Game Snapshot (a stage the stock readout shows as 0 at a trickle throttle is recomputed with the "
                    + "rocket equation). SI units: m/s, s, N, kg. Its blocks are the ones that burn (launch, ascend, "
                    + "land, deorbit, return and the rest): call it to check the craft against a leg's delta-v budget, or a launch's or "
                    + "landing's TWR, and what each stage holds before you shed tanks with drop_empty, instead of working the rocket equation out "
                    + "yourself. Don't call it for where a burn leaves the orbit, or again when the craft hasn't changed since you last did.";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"body\":{\"type\":\"string\",\"description\":\"The body whose surface gravity (and, at the surface, pressure) to use; defaults to the body the craft is at.\"},"
                    + "\"situation\":{\"type\":\"string\",\"enum\":[\"vacuum\",\"surface\"],\"description\":\"vacuum, or surface for the body's sea-level pressure.\"}"
                    + "},\"required\":[\"situation\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return true; }
        }

        public string Activity
        {
            get { return "checking stage performance"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
        {
            var name = JsonNode.Str(input["body"]) ?? snapshot.Body;
            var surface = JsonNode.Str(input["situation"]) == "surface";
            if (snapshot.Bodies.Count == 0) return PlanningJson.Error("the Game Snapshot has no body table");
            var at = snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at == null)
                return PlanningJson.Error("there is no body named " + PlanningJson.Quote(name) + "; the bodies are "
                    + string.Join(", ", snapshot.Bodies.Select(b => b.Name).ToArray()));
            if (!snapshot.DeltaVSimulator) return PlanningJson.Error("the stock delta-v simulator is off, so there are no stage figures");
            if (snapshot.Stages.Count == 0) return PlanningJson.Error("the Game Snapshot has no stages");

            var atm = surface ? at.SeaLevelAtm : 0;
            var gravity = at.SurfaceGravity;
            var rows = Rows(snapshot, atm, gravity);

            var json = new JsonWriter().BeginObject()
                .Field("body", at.Name)
                .Field("situation", surface ? "surface" : "vacuum")
                .Field("pressure_atm", PlanningJson.Round(atm, 3))
                .Field("surface_gravity", PlanningJson.Round(gravity, 3));
            json.Name("stages").BeginArray();
            foreach (var row in rows)
            {
                json.BeginObject().Field("stage", row.Stage)
                    .Field("dv", PlanningJson.Round(row.DeltaV, 1))
                    .Field("twr", PlanningJson.Round(row.Twr, 2))
                    .Field("isp", PlanningJson.Round(row.Isp, 1))
                    .Field("burn_time", PlanningJson.Round(row.BurnTime, 1))
                    .Field("thrust", PlanningJson.Round(row.Thrust, 0))
                    .Field("start_mass", PlanningJson.Round(row.StartMass, 0))
                    .Field("end_mass", PlanningJson.Round(row.EndMass, 0));
                if (row.Recomputed.Length > 0) json.Field("recomputed", row.Recomputed);
                json.EndObject();
            }
            json.EndArray();
            var total = rows.Sum(r => r.DeltaV);
            json.Field("total_dv", PlanningJson.Round(total, 1))
                .Field("total_burn_time", PlanningJson.Round(rows.Sum(r => r.BurnTime), 1));
            json.EndObject();

            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = rows.Count.ToString(CultureInfo.InvariantCulture) + (rows.Count == 1 ? " stage, " : " stages, ")
                    + Math.Round(total).ToString("0", CultureInfo.InvariantCulture) + " m/s in all " + (surface ? "at the surface of " : "in vacuum at ") + at.Name,
            };
        }

        /// <summary>A row per stage at <paramref name="atm"/> atmospheres, with TWR at <paramref name="gravity"/>; <c>predict_burn</c> reads the vacuum rows for burn times.</summary>
        internal static List<StageRow> Rows(GameSnapshot snapshot, double atm, double gravity)
        {
            return snapshot.Stages.Select(s => Row(snapshot, s, atm, gravity)).ToList();
        }

        internal sealed class StageRow
        {
            public int Stage;
            public double DeltaV;
            public double Twr;
            public double Isp;
            public double BurnTime;
            /// <summary>N.</summary>
            public double Thrust;
            /// <summary>kg.</summary>
            public double StartMass;
            public double EndMass;
            /// <summary>Why the stage was recomputed rather than read; "" when it was read.</summary>
            public string Recomputed = "";
        }

        private static StageRow Row(GameSnapshot snapshot, SnapshotStage stage, double atm, double gravity)
        {
            var row = new StageRow { Stage = stage.Number, StartMass = stage.StartMassT * 1000, EndMass = stage.EndMassT * 1000 };
            // The engines that can burn in this stage: lit in it or in an earlier stage and still aboard (as SeaLevelThrust.ForStage counts them).
            var engines = snapshot.Craft.Parts.Where(p => p.ThrustVacKn > 0 && p.Stage >= stage.Number).ToList();
            var thrustVac = stage.ThrustVacKn > 0 || !stage.DeltaVUnreliable ? stage.ThrustVacKn : engines.Sum(e => e.ThrustVacKn);

            double ispVac;
            if (!stage.DeltaVUnreliable && stage.DeltaVVac > 0 && row.StartMass > row.EndMass && row.EndMass > 0)
            {
                // Stock's own figure, turned back into the Isp it implies, so vacuum delta-v is exactly stock's.
                ispVac = stage.DeltaVVac / (G0 * Math.Log(row.StartMass / row.EndMass));
            }
            else
            {
                var flow = engines.Where(e => e.IspVac > 0).Sum(e => e.ThrustVacKn / e.IspVac);
                ispVac = flow > 0 ? engines.Where(e => e.IspVac > 0).Sum(e => e.ThrustVacKn) / flow : 0;
            }

            if (stage.DeltaVUnreliable)
            {
                row.Recomputed = "rocket equation: the stock readout is 0 at a trickle throttle";
                if (!(row.EndMass < row.StartMass))
                {
                    row.EndMass = EstimatedEndMass(snapshot, stage, row.StartMass);
                    row.Recomputed += "; end mass from the propellant aboard less what later stages burn";
                }
            }

            var thrust = atm <= 0 ? thrustVac
                : stage.ThrustVacKn > 0 ? SeaLevelThrust.ForStage(snapshot, stage, atm) : SeaLevelThrust.Of(engines, atm);
            // The fuel flow is fixed, so thrust and Isp fall together with pressure.
            row.Isp = thrustVac > 0 ? ispVac * thrust / thrustVac : 0;
            row.Thrust = thrust * 1000;
            if (row.Isp > 0 && row.EndMass > 0 && row.EndMass < row.StartMass)
                row.DeltaV = row.Isp * G0 * Math.Log(row.StartMass / row.EndMass);
            if (gravity > 0 && row.StartMass > 0) row.Twr = row.Thrust / (row.StartMass * gravity);
            if (row.Thrust > 0 && row.Isp > 0) row.BurnTime = (row.StartMass - row.EndMass) * G0 * row.Isp / row.Thrust;
            return row;
        }

        /// <summary>The stage's start mass less the propellant aboard that the later stages don't burn (kg); its start mass when there is none.</summary>
        private static double EstimatedEndMass(GameSnapshot snapshot, SnapshotStage stage, double startMass)
        {
            var aboard = snapshot.Craft.Parts.Where(p => p.DryMassT > 0 && p.MassT > p.DryMassT).Sum(p => (p.MassT - p.DryMassT) * 1000);
            var later = snapshot.Stages.Where(s => s.Number < stage.Number && s.StartMassT > s.EndMassT).Sum(s => (s.StartMassT - s.EndMassT) * 1000);
            var burnt = Math.Max(0, aboard - later);
            return Math.Max(0, startMass - burnt);
        }
    }
}
