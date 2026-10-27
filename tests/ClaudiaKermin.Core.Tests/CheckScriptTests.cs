// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary><c>check_script</c> through the Planning Tools entry point, over a scripted Script Check.</summary>
    public sealed class CheckScriptTests
    {
        private sealed class ScriptedCheck : IScriptCheck
        {
            public readonly List<string> Checked = new List<string>();
            public ScriptCheckResult Result = ScriptCheckResult.Pass();

            public ScriptCheckResult Check(string script)
            {
                Checked.Add(script);
                return Result;
            }
        }

        private readonly ScriptedCheck _check = new ScriptedCheck();

        private JsonElement Run(string script)
        {
            var tools = PlanningTools.Default(_check);
            var input = new JsonWriter().BeginObject().Field("script", script).EndObject().ToString();
            var outcome = tools.Run("check_script", input, new GameSnapshot(), new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            Assert.True(outcome.Json.Length <= PlanningTools.MaxResultChars);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        [Fact]
        public void A_passing_script_is_ok_with_no_problems()
        {
            var result = Run("print 1.");
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.False(result.TryGetProperty("problems", out _));
        }

        [Fact]
        public void The_check_runs_over_the_bundle_the_reply_is_checked_as()
        {
            Run("print 1.");
            Assert.Equal(CoreLibrary.Bundle("print 1."), Assert.Single(_check.Checked));
        }

        [Fact]
        public void A_script_that_does_not_compile_reports_the_compile_error_with_its_line()
        {
            _check.Result = new ScriptCheckResult("Syntax error: expected '.' at line:3 column:4", null);
            var result = Run("broken");
            Assert.False(result.GetProperty("ok").GetBoolean());
            var problem = Assert.Single(result.GetProperty("problems").EnumerateArray());
            Assert.Equal("compile", problem.GetProperty("kind").GetString());
            Assert.Equal(3, problem.GetProperty("line").GetInt32());
            Assert.Contains("expected '.'", problem.GetProperty("text").GetString());
        }

        [Fact]
        public void A_compile_error_without_a_line_leaves_the_line_out()
        {
            _check.Result = new ScriptCheckResult("the compiler returned no code", null);
            var problem = Assert.Single(Run("x").GetProperty("problems").EnumerateArray());
            Assert.False(problem.TryGetProperty("line", out _));
            Assert.Equal("the compiler returned no code", problem.GetProperty("text").GetString());
        }

        [Fact]
        public void Forbidden_calls_are_listed_with_their_lines_and_kind()
        {
            _check.Result = new ScriptCheckResult(null, new List<string> { "line 2: COPYPATH (touch files)", "line 5: :QUICKSAVE (quicksave)" });
            var result = Run("x");
            Assert.False(result.GetProperty("ok").GetBoolean());
            var problems = result.GetProperty("problems").EnumerateArray().ToList();
            Assert.Equal(2, problems.Count);
            Assert.All(problems, p => Assert.Equal("forbidden", p.GetProperty("kind").GetString()));
            Assert.Equal(new[] { 2, 5 }, problems.Select(p => p.GetProperty("line").GetInt32()).ToArray());
            Assert.Equal("COPYPATH (touch files)", problems[0].GetProperty("text").GetString());
        }

        [Fact]
        public void A_script_that_both_fails_to_compile_and_is_forbidden_lists_both()
        {
            _check.Result = new ScriptCheckResult("bad", new List<string> { "line 1: RUN (touch files)" });
            var kinds = Run("x").GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("kind").GetString()).ToArray();
            Assert.Equal(new[] { "compile", "forbidden" }, kinds);
        }

        [Fact]
        public void Many_long_problems_are_cut_to_stay_within_the_result_size_limit()
        {
            var found = Enumerable.Range(1, 40).Select(i => "line " + i + ": " + new string('x', 400)).ToList();
            _check.Result = new ScriptCheckResult(new string('c', 5000), found);
            var result = Run("x");
            Assert.Equal(8, result.GetProperty("problems").GetArrayLength());
            Assert.Equal(33, result.GetProperty("more_problems").GetInt32());
        }

        [Fact]
        public void It_is_offered_on_Mission_turns_but_not_the_Craft_Check()
        {
            var tools = PlanningTools.Default(_check);
            Assert.Equal(new[] { "predict_burn", "stage_performance", "find_landing_site", "check_script" }, tools.Definitions(ToolOffer.Mission).Select(t => t.Name).ToArray());
            Assert.Equal(new[] { "stage_performance" }, tools.Definitions(ToolOffer.CraftCheck).Select(t => t.Name).ToArray());
        }

        [Fact]
        public void A_missing_script_is_a_schema_error()
        {
            var outcome = PlanningTools.Default(_check).Run("check_script", "{}", new GameSnapshot(), null);
            Assert.True(outcome.IsError);
            Assert.Empty(_check.Checked);
        }
    }
}
