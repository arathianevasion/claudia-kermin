// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// RSVP v9 ships unmodified apart from its loading (kerboscript/rsvp/NOTE.md): these pin its files to
    /// the v9 originals by git blob id, and check the core embeds the same files it bundles.
    /// </summary>
    public class RsvpBundleTests
    {
        [Theory]
        [InlineData("hill_climb.ks", "01f3fbe79c246e2bf685bffdb20df8d4cb629260")]
        [InlineData("lambert.ks", "60f29f39d7af20e35ff1f4f784c9a4824e01caae")]
        [InlineData("maneuver.ks", "f951e93552d4b659bd260d542fc25b1fd4d1514a")]
        [InlineData("orbit.ks", "4c48deca3b23985e0995e1d82bd6c0c5db590572")]
        [InlineData("refine.ks", "3c86ddfa76f2a48b2e586ca90c2e7753cf631007")]
        [InlineData("search.ks", "2366fbe3f89016096f88274596372267e1d5d576")]
        [InlineData("transfer.ks", "86a23b742dc1620c822337169f9febb1e9847e07")]
        [InlineData("validate.ks", "7b18a5f33d7ba8b9613ba4ab9350771705d82d3c")]
        [InlineData("main.ks.v9", "5cb9e5f3a05eade2543495a168d6358cc7b997d6")]
        public void RsvpsOwnFilesAreTheV9OriginalsByteForByte(string file, string blobId)
        {
            Assert.Equal(blobId, GitBlobId(File.ReadAllBytes(Path.Combine(RsvpDirectory(), file))));
        }

        [Fact]
        public void TheCoreEmbedsEveryBundledRsvpFileAndNotTheOriginalMain()
        {
            var onDisk = Directory.GetFiles(RsvpDirectory(), "*.ks").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(onDisk, CoreLibrary.Rsvp.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(9, CoreLibrary.Rsvp.Count);
        }

        [Fact]
        public void TheBundledMainLoadsNoFilesAndCompilesNothing()
        {
            var main = string.Join("\n", CoreLibrary.Rsvp.Single(f => f.Name == "main").Source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotContain("runoncepath", main);
            Assert.DoesNotContain("local function compile_to", main);
            Assert.DoesNotContain("scriptpath()", main);
            Assert.Contains("rsvp_file_validate(export@).", main);
        }

        private static string GitBlobId(byte[] content)
        {
            var header = Encoding.ASCII.GetBytes("blob " + content.Length + "\0");
            using (var sha1 = SHA1.Create())
                return string.Concat(sha1.ComputeHash(header.Concat(content).ToArray()).Select(b => b.ToString("x2")));
        }

        private static string RsvpDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "kerboscript", "rsvp");
        }
    }
}
