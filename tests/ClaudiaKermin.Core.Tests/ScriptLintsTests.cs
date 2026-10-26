// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The warn-only lints on re-built blocks and tools, and how their text reaches the Custom Script badge's reason.</summary>
    public sealed class ScriptLintsTests
    {
        private const string SiteSearch = "circularize().\nlocal best is 0.\nfrom {local i is 0.} until i > 20 step {set i to i + 1.} do {\n"
            + "  local p is latlng(i, 10).\n  if p:terrainheight > best { set best to p:terrainheight. }\n}\nland().\n";
        private const string NudgeLoop = "circularize().\nuntil ship:orbit:nextpatch:periapsis > 30000 {\n  set nextnode:prograde to nextnode:prograde + 1.\n}\n";
        private const string FuelStaging = "launch().\nuntil false {\n  if stage:liquidfuel < 0.1 { stage. }\n  wait 0.1.\n}\n";

        [Fact]
        public void AHandWrittenSiteSearchLoopWarns()
        {
            var warnings = ScriptLints.Warnings(SiteSearch);
            Assert.Contains(warnings, w => w.Contains("landing-site search"));
            Assert.Single(warnings);
        }

        [Fact]
        public void ALandingSiteBlockOptionOutsideALoopDoesNotWarnAboutASiteSearch()
        {
            Assert.Empty(ScriptLints.Warnings("land(lexicon(\"target\", latlng(1.2, 3.4))).\nfor x in list(1, 2) { print x. }\n"));
        }

        [Fact]
        public void ANodeAndNextPatchNudgeLoopWarns()
        {
            var warnings = ScriptLints.Warnings(NudgeLoop);
            Assert.Contains(warnings, w => w.Contains("next patch"));
            Assert.Single(warnings);
        }

        [Fact]
        public void ReadingTheNextPatchOutsideANodeLoopDoesNotWarn()
        {
            Assert.Empty(ScriptLints.Warnings("circularize().\nprint ship:orbit:nextpatch:periapsis.\nuntil ship:altitude > 10 { print 1. }\n"));
            Assert.Empty(ScriptLints.Warnings("until nextnode:eta < 10 { print 1. }\nlocal p is ship:orbit:nextpatch.\n"));
        }

        [Fact]
        public void AStagingLoopThatWatchesFuelWarns()
        {
            var warnings = ScriptLints.Warnings(FuelStaging);
            Assert.Contains(warnings, w => w.Contains("staging loop"));
            Assert.Single(warnings);
            Assert.Contains(ScriptLints.Warnings("launch().\nwhen stage:solidfuel < 1 then { stage. }\n"), w => w.Contains("staging loop"));
        }

        [Fact]
        public void PlainStagingAndAFuelReadOutsideAStagingLoopDoNotWarn()
        {
            Assert.Empty(ScriptLints.Warnings("launch().\nstage.\ncircularize().\n"));
            Assert.Empty(ScriptLints.Warnings("until ship:altitude > 10 { print stage:liquidfuel. }\nstage.\n"));
        }

        [Fact]
        public void AnOrdinaryBlockScriptAndWordsInCommentsAndStringsDoNotWarn()
        {
            Assert.Empty(ScriptLints.Warnings("local r is launch().\nif r:success { set r to circularize(). }\nADDONS:CLAUDIA:REPORT(lexicon(\"success\", r:success)).\n"));
            Assert.Empty(ScriptLints.Warnings("// until false { stage. print stage:liquidfuel. }\nprint \"until x { latlng(1,2). }\".\ncircularize().\n"));
            Assert.Empty(ScriptLints.Warnings(""));
            Assert.Empty(ScriptLints.Warnings(null));
        }

        [Fact]
        public void ALoopAfterAnotherBlockIsStillRead()
        {
            Assert.Contains(ScriptLints.Warnings("launch().\nif true { print 1. }\n" + FuelStaging), w => w.Contains("staging loop"));
            Assert.Contains(ScriptLints.Warnings("local await is 1.\n" + FuelStaging), w => w.Contains("staging loop"));
        }

        [Fact]
        public void ABlockCalledInALoopWithATargetIsNotASiteSearch()
        {
            Assert.Empty(ScriptLints.Warnings("for t in targets { land(lexicon(\"target\", latlng(t:lat, t:lng))). }\n"));
            Assert.Empty(ScriptLints.Warnings("until ship:altitude > 70000 { print ship:maxthrust. if true { stage. } }\n"));
        }

        [Fact]
        public void AReasonAlreadyHoldingTheLintTextIsNotRepeated()
        {
            var lint = ScriptLints.Warnings(FuelStaging).Single();
            var plan = Reply(FuelStaging).SequencePlan;
            Assert.Equal(1, plan.CustomScriptReason.Split(new[] { lint }, System.StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void WaitUntilIsNotALoop()
        {
            Assert.Empty(ScriptLints.Warnings("wait until stage:liquidfuel < 1.\nstage.\nif true { print nextnode. }\n"));
        }

        // ---- The badge ----

        private static MissionReply Reply(string script)
        {
            var json = "{\"kind\":\"sequence_plan\",\"sequence_plan\":{\"name\":\"Burn\",\"custom_script_reason\":\"Nothing fits.\",\"script\":"
                + "\"" + script.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"}}";
            return MissionReply.Parse(json);
        }

        [Fact]
        public void TheLintTextAppearsInTheCustomScriptBadgesReason()
        {
            var plan = Reply(SiteSearch).SequencePlan;
            Assert.True(plan.CustomScript);
            Assert.StartsWith("Nothing fits.", plan.CustomScriptReason);
            Assert.Contains("landing-site search", plan.CustomScriptReason);
        }

        [Fact]
        public void AFuelWatchingStagingLoopShowsTheBadgeEvenThoughStagingAloneIsBlockUse()
        {
            var plan = Reply(FuelStaging).SequencePlan;
            Assert.True(plan.CustomScript);
            Assert.Contains("staging loop", plan.CustomScriptReason);
        }

        [Fact]
        public void ACleanBlockScriptHasNoBadgeAndNoLintText()
        {
            var plan = Reply("launch().\nstage.\ncircularize().\n").SequencePlan;
            Assert.False(plan.CustomScript);
            Assert.Equal("", plan.CustomScriptReason);
        }
    }
}
