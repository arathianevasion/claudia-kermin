// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The terrain maths behind ADDONS:CLAUDIA's slope and flat-spot suffixes, on made-up terrain.</summary>
    public class TerrainTests
    {
        // The Mun's radius, so metres and degrees relate as they do there.
        private const double Radius = 200000;
        private const double MetresPerDegree = Radius * Math.PI / 180;

        /// <summary>A plane tilted the given number of degrees, rising to the east.</summary>
        private static Terrain.HeightAt RisingEast(double degrees)
        {
            return (lat, lng) => 1000 + Math.Tan(degrees * Math.PI / 180) * lng * MetresPerDegree * Math.Cos(lat * Math.PI / 180);
        }

        [Fact]
        public void FlatGroundHasNoSlope()
        {
            Assert.Equal(0, Terrain.Slope((lat, lng) => 1500, Radius, 3, 40), 6);
        }

        [Fact]
        public void TheSlopeOfATiltedPlaneIsItsTilt()
        {
            Assert.Equal(10, Terrain.Slope(RisingEast(10), Radius, 0, 20), 2);
            Assert.Equal(25, Terrain.Slope(RisingEast(25), Radius, 0, -60), 2);
        }

        /// <summary>A 20° slope rising east from (0, 0) that levels off into a plateau <paramref name="plateau"/> m east.</summary>
        private static Terrain.HeightAt SlopeThenPlateau(double plateau)
        {
            return (lat, lng) => 500 + Math.Tan(20 * Math.PI / 180) * Math.Min(Math.Max(lng * MetresPerDegree, -1000), plateau);
        }

        [Fact]
        public void AFlatEnoughSpotIsKept()
        {
            var spot = Terrain.Flattest((lat, lng) => 800, Radius, 1, 2, 300, 8);
            Assert.True(spot.Flat);
            Assert.Equal(1, spot.Lat, 9);
            Assert.Equal(2, spot.Lng, 9);
            Assert.Equal(0, spot.Distance, 6);
            Assert.Equal(800, spot.Height, 6);
        }

        [Fact]
        public void ASteepSpotDivertsToTheNearestFlatGroundWithinReach()
        {
            var spot = Terrain.Flattest(SlopeThenPlateau(150), Radius, 0, 0, 300, 8);
            Assert.True(spot.Flat);
            Assert.True(spot.Slope <= 8, "slope " + spot.Slope);
            Assert.InRange(spot.Distance, 150, 200);
            Assert.True(spot.Lng > 0, "east, onto the plateau");
            Assert.Equal(Terrain.Distance(0, 0, spot.Lat, spot.Lng, Radius), spot.Distance, 3);
        }

        [Fact]
        public void WithNothingFlatEnoughInReachTheLeastSteepSpotIsBestEffort()
        {
            var spot = Terrain.Flattest(SlopeThenPlateau(600), Radius, 0, 0, 300, 8);
            Assert.False(spot.Flat);
            Assert.Equal(20, spot.Slope, 1);
            Assert.True(spot.Distance <= 300);

            // Steepest at the aim point, gentlest (10°) 100 m east.
            var tan10 = Math.Tan(10 * Math.PI / 180);
            Terrain.HeightAt easing = (lat, lng) =>
            {
                var x = lng * MetresPerDegree;
                return 100 + tan10 * x + 0.001 * (x - 100) * Math.Abs(x - 100);
            };
            var gentler = Terrain.Flattest(easing, Radius, 0, 0, 300, 8);
            Assert.False(gentler.Flat);
            Assert.InRange(gentler.Distance, 75, 125);
            Assert.Equal(10, gentler.Slope, 0);
        }

        [Fact]
        public void TheFloorOfAVShapedGrooveIsNotFlat()
        {
            Terrain.HeightAt groove = (lat, lng) => 100 + 0.3 * Math.Abs(lng * MetresPerDegree);
            Assert.Equal(Math.Atan(0.3) * 180 / Math.PI, Terrain.Slope(groove, Radius, 0, 0), 2);
        }

        [Fact]
        public void DistanceIsAlongTheSurface()
        {
            Assert.Equal(Math.PI * Radius / 2, Terrain.Distance(0, 0, 0, 90, Radius), 3);
            Assert.Equal(Math.PI * Radius / 2, Terrain.Distance(0, 0, 90, 0, Radius), 3);
            Assert.Equal(1000, Terrain.Distance(0, 179.9, 0, -179.9, 180 * 1000 / (0.2 * Math.PI)), 3);
        }
    }
}
