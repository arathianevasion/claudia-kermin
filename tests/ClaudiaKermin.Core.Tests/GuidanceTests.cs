// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// How to plan (#285): the cached base layer's planning rules and worked example, and the pairs that tie each
    /// Planning Tool to its Building Block and back.
    /// </summary>
    public sealed class GuidanceTests
    {
        private sealed class PassingCheck : IScriptCheck
        {
            public ScriptCheckResult Check(string script)
            {
                return ScriptCheckResult.Pass();
            }
        }

        /// <summary>Every tool a Mission request offers, check_script included.</summary>
        private static IList<ToolDefinition> Tools()
        {
            return PlanningTools.Default(new PassingCheck()).Definitions(ToolOffer.Mission);
        }

        [Fact]
        public void TheBaseLayerCarriesTheThreeRulesInPlaceOfPreferBuildingBlocks()
        {
            var layer = MissionPrompts.BaseLayer;
            Assert.DoesNotContain("Prefer Building Blocks", layer);
            Assert.Contains("Don't work out in your head what a Planning Tool can calculate; call it.", layer);
            Assert.Contains("Don't write in a script what a Building Block does; call the block.", layer);
            Assert.Contains("Pass blocks goals, not timings", layer);
            Assert.Contains(MissionPrompts.HowToPlan, layer);
        }

        [Fact]
        public void TheBaseLayerSaysToCallAToolOnlyWhenItsAnswerChangesTheReplyAndNeverDuringAnAlert()
        {
            var layer = MissionPrompts.BaseLayer;
            Assert.Contains("only when its answer changes your reply", layer);
            Assert.Contains("never to test that tools work", layer);
            Assert.Contains("never call a tool during an Alert", layer);
        }

        [Fact]
        public void TheWorkedExampleIsAMunLandingFromFindLandingSiteToStagePerformanceToLandByBiomeWithDropEmpty()
        {
            var plan = MissionPrompts.HowToPlan;
            var site = plan.IndexOf("find_landing_site", StringComparison.Ordinal);
            var stages = plan.IndexOf("stage_performance", StringComparison.Ordinal);
            var script = plan.IndexOf(MissionPrompts.WorkedExampleScript, StringComparison.Ordinal);
            Assert.True(site >= 0 && stages > site && script > stages, plan);
            Assert.Contains("land(", MissionPrompts.WorkedExampleScript);
            Assert.Contains("lexicon(\"biome\", \"Midlands\")", MissionPrompts.WorkedExampleScript);
            Assert.Contains("\"drop_empty\"", MissionPrompts.WorkedExampleScript);
            Assert.Contains("addons:claudia:report(res).", MissionPrompts.WorkedExampleScript);
            // The example is block use, not a Custom Script, and its Debrief calls no tool.
            Assert.False(ScriptProvenance.IsCustom(MissionPrompts.WorkedExampleScript));
            Assert.Contains("Debrief", plan.Substring(script));
        }

        [Fact]
        public void TheGuidanceStaysShortSoTheCachedPrefixGrowsByLittleMoreThanTheRulesAndExample()
        {
            // The base layer was 8,401 characters before #285; the rules and the example are all it gains.
            Assert.InRange(MissionPrompts.HowToPlan.Length, 1, 1500);
            // was 8401 + 1500 = 9901; raised for #349 Claudia's warp guidance (human decision 2026-10-27); raised by the
            // rcs_tolerance doc worded in the RCS Use spec (#393, #400); to 10762 for the claudia_rcs_translate doc (#401);
            // to 10921 for the spec's RCS Use / SAS wording in the mission prompt (#402; spec-mandated, not yet human-approved);
            // to 11116 (+195) for numbering the Mission Plan's Maneuver Sequence each Sequence Plan flies (#413; ticket-mandated, not yet human-approved)
            Assert.InRange(MissionPrompts.BaseLayer.Length, 1, 11116);
        }

        [Fact]
        public void TheMissionPromptTellsClaudiaToNameTheStepEachSequencePlanFliesAndTheSameStepForARetry()
        {
            Assert.Contains("Each Sequence Plan's plan_step numbers the Mission Plan's Maneuver Sequence it flies (1 for the first): a retry gives the same number again", MissionPrompts.BaseLayer);
        }

        [Fact]
        public void TheMissionPromptTellsClaudiaToLeaveRcsToTheBlocksAndNeverSwitchSasOn()
        {
            Assert.Contains("Blocks manage RCS themselves (RCS Use): leave it to them, or use claudia_rcs_translate for translation. "
                + "Never switch SAS on: the Script Check rejects it, because SAS fights kOS steering and fires RCS through holds.", MissionPrompts.BaseLayer);
            Assert.DoesNotContain("do not turn it back on", MissionPrompts.BaseLayer);
        }

        [Fact]
        public void EveryToolDescriptionNamesItsBuildingBlockAndWhenNotToCallIt()
        {
            var blocks = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            foreach (var tool in Tools())
            {
                // check_script pairs with whichever blocks the script runs.
                var named = tool.Name == "check_script"
                    ? tool.Description.Contains("the one that runs the Building Blocks")
                    : blocks.Any(b => Regex.IsMatch(tool.Description, "Its blocks? (is|are) [^:]*\\b" + b + "\\b"));
                Assert.True(named, tool.Name + " names no Building Block: " + tool.Description);
                Assert.True(tool.Description.Contains("Don't call it"), tool.Name + " doesn't say when not to call it: " + tool.Description);
            }
        }

        [Fact]
        public void PredictBurnIsNotForTheBlocksThatPlanTheirOwnBurns()
        {
            var predict = Tools().Single(t => t.Name == "predict_burn").Description;
            Assert.Contains("Its block is exec_node", predict);
            Assert.Contains("Don't call it for transfer_to, return, tune_pe, deorbit, circularize or land, which plan their own burns", predict);
        }

        [Fact]
        public void CheckScriptIsNotForTestingThatToolsWork()
        {
            var check = Tools().Single(t => t.Name == "check_script").Description;
            Assert.Contains("never to test that tools work", check);
        }

        [Theory]
        [InlineData("land", "find_landing_site")]
        [InlineData("land", "stage_performance")]
        [InlineData("deorbit", "stage_performance")]
        [InlineData("return", "stage_performance")]
        [InlineData("tune_pe", "solve_burn")]
        [InlineData("drop_parts", "stage_performance")]
        public void EachNewOrChangedBlocksDocsNameItsTool(string block, string tool)
        {
            Assert.Contains(tool, CoreLibrary.FullDocs(new[] { block }));
        }

        [Fact]
        public void ThePairsSitInTheFullDocsNotTheBaseLayersSummaries()
        {
            // The block summaries are cached in the base layer: a pair there would grow it past the rules and the example.
            var summaries = CoreLibrary.Summaries();
            foreach (var tool in Tools()) Assert.DoesNotContain(tool.Name, summaries);
        }
    }
}
