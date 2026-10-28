// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// #421: steering to a bare vector makes kOS roll the craft to radial-up inside 5 degrees. The harness runs
    /// kOS.Safe only (no Direction), so the helper is pinned by its definition and docs, and the sites by source.
    /// </summary>
    public class KeepRollTests
    {
        private static string Source(string name)
        {
            return CoreLibrary.Files.Single(f => f.Name == name).Source;
        }

        private static string Squash(string s)
        {
            return Regex.Replace(s, "\\s+", " ");
        }

        private static string[] SteeringLocks(string src)
        {
            return Regex.Matches(src, "lock steering to ([^\\r\\n]*)\\.\\s*$", RegexOptions.Multiline)
                .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
        }

        [Fact]
        public void The_helper_returns_lookdirup_along_vec_with_the_current_top()
        {
            var lib = Squash(Source("claudia_lib"));
            Assert.Contains("function claudia_keep_roll { parameter vec. return lookdirup(vec, ship:facing:topvector). }", lib);
        }

        [Fact]
        public void The_helper_is_documented_for_claudia()
        {
            var docs = string.Join("\n", CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Docs);
            Assert.Contains("claudia_keep_roll(vec)", docs);
            Assert.Contains("steers to a direction", docs);
        }

        [Fact]
        public void Warp_to_burn_steers_through_the_helper()
        {
            var locks = SteeringLocks(Source("claudia_lib"));
            Assert.Contains("claudia_keep_roll(nd:deltav)", locks);
            Assert.All(locks, l => Assert.StartsWith("claudia_keep_roll(", l));
        }

        [Theory]
        [InlineData("exec_node")]
        [InlineData("land")]
        public void Every_steering_lock_in_the_block_goes_through_the_helper(string block)
        {
            var src = Source(block);
            var locks = SteeringLocks(src);
            Assert.NotEmpty(locks);
            Assert.All(locks, l => Assert.Equal("claudia_keep_roll(steer)", l));
            // The trim and land's vectors reach the lock through steer, re-evaluated each tick.
            Assert.Contains("set steer to", src);
        }
    }
}
