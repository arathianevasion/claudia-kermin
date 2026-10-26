// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The Custom Script badge is derived from the script: Building Block calls only, no direct flight control.</summary>
    public sealed class ScriptProvenanceTests
    {
        [Fact]
        public void AScriptThatCallsOnlyBuildingBlocksIsNotCustom()
        {
            var script = "local res is launch(lexicon(\"target_ap\", 80000)).\n"
                + "if res:success { set res to circularize(). }\n"
                + "print \"done\".\n"
                + "ADDONS:CLAUDIA:REPORT(lexicon(\"success\", res:success)).\n";
            Assert.False(ScriptProvenance.IsCustom(script));
        }

        [Fact]
        public void BuildingBlockNamesMatchWithoutRegardToCase()
        {
            Assert.False(ScriptProvenance.IsCustom("LOCAL r IS Circularize().\n"));
        }

        [Fact]
        public void AScriptThatCallsNoBuildingBlockIsCustom()
        {
            Assert.True(ScriptProvenance.IsCustom("print \"hi\".\nADDONS:CLAUDIA:REPORT(lexicon(\"success\", true)).\n"));
            Assert.True(ScriptProvenance.IsCustom(""));
        }

        [Theory]
        [InlineData("lock steering to prograde.")]
        [InlineData("lock throttle to 1.")]
        [InlineData("unlock all.")]
        [InlineData("wait 5.")]
        [InlineData("wait until ship:altitude > 100.")]
        [InlineData("add node(time:seconds + 60, 0, 0, 10).")]
        [InlineData("add node(time:seconds + 60, 0, 0, 10).\nprint \"flying\".\nexec_node().")]
        [InlineData("add node(time:seconds + 60, 0, 0, 10). // exec_node() comes later\nlock throttle to 1.")]
        [InlineData("remove nextnode.")]
        [InlineData("set throttle to 0.5.")]
        [InlineData("set ship:control:pilotmainthrottle to 0.")]
        [InlineData("set target to mun.")]
        [InlineData("set warp to 3.")]
        [InlineData("sas on.")]
        [InlineData("gear off.")]
        [InlineData("set sas to true.")]
        [InlineData("set gear to false.")]
        [InlineData("reboot.")]
        [InlineData("deletepath(\"1:/x\").")]
        [InlineData("runpath(\"0:/other.ks\").")]
        [InlineData("warpto(time:seconds + 100).")]
        public void ABlockScriptThatAlsoFliesTheCraftItselfIsCustom(string extra)
        {
            Assert.True(ScriptProvenance.IsCustom("circularize().\n" + extra + "\n"));
        }

        [Fact]
        public void StagingBetweenBuildingBlocksIsBlockUse()
        {
            Assert.False(ScriptProvenance.IsCustom("launch(lexicon(\"target_ap\", 80000)).\nstage.\ncircularize().\n"));
            Assert.False(ScriptProvenance.IsCustom("circularize().\nSTAGE.\n"));
        }

        [Fact]
        public void AddingTheNodeAnExecNodeCallFliesIsBlockUse()
        {
            Assert.False(ScriptProvenance.IsCustom("add node(time:seconds + 60, 0, 0, 10).\nexec_node().\n"));
            Assert.False(ScriptProvenance.IsCustom("ADD NODE(time:seconds + 60, 0, 0, max(1, 2)).\nlocal r is exec_node(lexicon(\"min_ec_pct\", 10)).\n"));
            Assert.False(ScriptProvenance.IsCustom("add node(time:seconds + 60, 0, 0, 10).\nset res to exec_node().\n"));
        }

        [Fact]
        public void AScriptThatOnlyStagesAndAddsNodesWithoutABlockCallIsStillCustom()
        {
            Assert.True(ScriptProvenance.IsCustom("stage.\n"));
            Assert.True(ScriptProvenance.IsCustom("add node(time:seconds + 60, 0, 0, 10).\n"));
        }

        [Fact]
        public void ReadingStageOrCallingASuffixIsNotFlightControl()
        {
            var script = "circularize().\nlocal n is stage:number.\nlocal l is lexicon().\nl:add(\"a\", 1).\n";
            Assert.False(ScriptProvenance.IsCustom(script));
        }

        [Fact]
        public void NamesInCommentsAndStringsAreNotCode()
        {
            var script = "// wait 5. lock steering to up.\ncircularize().\nprint \"wait 10. stage.\".\n";
            Assert.False(ScriptProvenance.IsCustom(script));
            Assert.True(ScriptProvenance.IsCustom("// circularize().\nprint \"circularize()\".\n"));
        }

        [Fact]
        public void RedefiningABuildingBlockOrHelperNameIsCustom()
        {
            Assert.True(ScriptProvenance.IsCustom("function circularize { return 1. }\ncircularize().\n"));
            Assert.True(ScriptProvenance.IsCustom("function claudia_tick { }\ncircularize().\n"));
        }

        [Fact]
        public void ACustomFunctionThatFliesTheCraftMakesTheScriptCustom()
        {
            Assert.True(ScriptProvenance.IsCustom("function burn { lock throttle to 1. }\ncircularize().\nburn().\n"));
        }
    }
}
