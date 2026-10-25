// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class BlockSelectionTests
    {
        private static readonly string[] Shipped =
            { "ascend", "circularize", "coast", "exec_node", "land", "launch", "reentry", "return", "science", "transfer_to", "future_block" };

        [Fact]
        public void AMunLandingGetsTheMunBlocksButNotReentryOrScience()
        {
            var picked = BlockSelection.For("Land on the Mun", Shipped);
            Assert.Contains("transfer_to", picked);
            Assert.Contains("land", picked);
            Assert.Contains("exec_node", picked);
            Assert.DoesNotContain("reentry", picked);
            Assert.DoesNotContain("science", picked);
        }

        [Fact]
        public void AReturnTripGetsReentryAndReturn()
        {
            var picked = BlockSelection.For("Go to Minmus and come home", Shipped);
            Assert.Contains("return", picked);
            Assert.Contains("reentry", picked);
        }

        [Fact]
        public void AGoalThatPointsAtNothingGetsEveryBlock()
        {
            Assert.Equal(Shipped.Length, BlockSelection.For("Do something cool", Shipped).Count);
            Assert.Equal(Shipped.Length, BlockSelection.For("", Shipped).Count);
        }

        [Fact]
        public void ABlockTheTableDoesNotKnowIsAlwaysIncluded()
        {
            Assert.Contains("future_block", BlockSelection.For("Do the science", Shipped));
        }

        [Fact]
        public void TheLayerHasFullDocsOnlyForThePickedBlocksAndTheBaseLayerNamesAll()
        {
            var layer = MissionPrompts.MissionLayer("m1", "Fly the science experiments in orbit");
            var launch = CoreLibrary.Blocks.First(b => b.Name == "launch").Docs;
            var reentry = CoreLibrary.Blocks.First(b => b.Name == "reentry").Docs;
            Assert.Contains(string.Join("\n", launch.ToArray()), layer);
            Assert.DoesNotContain(string.Join("\n", reentry.ToArray()), layer);
            Assert.Contains(reentry[0], MissionPrompts.BaseLayer);
        }
    }
}
