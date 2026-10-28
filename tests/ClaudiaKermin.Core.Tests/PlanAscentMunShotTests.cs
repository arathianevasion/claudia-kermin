// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>plan_ascent</c> on Snapshots recorded in game (#373): Mun Shot on the pad and Mun Shot v2 from the editor, checked
    /// against what the real flights did.
    /// </summary>
    public sealed class PlanAscentMunShotTests
    {
        // Stage 5 is the LV-T45 Swivel core burning alone once the Reliant boosters are gone (TWR about 0.8).
        private const int SwivelStage = 5;

        private readonly PlanningTools _tools = PlanningTools.Default();
        private readonly ITestOutputHelper _out;

        public PlanAscentMunShotTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static GameSnapshot Load(string name)
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", name)));
        }

        private JsonElement Run(string fixture, string input)
        {
            var outcome = _tools.Run("plan_ascent", input, Load(fixture), new NoPlanningBody());
            _out.WriteLine(outcome.Json);
            Assert.False(outcome.IsError, outcome.Json);
            Assert.True(outcome.Json.Length <= PlanningTools.MaxResultChars, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private static JsonElement Stage(JsonElement result, int number)
        {
            return result.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetInt32() == number);
        }

        [Fact]
        public void Mun_Shot_at_the_default_shape_falls_in_the_Swivel_Boost_stage()
        {
            var result = Run("mun-shot-pad.json", "{\"target_ap\":80000,\"turn_shape\":0.45}");
            Assert.False(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.Equal(SwivelStage, result.GetProperty("failed_stage").GetInt32());
            Assert.Equal("Boost stage", result.GetProperty("failed_role").GetString());
            Assert.Equal("Boost stage", Stage(result, SwivelStage).GetProperty("role").GetString());
            Assert.InRange(Stage(result, SwivelStage).GetProperty("twr").GetDouble(), 0.6, 1.0);
            Assert.InRange(result.GetProperty("failed_altitude").GetDouble(), 1, 70000);
        }

        // The flight reached ap 79.8 km with pe 62.9 km, still in the air at 333 s. The sim does not count that as orbit, though
        // for a different reason: it has the Terrier stage (4) run out of vertical speed at about 69 km, not the burn ending
        // flat. Both are "not cleanly to orbit"; the sim does not model the flat finish.
        [Fact]
        public void Mun_Shot_at_shape_0_65_with_a_late_turn_is_not_counted_as_reaching_orbit()
        {
            var result = Run("mun-shot-pad.json", "{\"target_ap\":80000,\"turn_shape\":0.65,\"turn_start_speed\":80}");
            Assert.False(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.False(result.TryGetProperty("margin", out _));
            Assert.Equal("Boost stage", result.GetProperty("failed_role").GetString());
            Assert.True(result.GetProperty("failed_altitude").GetDouble() < 70000);
        }

        [Fact]
        public void The_search_finds_Mun_Shot_a_shape_that_reaches_80_km()
        {
            var result = Run("mun-shot-pad.json", "{\"target_ap\":80000}");
            Assert.True(result.GetProperty("reaches_orbit").GetBoolean());
            var shape = result.GetProperty("turn_shape").GetDouble();
            Assert.True(shape > 0.45);
            var at45 = result.GetProperty("tried").EnumerateArray().Single(t => t.GetProperty("turn_shape").GetDouble() == 0.45);
            Assert.Equal(SwivelStage, at45.GetProperty("falls_in_stage").GetInt32());
            // Flying the shape it picked on its own gives the same answer.
            var again = Run("mun-shot-pad.json", "{\"target_ap\":80000,\"turn_shape\":" + shape.ToString(CultureInfo.InvariantCulture) + "}");
            Assert.True(again.GetProperty("reaches_orbit").GetBoolean());
        }

        [Fact]
        public void Mun_Shot_v2_reaches_80_km_with_a_margin_as_its_clean_flights_at_the_defaults_say()
        {
            var result = Run("mun-shot-v2-editor.json", "{\"target_ap\":80000}");
            Assert.True(result.GetProperty("reaches_orbit").GetBoolean());
            Assert.False(result.TryGetProperty("failed_stage", out _));
            Assert.False(result.GetProperty("margin").GetProperty("marginal").GetBoolean());
            var atDefaults = Run("mun-shot-v2-editor.json", "{\"target_ap\":80000,\"turn_shape\":0.45}");
            Assert.True(atDefaults.GetProperty("reaches_orbit").GetBoolean());
            Assert.False(atDefaults.GetProperty("margin").GetProperty("marginal").GetBoolean());
        }
    }
}
