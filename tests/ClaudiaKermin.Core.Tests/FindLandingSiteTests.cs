// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>find_landing_site</c> through the Planning Tools entry point against a fake body interface: a sphere the size of the Mun
    /// with terrain, biome and sun given as functions of latitude, longitude and UT, and a craft in a 50 km equatorial orbit, so the
    /// ground track is worked out by hand (angle = mean anomaly, less the body's turn).
    /// </summary>
    public sealed class FindLandingSiteTests
    {
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double Slow = 138984.38;

        private sealed class FakeBody : PlanningBodyBase
        {
            public Func<double, double, double> Terrain = (lat, lon) => 100;
            public Func<double, double, string> BiomeAt = (lat, lon) => "Midlands";
            public Func<double, double, double, double> Sun = (lat, lon, ut) => 45;
            /// <summary>Mirrors the frame, as a game with another axis convention would.</summary>
            public bool Mirrored;
            public int Surveys;

            public override double TerrainAltitude(string body, double latitude, double longitude) { return Terrain(latitude, longitude); }
            public override string Biome(string body, double latitude, double longitude) { return BiomeAt(latitude, longitude); }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return Sun(latitude, longitude, ut); }

            public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
            {
                Surveys++;
                base.Survey(body, samples, facts);
            }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = (Mirrored ? -1 : 1) * Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private readonly PlanningTools _tools = PlanningTools.Default();

        private static GameSnapshot Snapshot(double rotationPeriod = Slow, double meanAnomaly = 0, double inclination = 0, bool atmosphere = false, string orbitBody = "Mun")
        {
            var rp = MunR + 50000;
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = orbitBody };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = rp, Eccentricity = 0, Inclination = inclination, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = meanAnomaly, Epoch = 1000, Body = orbitBody, Periapsis = 50000, Apoapsis = 50000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, SoiRadius = 2429559.1, RotationPeriod = rotationPeriod, Sma = 12000000,
                AtmosphereHeight = atmosphere ? 5000 : 0,
            });
            return snapshot;
        }

        private static double Period()
        {
            var a = MunR + 50000;
            return 2 * Math.PI * Math.Sqrt(a * a * a / MunGm);
        }

        private JsonElement Run(GameSnapshot snapshot, string input, FakeBody body)
        {
            var outcome = _tools.Run("find_landing_site", input, snapshot, body);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input, FakeBody body)
        {
            var outcome = _tools.Run("find_landing_site", input, snapshot, body);
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        private static List<JsonElement> Sites(JsonElement result)
        {
            return result.GetProperty("sites").EnumerateArray().ToList();
        }

        private static double Num(JsonElement site, string name)
        {
            return site.GetProperty(name).GetDouble();
        }

        private static double Lon(JsonElement site)
        {
            return Num(site, "longitude");
        }

        [Fact]
        public void The_tool_is_offered_on_mission_turns_only()
        {
            Assert.Contains("find_landing_site", _tools.Definitions(ToolOffer.Mission).Select(t => t.Name));
            Assert.DoesNotContain("find_landing_site", _tools.Definitions(ToolOffer.CraftCheck).Select(t => t.Name));
        }

        [Fact]
        public void A_body_with_an_atmosphere_is_an_error_saying_airless_only_and_the_body_interface_is_not_asked()
        {
            var fake = new FakeBody();
            var snapshot = Snapshot();
            snapshot.Orbit.Body = "Kerbin";
            snapshot.Body = "Kerbin";
            var error = Error(snapshot, "{\"body\":\"Kerbin\"}", fake);
            Assert.Contains("airless-only", error);
            Assert.Equal(0, fake.Surveys);
            Assert.Contains("airless-only", Error(Snapshot(atmosphere: true), "{\"body\":\"Mun\"}", fake));
        }

        [Fact]
        public void A_craft_not_orbiting_the_body_is_an_error_naming_it()
        {
            var snapshot = Snapshot(orbitBody: "Kerbin");
            Assert.Contains("not in orbit round Mun", Error(snapshot, "{\"body\":\"mun\"}", new FakeBody()));
            Assert.Contains("there is no body named \"Gilly\"", Error(Snapshot(), "{\"body\":\"Gilly\"}", new FakeBody()));
        }

        [Fact]
        public void The_first_site_is_where_the_ground_track_is_now_and_each_pass_is_a_ut_on_the_track()
        {
            // 90° of orbit in, on a body that hardly turns: the craft is over longitude 90 now.
            var snapshot = Snapshot(meanAnomaly: Math.PI / 2);
            var result = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false}", new FakeBody());
            var first = Sites(result)[0];
            Assert.Equal(1000, Num(first, "pass_ut"), 1);
            Assert.Equal("in 0 s", first.GetProperty("pass_in").GetString());
            Assert.Equal(90, Lon(first), 1);
            Assert.Equal(0, Num(first, "latitude"), 1);
            Assert.Equal("Midlands", first.GetProperty("biome").GetString());
            Assert.Equal(100, Num(first, "terrain_altitude"));
            Assert.Equal(0, Num(first, "slope_deg"), 1);
            Assert.Equal(45, Num(first, "sun_elevation_deg"));
            // The others are fixed points the track passes later: longitude at their pass is the angle then, less the body's turn.
            foreach (var site in Sites(result))
            {
                var dt = Num(site, "pass_ut") - 1000;
                var expected = LandingSiteSearch.Wrap180(90 + dt * 360 / Period() - dt * 360 / Slow);
                Assert.Equal(expected, Lon(site), 1);
            }
        }

        [Fact]
        public void The_bodys_rotation_moves_the_ground_track_west_of_the_orbits_angle()
        {
            // A body that turns once in 4 orbits: the track slips west by a quarter of an orbit's angle each orbit.
            var snapshot = Snapshot(rotationPeriod: Period() * 4);
            var result = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false,\"within_hours\":3}", new FakeBody());
            Assert.All(Sites(result), site =>
            {
                var dt = Num(site, "pass_ut") - 1000;
                var expected = LandingSiteSearch.Wrap180(dt * 360 / Period() - dt * 360 / (Period() * 4));
                Assert.Equal(expected, Lon(site), 1);
            });
            Assert.True(Sites(result).Count >= 2);
        }

        [Fact]
        public void Biome_filtering_keeps_only_sites_in_the_biome_whatever_the_case_or_spacing()
        {
            var fake = new FakeBody { BiomeAt = (lat, lon) => lon >= 0 ? "NorthernBasin" : "Highlands" };
            var result = Run(Snapshot(), "{\"body\":\"Mun\",\"daylight\":false,\"biome\":\"northern basin\"}", fake);
            Assert.NotEmpty(Sites(result));
            Assert.All(Sites(result), s =>
            {
                Assert.Equal("NorthernBasin", s.GetProperty("biome").GetString());
                Assert.True(Lon(s) >= -1);
            });
        }

        [Fact]
        public void A_biome_the_track_never_crosses_gives_no_sites_and_says_how_far_the_search_got()
        {
            var result = Run(Snapshot(), "{\"body\":\"Mun\",\"daylight\":false,\"biome\":\"Poles\"}", new FakeBody());
            Assert.Empty(Sites(result));
            Assert.Contains("0 were in the biome", result.GetProperty("note").GetString());
        }

        [Fact]
        public void Slope_is_measured_from_the_terrain_and_steeper_ground_than_the_limit_is_left_out()
        {
            // Ground rising 1 m for every 10 m east, from longitude 0 to 90 (a 5.7° slope); flat elsewhere.
            var metresPerDegree = 2 * Math.PI * MunR / 360;
            Func<double, double, double> terrain = (lat, lon) => lon > 0 && lon < 90 ? lon * metresPerDegree / 10 : 0;
            var fake = new FakeBody { Terrain = terrain };
            var snapshot = Snapshot();
            var strict = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false,\"max_slope_deg\":5}", fake);
            Assert.All(Sites(strict), s => Assert.True(Lon(s) <= 0 || Lon(s) >= 90, "a 5.7° slope passed a 5° limit at " + Lon(s)));
            var loose = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false,\"max_slope_deg\":7,\"near\":{\"latitude\":0,\"longitude\":45}}", fake);
            var site = Sites(loose)[0];
            Assert.InRange(Lon(site), 0, 90);
            Assert.Equal(Math.Atan(0.1) * 180 / Math.PI, Num(site, "slope_deg"), 1);
        }

        [Fact]
        public void Daylight_keeps_sites_with_the_sun_up_at_the_pass_and_off_it_does_not()
        {
            var fake = new FakeBody { Sun = (lat, lon, ut) => lon >= 0 ? 30 : -10 };
            var snapshot = Snapshot(meanAnomaly: Math.PI * 1.5);
            var day = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":true}", fake);
            Assert.NotEmpty(Sites(day));
            Assert.All(Sites(day), s =>
            {
                Assert.True(Lon(s) >= -0.5, "a night site passed the daylight filter at " + Lon(s));
                Assert.Equal(30, Num(s, "sun_elevation_deg"));
            });
            // Over longitude 270 (-90) now, in the dark.
            var any = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false}", fake);
            Assert.True(Lon(Sites(any)[0]) < 0);
            Assert.Equal(-10, Num(Sites(any)[0], "sun_elevation_deg"));
        }

        [Fact]
        public void With_the_sun_at_a_known_place_the_far_side_of_the_craft_is_night_and_the_sub_solar_point_is_day()
        {
            // #312: the sun is overhead at longitude 90 at UT 1000 and, the body turning east, drifts west on it. The craft starts over
            // longitude 90, so the point opposite it (-90) is night for the whole hour: no site there is correct, and the note says why.
            var fake = new FakeBody
            {
                Sun = (lat, lon, ut) => 90 - LandingSiteSearch.ArcDegrees(lat, lon, 0, 90 - 360 * (ut - 1000) / Slow),
            };
            var snapshot = Snapshot(meanAnomaly: Math.PI / 2);
            var far = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":true,\"within_hours\":1,\"near\":{\"latitude\":0,\"longitude\":-90}}", fake);
            Assert.Empty(Sites(far));
            Assert.Contains("0 also in daylight", far.GetProperty("note").GetString());
            var near = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":true,\"within_hours\":1,\"near\":{\"latitude\":0,\"longitude\":90}}", fake);
            Assert.NotEmpty(Sites(near));
            Assert.All(Sites(near), s => Assert.InRange(Num(s, "sun_elevation_deg"), LandingSiteSearch.MinSunElevationDeg, 90));
        }

        [Fact]
        public void The_sun_is_asked_at_the_ut_of_the_pass()
        {
            var asked = new List<double>();
            var fake = new FakeBody { Sun = (lat, lon, ut) => { asked.Add(ut); return 20; } };
            var result = Run(Snapshot(), "{\"body\":\"Mun\"}", fake);
            foreach (var site in Sites(result)) Assert.Contains(asked, ut => Math.Abs(ut - Num(site, "pass_ut")) < 0.5);
        }

        [Fact]
        public void The_ground_track_pass_must_fall_within_the_hours_asked()
        {
            // The biome is only at longitude 270 (-90): three quarters of an orbit (about 2300 s) away.
            var fake = new FakeBody { BiomeAt = (lat, lon) => lon < -80 && lon > -100 ? "Craters" : "Midlands" };
            var snapshot = Snapshot();
            var short_ = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false,\"biome\":\"Craters\",\"within_hours\":0.5}", fake);
            Assert.Empty(Sites(short_));
            var long_ = Run(snapshot, "{\"body\":\"Mun\",\"daylight\":false,\"biome\":\"Craters\",\"within_hours\":1}", fake);
            var site = Sites(long_)[0];
            Assert.InRange(Num(site, "pass_ut") - 1000, 0.5 * 3600 * 0.99, 3600);
            Assert.True(Num(site, "pass_ut") - 1000 <= 3600);
            Assert.Equal(1, Num(long_, "window_hours"));
        }

        [Fact]
        public void Within_hours_and_daylight_default_from_the_mission_settings_on_the_snapshot()
        {
            var fake = new FakeBody { Sun = (lat, lon, ut) => -5 };
            var snapshot = Snapshot();
            snapshot.DaylightLandingDefault = true;
            Assert.Empty(Sites(Run(snapshot, "{\"body\":\"Mun\"}", fake)));
            snapshot.DaylightLandingDefault = false;
            Assert.NotEmpty(Sites(Run(snapshot, "{\"body\":\"Mun\"}", fake)));
            snapshot.TimeCapHours = 2;
            Assert.Equal(2, Num(Run(snapshot, "{\"body\":\"Mun\"}", fake), "window_hours"));
            Assert.Equal(3, Num(Run(snapshot, "{\"body\":\"Mun\",\"within_hours\":3}", fake), "window_hours"));
        }

        [Fact]
        public void At_most_three_sites_come_back_and_they_are_apart()
        {
            var result = Run(Snapshot(), "{\"body\":\"Mun\",\"daylight\":false,\"within_hours\":6}", new FakeBody());
            var sites = Sites(result);
            Assert.Equal(3, sites.Count);
            for (var i = 0; i < sites.Count; i++)
                for (var j = i + 1; j < sites.Count; j++)
                    Assert.True(LandingSiteSearch.ArcDegrees(Num(sites[i], "latitude"), Lon(sites[i]), Num(sites[j], "latitude"), Lon(sites[j])) >= LandingSiteSearch.MinSeparationDeg);
        }

        [Fact]
        public void A_near_point_orders_sites_by_closeness_and_leaves_out_track_far_from_it()
        {
            var result = Run(Snapshot(), "{\"body\":\"Mun\",\"daylight\":false,\"near\":{\"latitude\":0,\"longitude\":120}}", new FakeBody());
            var sites = Sites(result);
            Assert.NotEmpty(sites);
            Assert.Equal(120, Lon(sites[0]), 0);
            Assert.Equal(0, Num(sites[0], "from_near_deg"), 0);
            Assert.All(sites, s => Assert.InRange(Num(s, "from_near_deg"), 0, 15));
            for (var i = 1; i < sites.Count; i++) Assert.True(Num(sites[i], "from_near_deg") >= Num(sites[i - 1], "from_near_deg"));
        }

        [Fact]
        public void Latitude_follows_an_inclined_orbit()
        {
            var result = Run(Snapshot(inclination: 30, meanAnomaly: Math.PI / 2), "{\"body\":\"Mun\",\"daylight\":false}", new FakeBody());
            Assert.Equal(30, Num(Sites(result)[0], "latitude"), 1);
        }

        [Fact]
        public void A_frame_the_ground_track_does_not_expect_is_an_error_not_a_wrong_track()
        {
            Assert.Contains("does not sit on Mun", Error(Snapshot(), "{\"body\":\"Mun\"}", new FakeBody { Mirrored = true }));
        }

        [Fact]
        public void A_body_interface_that_reads_nothing_is_an_error()
        {
            var outcome = _tools.Run("find_landing_site", "{\"body\":\"Mun\"}", Snapshot(), new NoPlanningBody());
            Assert.True(outcome.IsError);
            Assert.Contains("could not say where", outcome.Json);
        }

        [Fact]
        public void Terrain_that_cannot_be_read_is_an_error()
        {
            var fake = new FakeBody { Terrain = (lat, lon) => double.NaN };
            Assert.Contains("terrain", Error(Snapshot(), "{\"body\":\"Mun\",\"daylight\":false}", fake));
        }

        [Fact]
        public void The_body_interface_is_asked_in_batches_not_a_call_per_point()
        {
            var fake = new FakeBody();
            Run(Snapshot(), "{\"body\":\"Mun\",\"biome\":\"Midlands\",\"within_hours\":24}", fake);
            Assert.InRange(fake.Surveys, 2, 10);
        }

        [Fact]
        public void The_result_stays_inside_the_size_limit_and_numbers_are_rounded()
        {
            var result = Run(Snapshot(), "{\"body\":\"Mun\",\"daylight\":false}", new FakeBody());
            var text = result.GetRawText();
            Assert.True(text.Length < PlanningTools.MaxResultChars);
            Assert.All(Sites(result), s => Assert.Equal(Math.Round(Lon(s), 3), Lon(s)));
            Assert.Equal("Mun", result.GetProperty("body").GetString());
        }

        // ---- The block-internal call land makes for a biome target (ADR 0007): the same search, on the body orbited ----

        private static Dictionary<string, object> Ask(params object[] keysAndValues)
        {
            var input = new Dictionary<string, object>();
            for (var i = 0; i < keysAndValues.Length; i += 2) input[(string)keysAndValues[i]] = keysAndValues[i + 1];
            return input;
        }

        [Fact]
        public void The_block_call_gives_the_first_site_in_the_biome_on_the_body_orbited()
        {
            var fake = new FakeBody { BiomeAt = (lat, lon) => lon >= 45 ? "Midlands" : "Highlands" };
            var site = BlockCalls.LandingSite(Ask("biome", "midlands", "daylight", false, "max_slope_deg", 8.0, "within_hours", 1.0), Snapshot(), fake);
            Assert.Equal("", site["error"]);
            Assert.Equal("Midlands", site["biome"]);
            // The soonest pass in the biome: the first track point (2 degrees apart) past longitude 45, an eighth of an orbit on.
            Assert.InRange((double)site["lng"], 45, 47);
            Assert.Equal(0, (double)site["lat"], 1);
            Assert.True((double)site["pass_ut"] > 1000);
            Assert.Equal(45.0, site["sun_elevation_deg"]);
        }

        [Fact]
        public void The_block_call_with_a_near_point_gives_the_site_nearest_it()
        {
            var site = BlockCalls.LandingSite(Ask("biome", "Midlands", "daylight", false, "within_hours", 1.0,
                "near", Ask("latitude", 0.0, "longitude", -100.0)), Snapshot(), new FakeBody());
            Assert.Equal("", site["error"]);
            Assert.Equal(-100, (double)site["lng"], 0);
        }

        [Fact]
        public void The_block_call_keeps_to_daylight_when_asked()
        {
            // Sun up only east of longitude 90: the first daylight site is there, not where the craft is now.
            var fake = new FakeBody { Sun = (lat, lon, ut) => lon >= 90 && lon < 170 ? 30 : -30 };
            var site = BlockCalls.LandingSite(Ask("biome", "Midlands", "daylight", true, "within_hours", 1.0), Snapshot(), fake);
            Assert.Equal("", site["error"]);
            Assert.InRange((double)site["lng"], 89.5, 170);
        }

        [Fact]
        public void The_block_call_with_no_site_says_why_naming_the_biome()
        {
            var site = BlockCalls.LandingSite(Ask("biome", "Poles", "daylight", false, "within_hours", 1.0), Snapshot(), new FakeBody());
            var error = (string)site["error"];
            Assert.StartsWith("no site in the Poles biome", error);
            Assert.Contains("0 were in the biome", error);
            // The tool's advice names its inputs; the block gives its own.
            Assert.DoesNotContain("within_hours", error);
        }

        [Fact]
        public void The_block_call_passes_on_the_searchs_errors()
        {
            Assert.Contains("airless-only", (string)BlockCalls.LandingSite(Ask("biome", "Midlands"), Snapshot(atmosphere: true), new FakeBody())["error"]);
            Assert.Contains("max_slope_deg", (string)BlockCalls.LandingSite(Ask("biome", "Midlands", "max_slope_deg", 0.0), Snapshot(), new FakeBody())["error"]);
            var landed = Snapshot();
            landed.Orbit = null;
            Assert.Contains("not in orbit", (string)BlockCalls.LandingSite(Ask("biome", "Midlands"), landed, new FakeBody())["error"]);
        }

        [Fact]
        public void The_block_call_ignores_a_body_it_is_given_and_searches_the_one_orbited()
        {
            var site = BlockCalls.LandingSite(Ask("body", "Kerbin", "biome", "Midlands", "daylight", false, "within_hours", 1.0), Snapshot(), new FakeBody());
            Assert.Equal("", site["error"]);
        }
    }
}
