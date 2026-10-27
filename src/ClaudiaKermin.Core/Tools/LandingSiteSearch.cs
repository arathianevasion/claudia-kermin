// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>What to look for: <c>find_landing_site</c>'s inputs once defaulted, and what a Building Block will pass later.</summary>
    internal sealed class LandingSiteQuery
    {
        /// <summary>The biome wanted; null or empty for any.</summary>
        public string Biome;
        /// <summary>The sun must be up at the pass.</summary>
        public bool Daylight = true;
        public double MaxSlopeDeg = 5;
        /// <summary>How far ahead the ground track is searched, hours.</summary>
        public double WithinHours = 6;
        public bool HasNear;
        public double NearLatitude;
        public double NearLongitude;
        public int MaxSites = 3;
    }

    internal sealed class LandingSite
    {
        public double Latitude;
        public double Longitude;
        public string Biome = "";
        /// <summary>Terrain height above sea level, m.</summary>
        public double Altitude;
        public double SlopeDeg;
        /// <summary>The UT the craft's current ground track passes over the site.</summary>
        public double PassUt;
        /// <summary>The sun's elevation at <see cref="PassUt"/>, degrees.</summary>
        public double SunElevationDeg;
        /// <summary>Degrees of arc from the <c>near</c> point; NaN without one.</summary>
        public double FromNearDeg = double.NaN;
    }

    internal sealed class LandingSiteResult
    {
        public string Error;
        public readonly List<LandingSite> Sites = new List<LandingSite>();
        /// <summary>Ground-track points looked at, and how many were left after the biome, daylight and slope tests, for saying why a search came back empty.</summary>
        public int Candidates;
        public int AfterBiome;
        public int AfterDaylight;
        public int AfterSlope;
        /// <summary>The window actually searched, hours (the ask, held to what the search can cover).</summary>
        public double WindowHours;
    }

    /// <summary>
    /// The landing-site search (#278), in Core so the <c>find_landing_site</c> tool and the land-by-biome block share it. The craft's
    /// current orbit gives the ground track: the orbit is propagated here and turned into latitude and longitude with the body's
    /// rotation, after the body interface says where the orbit's frame sits on the body (three reads, which also catch a frame this
    /// code did not expect). Each point of the track within the window is a candidate site, tried in order of pass time (or
    /// closeness to a <c>near</c> point) against the biome, the sun and the slope, so a site is always one the craft will fly over.
    /// The body interface is asked in batches; it is asked nothing about a body with an atmosphere, which is refused.
    /// </summary>
    internal static class LandingSiteSearch
    {
        /// <summary>The sun must be at least this high at the pass for a daylight site, degrees.</summary>
        public const double MinSunElevationDeg = 5;
        public const double MaxWindowHours = 48;
        /// <summary>The distance either side of a site the slope is measured over, m.</summary>
        public const double SlopeSpacing = 25;
        /// <summary>Sites in one result are at least this far apart, degrees of arc.</summary>
        public const double MinSeparationDeg = 2;
        private const int TrackStepsPerOrbit = 180;
        private const int MaxTrackSamples = 2000;
        /// <summary>The most track points surveyed for the biome and the sun, and the most whose slope is measured, per search.</summary>
        private const int MaxCandidates = 240;
        private const int MaxSlopeChecks = 60;
        private const int SlopeBatch = 12;
        /// <summary>With a <c>near</c> point, only track within this many degrees of it is a candidate.</summary>
        private const double NearRadiusDeg = 15;

        public static LandingSiteResult Find(SnapshotBody body, Conic orbit, double now, LandingSiteQuery query, IPlanningBody api)
        {
            var result = new LandingSiteResult();
            if (body.AtmosphereHeight > 0)
            {
                result.Error = body.Name + " has an atmosphere, and find_landing_site is airless-only for now";
                return result;
            }
            if (!orbit.Closed || orbit.Period <= 0)
            {
                result.Error = "the craft's orbit round " + body.Name + " is not closed, so it has no repeating ground track to search";
                return result;
            }

            double lonOffset, handed;
            var frame = Frame(body.Name, api, out lonOffset, out handed);
            if (frame != null)
            {
                result.Error = frame;
                return result;
            }

            var window = Math.Max(0.05, Math.Min(MaxWindowHours, query.WithinHours)) * 3600;
            result.WindowHours = window / 3600;
            var track = Track(body, orbit, now, window, lonOffset, handed);
            result.Candidates = track.Count;

            var candidates = Candidates(track, query);
            var wantBiome = !string.IsNullOrEmpty(query.Biome);
            var facts = (wantBiome ? SiteFacts.Biome : 0) | (query.Daylight ? SiteFacts.Sun : 0);
            if (facts != 0 && candidates.Count > 0)
            {
                api.Survey(body.Name, candidates.Select(c => c.Sample).ToList(), facts);
                if (wantBiome)
                {
                    candidates = candidates.Where(c => SameBiome(c.Sample.Biome, query.Biome)).ToList();
                }
                result.AfterBiome = candidates.Count;
                if (query.Daylight)
                    candidates = candidates.Where(c => c.Sample.SunElevation >= MinSunElevationDeg).ToList();
            }
            else
            {
                result.AfterBiome = candidates.Count;
            }
            result.AfterDaylight = candidates.Count;

            var sites = new List<LandingSite>();
            var checkedSlope = 0;
            var unreadable = 0;
            var passedSlope = 0;
            for (var at = 0; at < candidates.Count && sites.Count < query.MaxSites && checkedSlope < MaxSlopeChecks; at += SlopeBatch)
            {
                var batch = candidates.Skip(at).Take(Math.Min(SlopeBatch, MaxSlopeChecks - checkedSlope)).ToList();
                checkedSlope += batch.Count;
                var points = new List<SiteSample>();
                foreach (var c in batch) points.AddRange(Cross(body, c.Sample));
                api.Survey(body.Name, points, SiteFacts.Altitude | SiteFacts.Biome | SiteFacts.Sun);
                for (var i = 0; i < batch.Count && sites.Count < query.MaxSites; i++)
                {
                    var five = points.Skip(i * 5).Take(5).ToList();
                    if (five.Any(p => double.IsNaN(p.Altitude)))
                    {
                        unreadable++;
                        continue;
                    }
                    var slope = Slope(five);
                    if (slope > query.MaxSlopeDeg) continue;
                    passedSlope++;
                    var c = batch[i];
                    if (sites.Any(s => ArcDegrees(s.Latitude, s.Longitude, c.Sample.Latitude, c.Sample.Longitude) < MinSeparationDeg)) continue;
                    sites.Add(new LandingSite
                    {
                        Latitude = c.Sample.Latitude, Longitude = c.Sample.Longitude, Biome = five[0].Biome, Altitude = five[0].Altitude,
                        SlopeDeg = slope, PassUt = c.Sample.Ut, SunElevationDeg = five[0].SunElevation, FromNearDeg = c.FromNear,
                    });
                }
            }
            result.AfterSlope = passedSlope;
            if (candidates.Count > 0 && unreadable == checkedSlope)
            {
                result.Error = "the terrain on " + body.Name + " could not be read";
                return result;
            }
            result.Sites.AddRange(sites);
            return result;
        }

        private sealed class Candidate
        {
            public SiteSample Sample;
            public double FromNear = double.NaN;
        }

        private sealed class TrackPoint
        {
            public double Latitude;
            public double Longitude;
            public double Ut;
        }

        /// <summary>How the orbit's frame sits on the body: the longitude of its x axis and whether longitude grows with its angle (1) or against it (-1); an error text when the three reads don't fit a frame this code knows.</summary>
        private static string Frame(string body, IPlanningBody api, out double lonOffset, out double handed)
        {
            lonOffset = 0;
            handed = 1;
            double lat0, lon0, lat1, lon1, lat2, lon2;
            if (!api.SurfaceUnder(body, 1, 0, 0, out lat0, out lon0) || !api.SurfaceUnder(body, 0, 1, 0, out lat1, out lon1)
                || !api.SurfaceUnder(body, 0, 0, 1, out lat2, out lon2)
                || double.IsNaN(lat0 + lon0 + lat1 + lon1 + lat2))
                return "the game could not say where the orbit's frame sits on " + body;
            var quarter = Wrap180(lon1 - lon0);
            if (Math.Abs(lat0) > 1 || Math.Abs(lat1) > 1 || lat2 < 89 || Math.Abs(Math.Abs(quarter) - 90) > 1)
                return "the orbit's frame does not sit on " + body + " the way the ground track assumes (z north, x and y on the equator)";
            lonOffset = lon0;
            handed = quarter > 0 ? 1 : -1;
            return null;
        }

        /// <summary>The craft's ground track over the window: latitude, and the longitude fixed to the body, at evenly spaced UTs.</summary>
        private static List<TrackPoint> Track(SnapshotBody body, Conic orbit, double now, double window, double lonOffset, double handed)
        {
            var step = orbit.Period / TrackStepsPerOrbit;
            var count = (int)Math.Min(MaxTrackSamples, Math.Ceiling(window / step) + 1);
            step = count > 1 ? window / (count - 1) : window;
            var turn = body.RotationPeriod > 0 ? 360 / body.RotationPeriod : 0;
            var track = new List<TrackPoint>();
            for (var i = 0; i < count; i++)
            {
                var ut = now + i * step;
                var r = orbit.Position(ut);
                var length = r.Length;
                if (length <= 0) continue;
                var lat = Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI;
                var lon = lonOffset + handed * Math.Atan2(r.Y, r.X) * 180 / Math.PI - turn * (ut - now);
                track.Add(new TrackPoint { Latitude = lat, Longitude = Wrap180(lon), Ut = ut });
            }
            return track;
        }

        /// <summary>The track points worth testing: in pass order, or with a <c>near</c> point the ones close to it, nearest first.</summary>
        private static List<Candidate> Candidates(List<TrackPoint> track, LandingSiteQuery query)
        {
            IEnumerable<Candidate> all = track.Select(t => new Candidate
            {
                Sample = new SiteSample { Latitude = t.Latitude, Longitude = t.Longitude, Ut = t.Ut },
                FromNear = query.HasNear ? ArcDegrees(t.Latitude, t.Longitude, query.NearLatitude, query.NearLongitude) : double.NaN,
            });
            if (query.HasNear)
                return all.Where(c => c.FromNear <= NearRadiusDeg).OrderBy(c => c.FromNear).Take(MaxCandidates).ToList();
            var list = all.ToList();
            var stride = (int)Math.Ceiling(list.Count / (double)MaxCandidates);
            return stride <= 1 ? list : list.Where((c, i) => i % stride == 0).ToList();
        }

        /// <summary>The site and the four points a slope is measured from (east, west, north, south, each <see cref="SlopeSpacing"/> away).</summary>
        private static IEnumerable<SiteSample> Cross(SnapshotBody body, SiteSample site)
        {
            var dLat = SlopeSpacing / body.Radius * 180 / Math.PI;
            var cos = Math.Max(0.05, Math.Cos(site.Latitude * Math.PI / 180));
            var dLon = dLat / cos;
            yield return new SiteSample { Latitude = site.Latitude, Longitude = site.Longitude, Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude, Longitude = Wrap180(site.Longitude + dLon), Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude, Longitude = Wrap180(site.Longitude - dLon), Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude + dLat, Longitude = site.Longitude, Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude - dLat, Longitude = site.Longitude, Ut = site.Ut };
        }

        /// <summary>The ground's steepness from the centre and the four points round it, degrees.</summary>
        private static double Slope(List<SiteSample> five)
        {
            var east = (five[1].Altitude - five[2].Altitude) / (2 * SlopeSpacing);
            var north = (five[3].Altitude - five[4].Altitude) / (2 * SlopeSpacing);
            return Math.Atan(Math.Sqrt(east * east + north * north)) * 180 / Math.PI;
        }

        /// <summary>Biome names match ignoring case, spaces and punctuation ("Northern Basin" is stock's "NorthernBasin").</summary>
        public static bool SameBiome(string a, string b)
        {
            return string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Squash(string text)
        {
            return new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
        }

        public static double Wrap180(double degrees)
        {
            var wrapped = degrees % 360;
            if (wrapped > 180) wrapped -= 360;
            if (wrapped <= -180) wrapped += 360;
            return wrapped;
        }

        /// <summary>Great-circle distance between two points, degrees of arc.</summary>
        public static double ArcDegrees(double lat1, double lon1, double lat2, double lon2)
        {
            double rad = Math.PI / 180;
            var a = Math.Pow(Math.Sin((lat2 - lat1) * rad / 2), 2) + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Pow(Math.Sin((lon2 - lon1) * rad / 2), 2);
            return 2 * Math.Asin(Math.Min(1, Math.Sqrt(a))) / rad;
        }

        public static string Count(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
