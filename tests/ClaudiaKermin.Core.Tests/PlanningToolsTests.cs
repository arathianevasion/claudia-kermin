// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The Planning Tools module's entry point and conventions, shared by every tool: errors not throws, the schema check, the size cap, the tools lists.</summary>
    public sealed class PlanningToolsTests
    {
        /// <summary>A tool for testing the module around it: it echoes, throws, or answers at a given length.</summary>
        internal sealed class FakeTool : IPlanningTool
        {
            public string Name { get; set; } = "fake";
            public string Description { get { return "A test tool."; } }
            public string InputSchemaJson
            {
                get { return "{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\"},\"items\":{\"type\":\"array\",\"maxItems\":2,\"items\":{\"type\":\"number\"}},\"flag\":{\"type\":\"boolean\"}},\"required\":[],\"additionalProperties\":false}"; }
            }
            public bool OnCraftCheck { get; set; }
            public string Activity { get { return "faking it"; } }
            public Func<JsonNode, GameSnapshot, ToolOutcome> Answer = (input, snapshot) => new ToolOutcome { Json = "{\"ok\":true}", Summary = "ok" };

            public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
            {
                return Answer(input, snapshot);
            }
        }

        private static string ErrorOf(ToolOutcome outcome)
        {
            Assert.True(outcome.IsError);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        [Fact]
        public void An_unknown_tool_bad_json_or_no_snapshot_is_an_error_result_never_a_throw()
        {
            var tools = new PlanningTools(new IPlanningTool[] { new FakeTool() });
            Assert.Equal("there is no tool named \"orbit\"", ErrorOf(tools.Run("orbit", "{}", new GameSnapshot(), null)));
            Assert.Equal("the input is not valid JSON", ErrorOf(tools.Run("fake", "{\"n\":", new GameSnapshot(), null)));
            Assert.Equal("there is no Game Snapshot of the craft to calculate from", ErrorOf(tools.Run("fake", "{}", null, null)));
        }

        [Fact]
        public void A_tool_that_throws_costs_one_error_result()
        {
            var fake = new FakeTool { Answer = (i, s) => { throw new InvalidOperationException("boom"); } };
            var tools = new PlanningTools(new IPlanningTool[] { fake });
            Assert.Equal("the calculation failed: InvalidOperationException", ErrorOf(tools.Run("fake", "{}", new GameSnapshot(), null)));
        }

        [Fact]
        public void The_non_strict_schema_is_checked_by_the_module_before_the_tool_runs()
        {
            var ran = false;
            var fake = new FakeTool { Answer = (i, s) => { ran = true; return new ToolOutcome { Json = "{}" }; } };
            var tools = new PlanningTools(new IPlanningTool[] { fake });
            Assert.Equal("input.n must be a whole number", ErrorOf(tools.Run("fake", "{\"n\":1.5}", new GameSnapshot(), null)));
            Assert.Equal("input.items takes at most 2 items", ErrorOf(tools.Run("fake", "{\"items\":[1,2,3]}", new GameSnapshot(), null)));
            Assert.Equal("input.items[1] must be a number", ErrorOf(tools.Run("fake", "{\"items\":[1,\"x\"]}", new GameSnapshot(), null)));
            Assert.Equal("input.flag must be true or false", ErrorOf(tools.Run("fake", "{\"flag\":1}", new GameSnapshot(), null)));
            Assert.Equal("input must be an object", ErrorOf(tools.Run("fake", "[]", new GameSnapshot(), null)));
            Assert.False(ran);
            Assert.False(tools.Run("fake", "{\"n\":2,\"items\":[1.5],\"flag\":true}", new GameSnapshot(), null).IsError);
            Assert.True(ran);
        }

        [Fact]
        public void A_result_over_about_1k_tokens_is_cut_with_a_note_and_stays_json()
        {
            var big = "{\"rows\":[" + string.Join(",", Enumerable.Repeat("{\"a\":\"\\\"quoted\\\"\"}", 600)) + "]}";
            var fake = new FakeTool { Answer = (i, s) => new ToolOutcome { Json = big, Summary = "many rows" } };
            var outcome = new PlanningTools(new IPlanningTool[] { fake }).Run("fake", "{}", new GameSnapshot(), null);
            Assert.True(outcome.Json.Length <= PlanningTools.MaxResultChars, outcome.Json.Length.ToString());
            using (var doc = JsonDocument.Parse(outcome.Json))
            {
                Assert.StartsWith("truncated: the result was " + big.Length + " characters", doc.RootElement.GetProperty("note").GetString());
                Assert.StartsWith("{\"rows\":[", doc.RootElement.GetProperty("partial").GetString());
            }
            Assert.Equal("many rows (truncated)", outcome.Summary);
        }

        [Fact]
        public void Times_are_absolute_ut_with_in_n_s_alongside()
        {
            var json = PlanningJson.Time(new JsonWriter().BeginObject(), "burn", 1500.04, 1200).EndObject().ToString();
            Assert.Equal("{\"burn_ut\":1500,\"burn_in\":\"in 300 s\"}", json);
            Assert.Contains("\"30 s ago\"", PlanningJson.Time(new JsonWriter().BeginObject(), "t", 1170, 1200).EndObject().ToString());
        }

        [Fact]
        public void The_tools_list_is_fixed_per_offer_in_registration_order_and_non_strict()
        {
            var tools = new PlanningTools(new IPlanningTool[] { new FakeTool { Name = "a" }, new FakeTool { Name = "b", OnCraftCheck = true }, new FakeTool { Name = "c" } });
            Assert.Equal(new[] { "a", "b", "c" }, tools.Definitions(ToolOffer.Mission).Select(t => t.Name).ToArray());
            Assert.Equal(new[] { "b" }, tools.Definitions(ToolOffer.CraftCheck).Select(t => t.Name).ToArray());
            Assert.All(tools.Definitions(ToolOffer.Mission), t => Assert.False(t.Strict));
            Assert.Equal("faking it", tools.Activity("a"));
            Assert.Equal("calculating", tools.Activity("zzz"));
        }

        [Fact]
        public void The_shipped_tools_are_the_Mission_tools_on_mission_turns_and_stage_performance_and_plan_ascent_on_every_request_kind()
        {
            var tools = PlanningTools.Default();
            Assert.Equal(new[] { "predict_burn", "solve_burn", "stage_performance", "plan_ascent", "find_landing_site" }, tools.Definitions(ToolOffer.Mission).Select(t => t.Name).ToArray());
            Assert.Equal(new[] { "stage_performance", "plan_ascent" }, tools.Definitions(ToolOffer.CraftCheck).Select(t => t.Name).ToArray());
        }

        [Fact]
        public void A_non_strict_tool_is_sent_without_the_strict_field()
        {
            var request = new MessagesRequest { Model = "claude-sonnet-5-5", MaxTokens = 10 };
            request.Tools.AddRange(PlanningTools.Default().Definitions(ToolOffer.Mission));
            request.Tools.Add(new ToolDefinition { Name = "strict_one", Description = "d", InputSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}" });
            using (var doc = JsonDocument.Parse(request.ToJson()))
            {
                var sent = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();
                Assert.All(sent.Take(sent.Count - 1), t => Assert.False(t.TryGetProperty("strict", out _)));
                Assert.True(sent.Last().GetProperty("strict").GetBoolean());
                Assert.Equal("auto", doc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
            }
        }
    }
}
