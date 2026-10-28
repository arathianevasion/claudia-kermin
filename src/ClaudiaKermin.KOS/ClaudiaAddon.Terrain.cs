// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Kos;
using kOS.Safe.Encapsulation;
using kOS.Safe.Exceptions;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Suffixed;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA</c>'s biome names and terrain samples, for the <c>land</c> and <c>ascend</c>
    /// blocks (and anything else a Mission Script needs them for). The maths is in
    /// <see cref="Terrain"/>; this side only reads the body's terrain. All of them only read.
    /// </summary>
    public partial class ClaudiaAddon
    {
        /// <summary>
        /// The farthest <c>FLATSPOT</c> searches, in metres, so a script can't stall a frame on a huge
        /// search; <c>land</c>'s <c>max_divert</c> has the same bound.
        /// </summary>
        private const double MaxReach = 2000;

        private void AddTerrainSuffixes()
        {
            // Block-internal calls (#439): through ClaudiaAddon.Guarded, so a throw becomes lexicon("error", reason) for the block.
            AddSuffix("BIOME", new OneArgsSuffix<Structure, GeoCoordinates>(geo => Guarded("BIOME", () => Biome(geo), null),
                "The science biome name at a GEOPOSITION (\"\" if its body has none)"));
            AddSuffix("HIGHESTTERRAIN", new OneArgsSuffix<ScalarValue, BodyTarget>(HighestTerrain, "The height in m above sea level of a body's highest terrain"));
            AddSuffix("SLOPE", new OneArgsSuffix<Structure, GeoCoordinates>(geo => Guarded("SLOPE", () => Slope(geo), null),
                "The ground's slope in degrees at a GEOPOSITION, over about a lander's footprint"));
            AddSuffix("FLATSPOT", new ThreeArgsSuffix<Structure, GeoCoordinates, ScalarValue, ScalarValue>(
                (geo, reach, maxSlope) => Guarded("FLATSPOT", () => FlatSpot(geo, reach, maxSlope), null),
                "Where to land near a GEOPOSITION: FLATSPOT(geo, reach_m, max_slope_deg) gives lexicon lat, lng, slope, alt, distance, flat"));
        }

        private static StringValue Biome(GeoCoordinates geo)
        {
            var body = geo.Body;
            if (body == null || body.BiomeMap == null) return new StringValue("");
            return new StringValue(ScienceUtil.GetExperimentBiome(body, geo.Latitude, geo.Longitude) ?? "");
        }

        private static ScalarValue HighestTerrain(BodyTarget target)
        {
            var pqs = target.Body == null ? null : target.Body.pqsController;
            return ScalarValue.Create(pqs == null ? 0 : Math.Max(0, pqs.radiusMax - pqs.radius));
        }

        private static ScalarValue Slope(GeoCoordinates geo)
        {
            if (geo.Body == null) throw new KOSException("SLOPE needs a GEOPOSITION on a body");
            return ScalarValue.Create(Terrain.Slope(HeightOn(geo.Body), geo.Body.Radius, geo.Latitude, geo.Longitude));
        }

        private static Lexicon FlatSpot(GeoCoordinates geo, ScalarValue reach, ScalarValue maxSlope)
        {
            if (geo.Body == null) throw new KOSException("FLATSPOT needs a GEOPOSITION on a body");
            var metres = Math.Min(MaxReach, Math.Max(0, reach.GetDoubleValue()));
            var spot = Terrain.Flattest(HeightOn(geo.Body), geo.Body.Radius, geo.Latitude, geo.Longitude, metres, maxSlope.GetDoubleValue());
            var result = new Lexicon();
            result.Add(new StringValue("lat"), ScalarValue.Create(spot.Lat));
            result.Add(new StringValue("lng"), ScalarValue.Create(spot.Lng));
            result.Add(new StringValue("slope"), ScalarValue.Create(spot.Slope));
            result.Add(new StringValue("alt"), ScalarValue.Create(spot.Height));
            result.Add(new StringValue("distance"), ScalarValue.Create(spot.Distance));
            result.Add(new StringValue("flat"), new BooleanValue(spot.Flat));
            return result;
        }

        /// <summary>The body's terrain height above sea level, as kOS's GEOPOSITION:TERRAINHEIGHT reads it (sea level over oceans).</summary>
        private static Terrain.HeightAt HeightOn(CelestialBody body)
        {
            return (lat, lng) =>
            {
                var pqs = body.pqsController;
                if (pqs == null) return 0;
                var height = pqs.GetSurfaceHeight(body.GetRelSurfaceNVector(lat, lng)) - pqs.radius;
                return body.ocean && height < 0 ? 0 : height;
            };
        }
    }
}
