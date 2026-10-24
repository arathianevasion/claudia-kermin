// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The Core Library as the core ships it: the bundle and the docs. Running it is in the kerboscript tests.</summary>
    public class CoreLibraryTests
    {
        private const string Script = "@lazyglobal off.\r\nlocal r is exec_node().\r\naddons:claudia:report(r).\r\n";

        [Fact]
        public void TheBundleKeepsTheMissionScriptFirstSoItsLineNumbersStayRight()
        {
            var bundle = CoreLibrary.Bundle(Script, 1, 10800);
            Assert.StartsWith("@lazyglobal off.\nlocal r is exec_node().\naddons:claudia:report(r).\n", bundle);
        }

        [Fact]
        public void TheBundleHoldsEveryLibraryFileWithOnlyTheScriptsLazyglobalDirective()
        {
            var bundle = CoreLibrary.Bundle(Script, 1, 10800);
            Assert.Contains("function claudia_finish {", bundle);
            Assert.Contains("function exec_node {", bundle);
            Assert.Single(Regex.Matches(bundle, "(?m)^\\s*@lazyglobal"));
        }

        [Fact]
        public void TheBundleCarriesThePlayersWaitCaps()
        {
            var bundle = CoreLibrary.Bundle(Script, 1.5, 7200);
            Assert.Contains("function claudia_config {\n  return lexicon(\"wait_cap_orbits\", 1.5, \"wait_cap_ground_s\", 7200).\n}", bundle);
        }

        [Fact]
        public void TheSharedHelpersComeFirstAndEveryBlockHasDocsLedByItsSummary()
        {
            Assert.Equal("claudia_lib", CoreLibrary.Files.First().Name);
            Assert.NotEmpty(CoreLibrary.Blocks);
            foreach (var block in CoreLibrary.Blocks)
                Assert.StartsWith(block.Name + "(options): ", block.Docs[0]);
        }

        [Fact]
        public void SummariesListEachBlockOnceAndFullDocsCanBeNarrowedToSomeBlocks()
        {
            Assert.Contains("\n- exec_node(options): flies a maneuver node", CoreLibrary.Summaries());
            Assert.DoesNotContain("max_residual [0.5]", CoreLibrary.Summaries());
            Assert.Contains("max_residual [0.5]", CoreLibrary.FullDocs(new[] { "exec_node" }));
            Assert.Equal("", CoreLibrary.FullDocs(new[] { "land" }));
        }
    }
}
