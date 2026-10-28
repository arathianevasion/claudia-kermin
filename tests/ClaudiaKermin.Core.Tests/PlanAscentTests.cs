// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>plan_ascent</c> through the Planning Tools entry point, on hand-built craft at Kerbin's pad: what Claudia reads in the
    /// JSON result, not the sim's insides.
    /// </summary>
    public sealed class PlanAscentTests
    {
        private const double G0 = 9.80665;
        private readonly PlanningTools _tools = PlanningTools.Default();
        private readonly ITestOutputHelper _out;

        public PlanAscentTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>A stage as the stock simulator gives it: vacuum and sea-level thrust (kN), vacuum Isp, start and end mass (t).</summary>
        private sealed class StageSpec
        {
            public double Vac;
            public double Asl;
            public double Isp;
            public double Start;
            public double End;
        }

        private static StageSpec S(double vac, double asl, double isp, double start, double end)
        {
            return new StageSpec { Vac = vac, Asl = asl, Isp = isp, Start = start, End = end };
        }

        /// <summary>Kerbin's air every 1400 m: pressure falling e-fold every 5600 m, 1.225 kg/m³ at sea level.</summary>
        private static SnapshotBody Kerbin()
        {
            var body = new SnapshotBody { Name = "Kerbin", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, RotationPeriod = 21549.425 };
            for (var h = 0.0; h <= 70000; h += 1400)
            {
                var atm = h < 70000 ? Math.Exp(-h / 5600) : 0;
                body.Air.Add(new[] { h, atm, 1.225 * atm, 340 - 0.004 * Math.Min(h, 12000) });
            }
            return body;
        }

        /// <summary>A craft on Kerbin's pad with <paramref name="stages"/> from the first to fire to the last (numbered down to 0).</summary>
        private static GameSnapshot Craft(params StageSpec[] stages)
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "PRELAUNCH", DeltaVSimulator = true };
            snapshot.Bodies.Add(Kerbin());
            snapshot.Bodies.Add(new SnapshotBody { Name = "Mun", Gm = 6.5138398e10, Radius = 200000 });
            snapshot.Craft.NoseDrag.AddRange(new[] { new[] { 0.0, 1.0 }, new[] { 0.8, 1.0 }, new[] { 1.1, 2.2 }, new[] { 1.5, 1.4 }, new[] { 3.0, 1.0 }, new[] { 10.0, 0.8 } });
            for (var i = 0; i < stages.Length; i++)
            {
                var s = stages[i];
                var number = stages.Length - 1 - i;
                snapshot.Craft.Parts.Add(new SnapshotPart
                {
                    Id = (uint)(i + 1), Name = "engine" + number, Stage = number, MassT = 1, DryMassT = 1,
                    ThrustVacKn = s.Vac, ThrustAslKn = s.Asl, IspVac = s.Isp,
                    IspCurve = { new[] { 0.0, s.Isp }, new[] { 1.0, s.Isp * s.Asl / s.Vac } },
                });
                snapshot.Stages.Add(new SnapshotStage
                {
                    Number = number, ThrustVacKn = s.Vac, StartMassT = s.Start, EndMassT = s.End,
                    DeltaVVac = s.Isp * G0 * Math.Log(s.Start / s.End),
                });
            }
            return snapshot;
        }

        private JsonElement Run(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("plan_ascent", input, snapshot, new NoPlanningBody());
            _out.WriteLine(outcome.Json);
            Assert.False(outcome.IsError, outcome.Json);
            Assert.True(outcome.Json.Length <= PlanningTools.MaxResultChars, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("plan_ascent", input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        // ---- the craft ----

        /// <summary>A Reliant Boost stage lighting on the pad (TWR about 1.7), then a Terrier stage with plenty to spare.</summary>
        private static GameSnapshot Strong()
        {
            return Craft(S(240, 205, 310, 12.0, 5.5), S(60, 14, 345, 3.5, 1.4));
        }

        /// <summary>
        /// Mun Shot's shape (#367): a strong stage on the pad, then a small stage at TWR under 0.4 that lights suborbital in the air,
        /// then a strong stage. The weak stage falls unless the climb before it was steep.
        /// </summary>
        private static GameSnapshot WeakBoost()
        {
            return Craft(S(500, 450, 300, 27, 16), S(45, 40, 320, 12, 10.5), S(240, 60, 345, 9, 3));
        }

        /// <summary>The same with a heavier pad stage and a weaker one after it: it falls whatever the turn.</summary>
        private static GameSnapshot Hopeless()
        {
            return Craft(S(500, 450, 300, 27, 17), S(40, 35, 320, 12, 10.5), S(240, 60, 345, 9, 3));
        }

        private static JsonElement Stage(JsonElement result, int number)
        {
            return result.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetInt32() == number);
        }

        private static double[] Tried(JsonElement result)
        {
            return result.GetProperty("tried").EnumerateArray().Select(t => t.GetProperty("turn_shape").GetDouble()).ToArray();
        }

        // ---- reaching orbit ----

        [Fact]
        public void A_strong_craft_reaches_orbit_with_the_shape_that_leaves_the_most_delta_v()
        {
            var result = Run(Strong(), "{\"target_ap\":80000}");
            Assert.True(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.Equal(80000, result.GetProperty("target_ap").GetDouble());
            Assert.Equal(new[] { 0.2, 0.25, 0.3, 0.35, 0.4, 0.45, 0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8 }, Tried(result));
            var shape = result.GetProperty("turn_shape").GetDouble();
            var best = result.GetProperty("tried").EnumerateArray().Where(t => t.TryGetProperty("dv_left", out _)).Max(t => t.GetProperty("dv_left").GetDouble());
            Assert.Equal(best, result.GetProperty("dv_left").GetDouble());
            Assert.Equal(best, result.GetProperty("tried").EnumerateArray().Single(t => t.GetProperty("turn_shape").GetDouble() == shape).GetProperty("dv_left").GetDouble());
            // Delta-v to orbit is Kerbin's few thousand m/s; the margin is the fraction left.
            var toOrbit = result.GetProperty("dv_to_orbit").GetDouble();
            Assert.InRange(toOrbit, 3000, 4200);
            var margin = result.GetProperty("margin");
            Assert.Equal(Math.Round(result.GetProperty("dv_left").GetDouble() / toOrbit, 3), margin.GetProperty("dv_fraction").GetDouble(), 2);
            Assert.False(margin.GetProperty("marginal").GetBoolean());
            Assert.True(margin.GetProperty("min_boost_vertical_speed").GetDouble() >= 20);
            Assert.Equal(0, result.GetProperty("dv_kept").GetDouble());
            // The stage that lights on the pad: a Boost stage, at TWR about 1.7.
            var first = Stage(result, 1);
            Assert.Equal("Boost stage", first.GetProperty("role").GetString());
            Assert.Equal(0, first.GetProperty("lights").GetProperty("altitude").GetDouble());
            Assert.Equal(205.0 / (12 * 9.81), first.GetProperty("twr").GetDouble(), 1);
        }

        [Fact]
        public void A_weak_boost_stage_falls_at_the_default_shape_and_reaches_orbit_on_a_steeper_climb()
        {
            var at45 = Run(WeakBoost(), "{\"target_ap\":80000,\"turn_shape\":0.45}");
            Assert.False(at45.GetProperty("reaches_orbit").GetBoolean());
            Assert.Equal(0.45, at45.GetProperty("best_turn_shape").GetDouble());
            Assert.Equal(1, at45.GetProperty("failed_stage").GetInt32());
            Assert.Equal("Boost stage", at45.GetProperty("failed_role").GetString());
            Assert.Contains("vertical speed", at45.GetProperty("failure").GetString());
            Assert.InRange(at45.GetProperty("failed_altitude").GetDouble(), 1, 70000);
            Assert.False(at45.TryGetProperty("tried", out _));
            // The weak stage lights suborbital in the air at a TWR well under 1.
            var weak = Stage(at45, 1);
            Assert.Equal("Boost stage", weak.GetProperty("role").GetString());
            Assert.InRange(weak.GetProperty("twr").GetDouble(), 0.2, 0.6);
            Assert.True(weak.GetProperty("lights").GetProperty("periapsis").GetDouble() < 0);

            var searched = Run(WeakBoost(), "{\"target_ap\":80000}");
            Assert.True(searched.GetProperty("reaches_orbit").GetBoolean());
            Assert.True(searched.GetProperty("turn_shape").GetDouble() > 0.45);
            var at45Tried = searched.GetProperty("tried").EnumerateArray().Single(t => t.GetProperty("turn_shape").GetDouble() == 0.45);
            Assert.Equal(1, at45Tried.GetProperty("falls_in_stage").GetInt32());
        }

        [Fact]
        public void A_hopeless_craft_cant_reach_orbit_with_any_shape_and_names_the_boost_stage_that_falls()
        {
            var result = Run(Hopeless(), "{\"target_ap\":80000}");
            Assert.False(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.Equal(1, result.GetProperty("failed_stage").GetInt32());
            Assert.Equal("Boost stage", result.GetProperty("failed_role").GetString());
            Assert.True(result.TryGetProperty("best_turn_shape", out _));
            Assert.False(result.TryGetProperty("turn_shape", out _));
            Assert.False(result.TryGetProperty("margin", out _));
            Assert.All(result.GetProperty("tried").EnumerateArray(), t => Assert.Equal(1, t.GetProperty("falls_in_stage").GetInt32()));
            // The stage after it never lights.
            Assert.Equal("not lit", Stage(result, 0).GetProperty("role").GetString());
            var outcome = _tools.Run("plan_ascent", "{\"target_ap\":80000}", Hopeless(), new NoPlanningBody());
            Assert.StartsWith("can't reach orbit: stage 1 falls at ", outcome.Summary);
        }

        // Apoapsis at target_ap is not orbit: a climb so steep that the launch cuts off slow leaves a circularization the
        // stages can't finish before the craft is back where the air matters.
        [Fact]
        public void Reaching_target_ap_is_not_orbit_when_the_stages_left_cant_circularize_before_the_air()
        {
            var result = Run(Craft(S(500, 450, 300, 27, 16), S(50, 44, 320, 12, 10.2), S(240, 60, 345, 9, 3)), "{\"target_ap\":80000,\"turn_shape\":0.75}");
            Assert.False(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.InRange(result.GetProperty("coast_ap").GetDouble(), 79000, 81000);
            Assert.Contains("circulariz", result.GetProperty("failure").GetString());
        }

        // ---- roles ----

        [Fact]
        public void A_stage_that_lights_suborbital_stays_a_boost_stage_though_it_reaches_orbit_and_stages_after_orbit_are_orbital()
        {
            var result = Run(Craft(S(240, 205, 310, 13.0, 6.5), S(60, 14, 345, 4.5, 1.9), S(20, 5, 345, 0.8, 0.5)), "{\"target_ap\":80000}");
            Assert.True(result.GetProperty("reaches_orbit").GetBoolean(), result.ToString());
            // Stage 1 lights suborbital and finishes the orbit itself.
            Assert.Equal(1, result.GetProperty("stages").EnumerateArray().Count(s => s.GetProperty("stage").GetInt32() == 1));
            var circularizer = Stage(result, 1);
            Assert.Equal("Boost stage", circularizer.GetProperty("role").GetString());
            Assert.True(circularizer.GetProperty("lights").GetProperty("periapsis").GetDouble() < 70000);
            Assert.InRange(circularizer.GetProperty("lights").GetProperty("altitude").GetDouble(), 1, 70000);
            Assert.True(circularizer.GetProperty("lights").GetProperty("time_to_ap").GetDouble() > 0);
            // Stage 0 lights only once in orbit.
            var orbital = Stage(result, 0);
            Assert.Equal("Orbital stage", orbital.GetProperty("role").GetString());
            Assert.False(orbital.TryGetProperty("lights", out _));
            Assert.Equal(Math.Round(345 * G0 * Math.Log(0.8 / 0.5)), orbital.GetProperty("dv_vac").GetDouble());
        }

        // ---- stage_limit ----

        [Fact]
        public void Stage_limit_keeps_the_stages_below_it_and_reports_their_delta_v()
        {
            var craft = Craft(S(240, 205, 310, 13.0, 6.5), S(60, 14, 345, 4.5, 1.9), S(20, 5, 345, 0.8, 0.5));
            var all = Run(craft, "{\"target_ap\":80000}");
            var kept = Run(craft, "{\"target_ap\":80000,\"stage_limit\":1}");
            Assert.True(kept.GetProperty("reaches_orbit").GetBoolean());
            Assert.Equal(1, kept.GetProperty("stage_limit").GetInt32());
            var stage0 = 345 * G0 * Math.Log(0.8 / 0.5);
            Assert.Equal(stage0, kept.GetProperty("dv_kept").GetDouble(), 0);
            Assert.True(Stage(kept, 0).GetProperty("kept").GetBoolean());
            // What the kept stage holds is not the launch's to burn.
            Assert.Equal(all.GetProperty("dv_left").GetDouble() - stage0, kept.GetProperty("dv_left").GetDouble(), 0);

            // Stage 1 alone can't make orbit: the launch runs dry at the limit, which says so.
            var weak = Run(Strong(), "{\"target_ap\":80000,\"stage_limit\":1,\"turn_shape\":0.45}");
            Assert.False(weak.GetProperty("reaches_orbit").GetBoolean());
            Assert.Contains("at stage limit 1", weak.GetProperty("failure").GetString());
            Assert.True(Stage(weak, 0).GetProperty("kept").GetBoolean());
        }

        // ---- inputs ----

        [Fact]
        public void Target_ap_auto_is_the_atmosphere_top_and_10_km_and_a_hop_is_refused()
        {
            Assert.Equal(80000, Run(Strong(), "{\"target_ap\":\"auto\",\"turn_shape\":0.45}").GetProperty("target_ap").GetDouble());
            Assert.Equal(80000, Run(Strong(), "{\"turn_shape\":0.45}").GetProperty("target_ap").GetDouble());
            Assert.Contains("not above the atmosphere top", Error(Strong(), "{\"target_ap\":60000}"));
            Assert.Equal("target_ap must be \"auto\" or m", Error(Strong(), "{\"target_ap\":\"high\"}"));
            Assert.Equal("throttle_max_q must be \"none\" or Pa", Error(Strong(), "{\"throttle_max_q\":\"off\"}"));
        }

        [Fact]
        public void A_given_turn_start_speed_and_throttle_max_q_change_the_climb()
        {
            var plain = Run(Strong(), "{\"turn_shape\":0.45}");
            var held = Run(Strong(), "{\"turn_shape\":0.45,\"throttle_max_q\":12000,\"turn_start_speed\":80}");
            Assert.True(held.GetProperty("reaches_orbit").GetBoolean());
            Assert.NotEqual(plain.GetProperty("dv_left").GetDouble(), held.GetProperty("dv_left").GetDouble());
            Assert.Equal(plain.GetProperty("turn_shape").GetDouble(), held.GetProperty("turn_shape").GetDouble());
        }

        // ---- errors ----

        [Fact]
        public void An_airless_body_has_no_launch_to_plan_and_points_at_stage_performance()
        {
            var snapshot = Strong();
            snapshot.Body = "Mun";
            var error = Error(snapshot, "{}");
            Assert.Contains("Mun has no air", error);
            Assert.Contains("stage_performance", error);
        }

        [Fact]
        public void A_snapshot_without_the_drag_table_is_an_error()
        {
            var snapshot = Strong();
            snapshot.Craft.NoseDrag.Clear();
            Assert.Equal("the Game Snapshot has no drag table for the craft", Error(snapshot, "{}"));
        }

        [Fact]
        public void A_snapshot_without_air_rows_estimates_the_air_and_says_so()
        {
            var snapshot = Strong();
            snapshot.Bodies[0].Air.Clear();
            var result = Run(snapshot, "{\"turn_shape\":0.45}");
            Assert.True(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.Contains("estimated", result.GetProperty("air").GetString());
            Assert.False(Run(Strong(), "{\"turn_shape\":0.45}").TryGetProperty("air", out _));
        }

        // ---- the tool ----

        [Fact]
        public void The_description_stays_within_900_characters_and_names_launch()
        {
            var definition = _tools.Definitions(ToolOffer.CraftCheck).Single(t => t.Name == "plan_ascent");
            Assert.InRange(definition.Description.Length, 1, 900);
            Assert.Contains("Its block is launch", definition.Description);
            Assert.Contains("Don't call it", definition.Description);
            Assert.Contains("Boost stage", definition.Description);
            Assert.Contains("Orbital stage", definition.Description);
        }

        [Fact]
        public void The_air_rows_survive_a_round_trip_of_the_recording()
        {
            var snapshot = Strong();
            var parsed = GameSnapshot.Parse(snapshot.ToJson());
            Assert.Equal(snapshot.Bodies[0].Air.Count, parsed.Bodies[0].Air.Count);
            Assert.Equal(snapshot.Bodies[0].Air[3], parsed.Bodies[0].Air[3]);
            Assert.Empty(parsed.Bodies[1].Air);
            Assert.DoesNotContain("\"air\"", snapshot.ToJson(false));
        }
    }
}
