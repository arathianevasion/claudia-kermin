// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The block tests shipped in tests/blocks: each one's test.cfg must parse and name its files.</summary>
    public class ShippedBlockTestsTests
    {
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
