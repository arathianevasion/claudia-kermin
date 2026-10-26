// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>stage_performance</c> through the Planning Tools entry point, against the recorded on-pad Snapshot and hand-built ones,
    /// checked against the rocket equation worked by hand.
    /// </summary>
    public sealed class StagePerformanceTests
    {
        private const double G0 = 9.80665;
        private readonly PlanningTools _tools = PlanningTools.Default();

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private JsonElement Run(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("stage_performance", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("stage_performance", input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        private static JsonElement Stage(JsonElement result, int number)
        {
            return result.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetInt32() == number);
        }

        [Fact]
        public void In_vacuum_the_recorded_stage_keeps_stock_delta_v_and_gets_isp_twr_and_burn_time_from_the_rocket_equation()
        {
            var result = Run(OnPad(), "{\"situation\":\"vacuum\"}");
            Assert.Equal("Kerbin", result.GetProperty("body").GetString());
            Assert.Equal("vacuum", result.GetProperty("situation").GetString());
            var stage = Stage(result, 0);
            var isp = 3200 / (G0 * Math.Log(4360.0 / 1900.0));
            Assert.Equal(3200, stage.GetProperty("dv").GetDouble(), 1);
            Assert.Equal(isp, stage.GetProperty("isp").GetDouble(), 1);
            // Kerbin's surface gravity is GM / R² = 9.81 m/s².
            Assert.Equal(60000 / (4360 * 9.81), stage.GetProperty("twr").GetDouble(), 2);
            Assert.Equal((4360 - 1900) * G0 * isp / 60000, stage.GetProperty("burn_time").GetDouble(), 1);
            Assert.Equal(60000, stage.GetProperty("thrust").GetDouble());
            Assert.Equal(4360, stage.GetProperty("start_mass").GetDouble());
            Assert.Equal(1900, stage.GetProperty("end_mass").GetDouble());
            Assert.False(stage.TryGetProperty("recomputed", out _));
            Assert.Equal(3200, result.GetProperty("total_dv").GetDouble(), 1);
            Assert.Equal(stage.GetProperty("burn_time").GetDouble(), result.GetProperty("total_burn_time").GetDouble(), 1);
        }

        [Fact]
        public void At_kerbins_surface_thrust_and_isp_fall_with_the_engines_isp_curve_and_the_burn_time_stays_the_same()
        {
            var vacuum = Stage(Run(OnPad(), "{\"situation\":\"vacuum\"}"), 0);
            var surface = Run(OnPad(), "{\"situation\":\"surface\",\"body\":\"Kerbin\"}");
            Assert.Equal(1, surface.GetProperty("pressure_atm").GetDouble());
            var stage = Stage(surface, 0);
            // The Terrier's Isp is 287.5 s at 1 atm against 345 s in vacuum: thrust 50 kN, and delta-v scales the same way.
            Assert.Equal(50000, stage.GetProperty("thrust").GetDouble());
            Assert.Equal(3200 * 287.5 / 345, stage.GetProperty("dv").GetDouble(), 1);
            Assert.Equal(50000 / (4360 * 9.81), stage.GetProperty("twr").GetDouble(), 2);
            Assert.Equal(vacuum.GetProperty("burn_time").GetDouble(), stage.GetProperty("burn_time").GetDouble(), 1);
        }

        [Fact]
        public void The_twr_is_at_the_named_bodys_surface_gravity_and_an_airless_surface_is_vacuum()
        {
            var mun = Run(OnPad(), "{\"situation\":\"surface\",\"body\":\"mun\"}");
            Assert.Equal("Mun", mun.GetProperty("body").GetString());
            Assert.Equal(0, mun.GetProperty("pressure_atm").GetDouble());
            var munGravity = 6.5138398e10 / (200000.0 * 200000.0);
            Assert.Equal(munGravity, mun.GetProperty("surface_gravity").GetDouble(), 3);
            Assert.Equal(60000 / (4360 * munGravity), Stage(mun, 0).GetProperty("twr").GetDouble(), 2);
            Assert.Equal(3200, Stage(mun, 0).GetProperty("dv").GetDouble(), 1);
        }

        private static GameSnapshot Trickle(double endMassT)
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "FLYING", DeltaVSimulator = true, Throttle = 0.0006 };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Kerbin", Gm = 3.5316e12, Radius = 600000, SeaLevelAtm = 1 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1, Name = "probe", Stage = 0, MassT = 0.5, DryMassT = 0.5 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 2, Name = "tank", Stage = 1, MassT = 2.0, DryMassT = 0.25 });
            snapshot.Craft.Parts.Add(new SnapshotPart
            {
                Id = 3, Name = "terrier", Stage = 2, MassT = 0.5, DryMassT = 0.5, ThrustVacKn = 60, ThrustAslKn = 50, IspVac = 345,
                IspCurve = { new[] { 0.0, 345.0 }, new[] { 1.0, 287.5 } },
            });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 4, Name = "tank2", Stage = 0, MassT = 1.0, DryMassT = 0.8 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 5, Name = "spark", Stage = 0, MassT = 0.1, DryMassT = 0.1, ThrustVacKn = 20, IspVac = 320 });
            snapshot.Stages.Add(new SnapshotStage { Number = 2, DeltaVVac = 0, DeltaVUnreliable = true, ThrustVacKn = 60, StartMassT = 4.0, EndMassT = endMassT });
            var laterIsp = 320.0;
            snapshot.Stages.Add(new SnapshotStage { Number = 0, DeltaVVac = laterIsp * G0 * Math.Log(1.6 / 1.4), ThrustVacKn = 20, StartMassT = 1.6, EndMassT = 1.4 });
            return snapshot;
        }

        [Fact]
        public void A_trickle_stage_is_recomputed_with_the_rocket_equation_from_its_engines_isp()
        {
            var snapshot = Trickle(2.25);
            Assert.True(snapshot.DeltaVUnreliable);
            var result = Run(snapshot, "{\"situation\":\"vacuum\"}");
            var stage = Stage(result, 2);
            Assert.Equal(345 * G0 * Math.Log(4.0 / 2.25), stage.GetProperty("dv").GetDouble(), 1);
            Assert.Equal(345, stage.GetProperty("isp").GetDouble(), 1);
            Assert.Equal(1750 * G0 * 345 / 60000, stage.GetProperty("burn_time").GetDouble(), 1);
            Assert.StartsWith("rocket equation", stage.GetProperty("recomputed").GetString());
            // The later stage is read as stock gave it, and the total counts both.
            var later = Stage(result, 0);
            Assert.Equal(320 * G0 * Math.Log(1.6 / 1.4), later.GetProperty("dv").GetDouble(), 1);
            Assert.Equal(stage.GetProperty("dv").GetDouble() + later.GetProperty("dv").GetDouble(), result.GetProperty("total_dv").GetDouble(), 0);
        }

        [Fact]
        public void A_trickle_stage_with_no_stock_end_mass_burns_the_propellant_aboard_less_what_later_stages_burn()
        {
            var result = Run(Trickle(4.0), "{\"situation\":\"vacuum\"}");
            var stage = Stage(result, 2);
            // 1.75 t in the tank plus 0.2 t in tank2, less the 0.2 t stage 0 burns: 1.75 t, so 4.0 t down to 2.25 t.
            Assert.Equal(2250, stage.GetProperty("end_mass").GetDouble());
            Assert.Equal(345 * G0 * Math.Log(4.0 / 2.25), stage.GetProperty("dv").GetDouble(), 1);
            Assert.Contains("propellant aboard", stage.GetProperty("recomputed").GetString());
        }

        [Fact]
        public void A_trickle_stage_at_the_surface_uses_the_engines_sea_level_isp()
        {
            var stage = Stage(Run(Trickle(2.25), "{\"situation\":\"surface\"}"), 2);
            Assert.Equal(287.5 * G0 * Math.Log(4.0 / 2.25), stage.GetProperty("dv").GetDouble(), 1);
            Assert.Equal(50000, stage.GetProperty("thrust").GetDouble(), 0);
        }

        [Fact]
        public void Bad_input_comes_back_as_an_error_naming_the_problem()
        {
            Assert.Equal("input.situation is required", Error(OnPad(), "{}"));
            Assert.Equal("input.situation must be one of vacuum, surface", Error(OnPad(), "{\"situation\":\"orbit\"}"));
            Assert.Equal("input.body must be a string", Error(OnPad(), "{\"situation\":\"vacuum\",\"body\":3}"));
            Assert.Equal("input.stage is not a field this tool takes", Error(OnPad(), "{\"situation\":\"vacuum\",\"stage\":1}"));
            Assert.Equal("there is no body named \"Jool\"; the bodies are Kerbin, Mun, Minmus, Sun", Error(OnPad(), "{\"situation\":\"vacuum\",\"body\":\"Jool\"}"));
        }

        [Fact]
        public void With_no_stage_figures_the_tool_says_why()
        {
            var off = OnPad();
            off.DeltaVSimulator = false;
            Assert.Equal("the stock delta-v simulator is off, so there are no stage figures", Error(off, "{\"situation\":\"vacuum\"}"));
            var none = OnPad();
            none.Stages.Clear();
            Assert.Equal("the Game Snapshot has no stages", Error(none, "{\"situation\":\"vacuum\"}"));
            var noBodies = OnPad();
            noBodies.Bodies.Clear();
            Assert.Equal("the Game Snapshot has no body table", Error(noBodies, "{\"situation\":\"vacuum\"}"));
        }

        [Fact]
        public void The_summary_for_the_mission_log_gives_the_stage_count_and_total()
        {
            var outcome = _tools.Run("stage_performance", "{\"situation\":\"vacuum\"}", OnPad(), null);
            Assert.Equal("1 stage, 3200 m/s in all in vacuum at Kerbin", outcome.Summary);
        }
    }
}
