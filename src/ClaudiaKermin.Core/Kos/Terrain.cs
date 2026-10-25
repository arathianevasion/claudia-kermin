// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// The terrain maths behind <c>ADDONS:CLAUDIA</c>'s terrain samples, kept free of KSP: the addon
    /// passes in a height function (the body's terrain height above sea level at a latitude and
    /// longitude in degrees) and the body's radius. Distances are in metres, slopes in degrees.
    /// </summary>
    public static class Terrain
    {
        /// <summary>Terrain height in metres above sea level at a latitude and longitude in degrees.</summary>
        public delegate double HeightAt(double lat, double lng);

        /// <summary>The radius of the ring sampled around a spot to find its slope: about a lander's footprint.</summary>
        public const double Footprint = 5;

        private const int RingSamples = 8;
        private const double Deg = Math.PI / 180;

        /// <summary>Spacing in metres of the rings of candidate spots searched for flat ground.</summary>
        public const double SearchStep = 25;

        /// <summary>
        /// The ground's slope at a spot, sampled on a ring of <see cref="Footprint"/> metres round it:
        /// the tilt of the best-fitting plane, or the steepest rise or fall from the spot to the ring if
        /// that is more (so the floor of a groove or the top of a ridge doesn't read as flat).
        /// </summary>
        public static double Slope(HeightAt height, double radius, double lat, double lng)
        {
            var centre = height(lat, lng);
            // On a ring of evenly spaced samples the least-squares plane's gradients separate.
            double sumEast = 0, sumNorth = 0, sumEast2 = 0, sumNorth2 = 0, steepest = 0;
            for (var i = 0; i < RingSamples; i++)
            {
                var angle = 2 * Math.PI * i / RingSamples;
                var east = Footprint * Math.Sin(angle);
                var north = Footprint * Math.Cos(angle);
                var at = Offset(lat, lng, east, north, radius);
                var h = height(at[0], at[1]);
                sumEast += east * h;
                sumNorth += north * h;
                sumEast2 += east * east;
                sumNorth2 += north * north;
                steepest = Math.Max(steepest, Math.Abs(h - centre) / Footprint);
            }
            var gradEast = sumEast / sumEast2;
            var gradNorth = sumNorth / sumNorth2;
            var plane = Math.Sqrt(gradEast * gradEast + gradNorth * gradNorth);
            return Math.Atan(Math.Max(plane, steepest)) / Deg;
        }

        /// <summary>
        /// Where to land near an aim point: the aim point itself if its slope is at most
        /// <paramref name="maxSlope"/>, else the nearest spot within <paramref name="reach"/> metres
        /// that is (the least steep of the nearest ring that has one), searched on rings
        /// <see cref="SearchStep"/> metres apart. If nothing in reach is flat enough, the least steep
        /// spot found, with <see cref="Spot.Flat"/> false.
        /// </summary>
        public static Spot Flattest(HeightAt height, double radius, double lat, double lng, double reach, double maxSlope)
        {
            var best = Measure(height, radius, lat, lng, lat, lng);
            if (best.Slope <= maxSlope) return best.AsFlat(true);
            var rings = (int)Math.Floor(Math.Max(0, reach) / SearchStep + 1e-9);
            for (var ring = 1; ring <= rings; ring++)
            {
                var distance = ring * SearchStep;
                var count = (int)Math.Ceiling(2 * Math.PI * distance / SearchStep);
                Spot flatHere = null;
                for (var i = 0; i < count; i++)
                {
                    var angle = 2 * Math.PI * i / count;
                    var at = Offset(lat, lng, distance * Math.Sin(angle), distance * Math.Cos(angle), radius);
                    var spot = Measure(height, radius, lat, lng, at[0], at[1]);
                    if (spot.Slope < best.Slope) best = spot;
                    if (spot.Slope <= maxSlope && (flatHere == null || spot.Slope < flatHere.Slope)) flatHere = spot;
                }
                if (flatHere != null) return flatHere.AsFlat(true);
            }
            return best.AsFlat(false);
        }

        /// <summary>Metres along the surface between two spots (latitudes and longitudes in degrees).</summary>
        public static double Distance(double lat1, double lng1, double lat2, double lng2, double radius)
        {
            var dLat = (lat2 - lat1) * Deg;
            var dLng = (lng2 - lng1) * Deg;
            var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Deg) * Math.Cos(lat2 * Deg) * Math.Pow(Math.Sin(dLng / 2), 2);
            return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        private static Spot Measure(HeightAt height, double radius, double aimLat, double aimLng, double lat, double lng)
        {
            return new Spot(lat, lng, Slope(height, radius, lat, lng), height(lat, lng), Distance(aimLat, aimLng, lat, lng, radius), false);
        }

        /// <summary>The latitude and longitude (degrees) a short way east and north (metres) of a spot.</summary>
        public static double[] Offset(double lat, double lng, double east, double north, double radius)
        {
            var newLat = lat + north / radius / Deg;
            var cos = Math.Max(Math.Cos(lat * Deg), 1e-6);
            return new[] { newLat, WrapLongitude(lng + east / (radius * cos) / Deg) };
        }

        /// <summary>A longitude in (-180, 180].</summary>
        public static double WrapLongitude(double lng)
        {
            var wrapped = ((lng + 180) % 360 + 360) % 360 - 180;
            return wrapped == -180 ? 180 : wrapped;
        }
    }

    /// <summary>A candidate landing spot: where it is, its slope (degrees) and terrain height (m), and how far (m) it is from the aim point.</summary>
    public sealed class Spot
    {
        public Spot(double lat, double lng, double slope, double height, double distance, bool flat)
        {
            Lat = lat;
            Lng = lng;
            Slope = slope;
            Height = height;
            Distance = distance;
            Flat = flat;
        }

        public double Lat { get; private set; }
        public double Lng { get; private set; }
        public double Slope { get; private set; }
        public double Height { get; private set; }
        public double Distance { get; private set; }

        /// <summary>The slope is within the limit searched for.</summary>
        public bool Flat { get; private set; }

        internal Spot AsFlat(bool flat)
        {
            return new Spot(Lat, Lng, Slope, Height, Distance, flat);
        }
    }
}
