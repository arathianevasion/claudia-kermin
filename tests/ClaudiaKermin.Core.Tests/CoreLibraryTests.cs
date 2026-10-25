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

        [Fact]
        public void LaunchAndCircularizeAreSummarisedAndDocumentTheirContractOptionsAndResults()
        {
            var summaries = CoreLibrary.Summaries();
            Assert.Contains("\n- launch(options): flies from the ground of a body with an atmosphere to a coast out of it; then circularize.", summaries);
            Assert.Contains("\n- circularize(options): makes the orbit circular at apoapsis, periapsis or an altitude in one burn.", summaries);
            var launch = CoreLibrary.FullDocs(new[] { "launch" });
            foreach (var option in new[] { "launch_at [\"now\"]", "target_ap [\"auto\"]", "target_inc [0]", "heading [\"lib_lazcalc\"]", "turn_start_speed [60]", "turn_shape [0.45]", "max_aoa [5]", "throttle_max_q [\"none\"]", "g, atm_height, surface_pressure [\"body\"]", "ap_error", "max_q", "coast_ap" })
                Assert.Contains(option, launch);
            var circularize = CoreLibrary.FullDocs(new[] { "circularize" });
            foreach (var option in new[] { "at [\"ap\"]", "tolerance [0.01]", "ecc" })
                Assert.Contains(option, circularize);
        }

        [Fact]
        public void EveryShippedBlockTestSpecParses()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var specs = System.IO.Directory.GetFiles(System.IO.Path.Combine(dir.FullName, "tests", "blocks"), "test.cfg", System.IO.SearchOption.AllDirectories);
            Assert.NotEmpty(specs);
            foreach (var path in specs)
            {
                var folder = System.IO.Path.GetDirectoryName(path);
                var spec = BlockTestSpec.Parse(System.IO.Path.GetFileName(folder), System.IO.File.ReadAllText(path));
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, spec.Craft)), path + ": no " + spec.Craft);
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "scenario.sfs")), path + ": no scenario.sfs");
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "test.ks")), path + ": no test.ks");
            }
        }

        [Fact]
        public void TheBundleHoldsEachRsvpFileAsAFunctionAfterTheLibrary()
        {
            var bundle = CoreLibrary.Bundle(Script, 1, 10800);
            foreach (var name in new[] { "hill_climb", "lambert", "main", "maneuver", "orbit", "refine", "search", "transfer", "validate" })
                Assert.Contains("\nfunction rsvp_file_" + name + " {\n", bundle);
            Assert.True(bundle.IndexOf("function rsvp_file_main {", System.StringComparison.Ordinal) > bundle.IndexOf("function exec_node {", System.StringComparison.Ordinal));
            Assert.DoesNotContain(CoreLibrary.Blocks, b => b.Name.StartsWith("rsvp", System.StringComparison.Ordinal));
        }
    }
}
