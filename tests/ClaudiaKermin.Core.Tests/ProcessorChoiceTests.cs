// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class ProcessorChoiceTests
    {
        // A stack, top down: probe core (root) > upper processor > tank > lower processor,
        // and a side processor radially attached to the tank.
        private static readonly VesselPart[] Stack =
        {
            new VesselPart(100, null, false),
            new VesselPart(200, 100, true),
            new VesselPart(300, 200, false),
            new VesselPart(400, 300, true),
            new VesselPart(500, 300, true),
        };

        [Fact]
        public void PicksTheProcessorNearestTheRoot()
        {
            Assert.Equal(200u, ProcessorChoice.Choose(Stack, null));
        }

        [Fact]
        public void ThePlayersChoiceWinsWhileItIsStillAProcessorOnTheVessel()
        {
            Assert.Equal(400u, ProcessorChoice.Choose(Stack, 400));
        }

        [Fact]
        public void AChoiceNoLongerOnTheVesselFallsBackToTheNearest()
        {
            Assert.Equal(200u, ProcessorChoice.Choose(Stack, 999));
            Assert.Equal(200u, ProcessorChoice.Choose(Stack, 300));
        }

        [Fact]
        public void EqualDistancesGoToThePartListedFirst()
        {
            var parts = new[]
            {
                new VesselPart(1, null, false),
                new VesselPart(3, 1, true),
                new VesselPart(2, 1, true),
            };

            Assert.Equal(3u, ProcessorChoice.Choose(parts, null));
        }

        [Fact]
        public void TheRootItselfCanBeTheProcessor()
        {
            var parts = new[] { new VesselPart(7, null, true), new VesselPart(8, 7, true) };

            Assert.Equal(7u, ProcessorChoice.Choose(parts, null));
        }

        [Fact]
        public void NoProcessorMeansNoChoice()
        {
            var parts = new[] { new VesselPart(1, null, false), new VesselPart(2, 1, false) };

            Assert.Null(ProcessorChoice.Choose(parts, null));
        }
    }
}
