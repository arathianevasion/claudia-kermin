// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The block tests shipped in tests/blocks/: each must load in the debug runner.</summary>
    public class ShippedBlockTestsTests
    {
        [Fact]
        public void EveryShippedBlockTestParsesAndNamesALibraryBlockAndItsFiles()
        {
            var tests = Directory.GetDirectories(Path.Combine(RepoRoot(), "tests", "blocks"));
            Assert.NotEmpty(tests);
            foreach (var test in tests)
            {
                var name = Path.GetFileName(test);
                var spec = BlockTestSpec.Parse(name, File.ReadAllText(Path.Combine(test, "test.cfg")));
                Assert.True(CoreLibrary.Blocks.Any(b => b.Name == spec.Block), name + ": no block " + spec.Block);
                Assert.True(File.Exists(Path.Combine(test, spec.Craft)), name + ": no " + spec.Craft);
                Assert.True(File.Exists(Path.Combine(test, "scenario.sfs")), name + ": no scenario.sfs");
                Assert.True(File.Exists(Path.Combine(test, "test.ks")), name + ": no test.ks");
                Assert.NotEmpty(spec.Checks);
            }
        }

        [Fact]
        public void EveryShippedToolScenarioHasTheBlockTestLayoutAndCallsAPlanningTool()
        {
            var scenarios = Directory.GetDirectories(Path.Combine(RepoRoot(), "tests", "tools"));
            Assert.NotEmpty(scenarios);
            var tools = Tools.PlanningTools.Default().Definitions(Tools.ToolOffer.Mission).Select(t => t.Name).ToList();
            foreach (var scenario in scenarios)
            {
                var name = Path.GetFileName(scenario);
                var spec = BlockTestSpec.Parse(name, File.ReadAllText(Path.Combine(scenario, "test.cfg")));
                Assert.True(CoreLibrary.Blocks.Any(b => b.Name == spec.Block), name + ": no block " + spec.Block);
                Assert.True(File.Exists(Path.Combine(scenario, spec.Craft)), name + ": no " + spec.Craft);
                Assert.True(File.Exists(Path.Combine(scenario, "scenario.sfs")), name + ": no scenario.sfs");
                Assert.NotEmpty(spec.Checks);
                var script = File.ReadAllText(Path.Combine(scenario, "test.ks"));
                Assert.Contains("addons:claudia:planningtool(", script);
                Assert.Contains(tools, t => script.Contains("planningtool(\"" + t + "\""));
            }
        }

        [Theory]
        [InlineData("transfer_to", "Kerbin")]
        [InlineData("return", "Mun")]
        public void TheTransferAndReturnBlockTestsParseAndShipTheirFiles(string test, string orbitBody)
        {
            var dir = Path.Combine(RepoRoot(), "tests", "blocks", test);
            var spec = BlockTestSpec.Parse(test, File.ReadAllText(Path.Combine(dir, "test.cfg")));
            Assert.Equal(test, spec.Block);
            Assert.Equal(orbitBody, spec.OrbitBody);
            Assert.True(File.Exists(Path.Combine(dir, spec.Craft)));
            Assert.True(File.Exists(Path.Combine(dir, "scenario.sfs")));
            Assert.True(File.Exists(Path.Combine(dir, "test.ks")));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
