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
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var tests = Directory.GetDirectories(Path.Combine(dir.FullName, "tests", "blocks"));
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
    }
}
