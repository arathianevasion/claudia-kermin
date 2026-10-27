// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>find_landing_site</c>: up to 3 landing sites on an airless body, each under the craft's current ground track within a
    /// window, with the biome, terrain altitude, slope, the UT the track passes over it and the sun's elevation then. The site
    /// search is <see cref="LandingSiteSearch"/>; terrain, biome and sun come live from the body interface.
    /// </summary>
    internal sealed class FindLandingSite : IPlanningTool
    {
        public string Name
        {
            get { return "find_landing_site"; }
        }

        public string Description
        {
            get
            {
                return "Up to 3 landing sites on an airless body that the craft's current orbit passes over within a window, found from "
                    + "the game's live terrain, biome and sun: each has latitude and longitude (degrees), biome, terrain altitude (m above "
                    + "sea level), slope (degrees), the UT the ground track passes over it and the sun's elevation then. Filters: an "
                    + "optional biome, daylight (defaults to the Mission Settings), a slope limit (default 5 degrees), a window in hours "
                    + "(defaults to the Mission Settings time cap) and an optional point to be near. The craft must be in orbit round the "
                    + "body. Its block is land: call it to see that a biome target or a landing the Mission Goal names can be flown, "
                    + "instead of searching by hand, then pass land the biome, not a site. Don't call it for land's target \"any\", or "
                    + "for a body with an atmosphere (an error; deorbit aims those landings).";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"body\":{\"type\":\"string\",\"description\":\"The airless body to land on, which the craft is orbiting.\"},"
                    + "\"biome\":{\"type\":\"string\",\"description\":\"Only sites in this biome (e.g. Highlands, Midlands, Poles); omit for any.\"},"
                    + "\"daylight\":{\"type\":\"boolean\",\"description\":\"Only sites with the sun above the horizon when the track passes; defaults to the Mission Settings.\"},"
                    + "\"max_slope_deg\":{\"type\":\"number\",\"description\":\"The steepest ground allowed, degrees; default 5.\"},"
                    + "\"within_hours\":{\"type\":\"number\",\"description\":\"How far ahead to look along the ground track, hours (at most 48); defaults to the Mission Settings time cap.\"},"
                    + "\"edge_margin_m\":{\"type\":\"number\",\"description\":\"With a biome: how far inside its edge a site should be, m; default 5000 (about the landing accuracy), 0 for none. Sites at the edge come back only when nothing deeper is reachable, marked near_biome_edge.\"},"
                    + "\"near\":{\"type\":\"object\",\"properties\":{\"latitude\":{\"type\":\"number\"},\"longitude\":{\"type\":\"number\"}},"
                    + "\"required\":[\"latitude\",\"longitude\"],\"additionalProperties\":false,\"description\":\"Prefer sites close to this point, degrees (within 15 degrees of arc of it).\"}"
                    + "},\"required\":[\"body\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "finding a landing site"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody api)
        {
            var name = JsonNode.Str(input["body"]);
            if (snapshot.Bodies.Count == 0) return PlanningJson.Error("the Game Snapshot has no body table");
            var system = new BodySystem(snapshot.Bodies);
            var body = system.Find(name);
            if (body == null)
                return PlanningJson.Error("there is no body named " + PlanningJson.Quote(name) + "; the bodies are "
                    + string.Join(", ", snapshot.Bodies.ConvertAll(b => b.Name).ToArray()));
            if (body.AtmosphereHeight > 0 || body.SeaLevelAtm > 0)
                return PlanningJson.Error(body.Name + " has an atmosphere, and find_landing_site is airless-only for now");
            var orbit = snapshot.Orbit;
            var orbitBody = orbit == null ? "" : string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body;
            if (orbit == null || !string.Equals(orbitBody, body.Name, StringComparison.OrdinalIgnoreCase))
                return PlanningJson.Error("the craft is not in orbit round " + body.Name + "; its ground track is only known for the body it orbits, so ask again once it is");

            var query = new LandingSiteQuery
            {
                Biome = JsonNode.Str(input["biome"]),
                Daylight = JsonNode.Bool(input["daylight"]) ?? snapshot.DaylightLandingDefault,
                MaxSlopeDeg = JsonNode.Double(input["max_slope_deg"]) ?? 5,
                WithinHours = JsonNode.Double(input["within_hours"]) ?? snapshot.TimeCapHours,
            };
            query.EdgeMarginM = JsonNode.Double(input["edge_margin_m"]) ?? LandingSiteSearch.DefaultEdgeMarginM;
            if (query.MaxSlopeDeg <= 0 || query.WithinHours <= 0) return PlanningJson.Error("max_slope_deg and within_hours must be more than 0");
            if (query.EdgeMarginM < 0) return PlanningJson.Error("edge_margin_m must be 0 or more");
            var near = input["near"];
            if (near != null)
            {
                query.HasNear = true;
                query.NearLatitude = JsonNode.Double(near["latitude"]) ?? 0;
                query.NearLongitude = JsonNode.Double(near["longitude"]) ?? 0;
                if (Math.Abs(query.NearLatitude) > 90) return PlanningJson.Error("near.latitude must be between -90 and 90");
            }

            var conic = Conic.FromElements(body.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan, orbit.ArgumentOfPeriapsis,
                orbit.MeanAnomalyAtEpoch, orbit.Epoch);
            var found = LandingSiteSearch.Find(body, conic, snapshot.Ut, query, api);
            if (found.Error != null) return PlanningJson.Error(found.Error);

            var json = new JsonWriter().BeginObject().Field("body", body.Name);
            json.Name("sites").BeginArray();
            foreach (var site in found.Sites)
            {
                json.BeginObject()
                    .Field("latitude", PlanningJson.Round(site.Latitude, 3))
                    .Field("longitude", PlanningJson.Round(site.Longitude, 3))
                    .Field("biome", site.Biome)
                    .Field("terrain_altitude", PlanningJson.Round(site.Altitude, 1))
                    .Field("slope_deg", PlanningJson.Round(site.SlopeDeg, 1));
                PlanningJson.Time(json, "pass", site.PassUt, snapshot.Ut)
                    .Field("sun_elevation_deg", PlanningJson.Round(site.SunElevationDeg, 1));
                if (!double.IsNaN(site.FromNearDeg)) json.Field("from_near_deg", PlanningJson.Round(site.FromNearDeg, 1));
                if (site.AtEdge) json.Field("near_biome_edge", true);
                json.EndObject();
            }
            json.EndArray();
            json.Field("window_hours", PlanningJson.Round(found.WindowHours, 2));
            if (found.Sites.Count > 0 && found.Sites[0].AtEdge)
                json.Field("note", "no site more than " + query.EdgeMarginM.ToString("0.##", CultureInfo.InvariantCulture) + " m inside the " + found.Sites[0].Biome
                    + " biome was reachable in " + found.WindowHours.ToString("0.##", CultureInfo.InvariantCulture) + " h; these are within that of its edge, so a landing may come down outside it");
            if (found.Sites.Count == 0)
                json.Field("note", "no site met the limits; of " + LandingSiteSearch.Count(found.Candidates) + " ground-track points, "
                    + LandingSiteSearch.Count(found.AfterBiome) + " were in the biome, " + LandingSiteSearch.Count(found.AfterDaylight)
                    + " also in daylight, " + LandingSiteSearch.Count(found.AfterSlope) + " also gentle enough; widen within_hours, the slope or the biome");
            json.EndObject();

            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = found.Sites.Count == 0 ? "no landing site on " + body.Name
                    : found.Sites.Count + (found.Sites.Count == 1 ? " site" : " sites") + " on " + body.Name + ", the first in the " + found.Sites[0].Biome,
            };
        }
    }
}
