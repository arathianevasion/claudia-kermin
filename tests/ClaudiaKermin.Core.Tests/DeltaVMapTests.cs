// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Delta-v Map is computed from body data passed in. Expected figures are the community's
    /// well-known Kerbin-system values (ranges, because the Map is computed, not copied from them).
    /// </summary>
    public sealed class DeltaVMapTests
    {
        public static List<SnapshotBody> KerbinSystem()
        {
            return new List<SnapshotBody>
            {
                new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, Sma = 13599840256, Inclination = 0 },
                new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, AtmosphereHeight = 0, Sma = 12000000, Inclination = 0 },
                new SnapshotBody { Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, AtmosphereHeight = 0, Sma = 47000000, Inclination = 6 },
                new SnapshotBody { Name = "Sun", Parent = "", Gm = 1.1723328e18, Radius = 261600000, AtmosphereHeight = 600000, Sma = 0, Inclination = 0 },
            };
        }

        [Fact]
        public void LaunchToKerbinOrbitIsAboutThirtyFourHundred()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("launch:Kerbin").DeltaV, 3300, 3500);
        }

        [Fact]
        public void TransferToTheMunDepartsAbout860AndCaptures310()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("transfer:Mun").Departure, 830, 890);
            Assert.InRange(map.Leg("transfer:Mun").Arrival, 290, 330);
            Assert.InRange(map.Leg("transfer:Mun").DeltaV, 1120, 1220);
        }

        [Fact]
        public void LandingAndAscentFromLowMunOrbitAreAbout580()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("land:Mun").DeltaV, 540, 650);
            Assert.InRange(map.Leg("ascend:Mun").DeltaV, 540, 650);
        }

        [Fact]
        public void ReturnFromLowMunOrbitToKerbinIsAbout310()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("return:Mun").DeltaV, 290, 330);
        }

        [Fact]
        public void MinmusIsDearerToLeaveForAndCheapToLandOn()
        {
            var map = DeltaVMap.Compute(KerbinSystem(), "Kerbin");
            Assert.InRange(map.Leg("transfer:Minmus").Departure, 900, 960);
            Assert.InRange(map.Leg("transfer:Minmus").Arrival, 140, 190);
            Assert.InRange(map.Leg("land:Minmus").DeltaV, 150, 260);
        }

        [Fact]
        public void EveryMoonOfTheHomeBodyGetsLegsAndNothingIsHardCoded()
        {
            var bodies = KerbinSystem();
            bodies.Add(new SnapshotBody { Name = "Pebble", Parent = "Kerbin", Gm = 1e9, Radius = 30000, Sma = 20000000 });
            var map = DeltaVMap.Compute(bodies, "Kerbin");
            Assert.Equal(new[] { "Minmus", "Mun", "Pebble" }, map.Moons.OrderBy(m => m).ToArray());
            Assert.True(map.Leg("transfer:Pebble").DeltaV > 0);

            bodies.First(b => b.Name == "Kerbin").Gm *= 2;
            Assert.True(DeltaVMap.Compute(bodies, "Kerbin").Leg("launch:Kerbin").DeltaV > map.Leg("launch:Kerbin").DeltaV * 1.3);
        }

        [Fact]
        public void AnUnknownHomeBodyGivesAnEmptyMap()
        {
            Assert.Empty(DeltaVMap.Compute(KerbinSystem(), "Eve").Legs);
            Assert.Null(DeltaVMap.Compute(KerbinSystem(), "Kerbin").Leg("land:Duna"));
        }

        [Fact]
        public void TheMapReadsAsLinesForClaude()
        {
            var text = DeltaVMap.Compute(KerbinSystem(), "Kerbin").ToText();
            Assert.Contains("launch:Kerbin", text);
            Assert.Contains("m/s", text);
        }
    }
}
