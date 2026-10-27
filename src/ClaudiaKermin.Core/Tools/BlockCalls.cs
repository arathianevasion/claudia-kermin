// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The block-internal calls (ADR 0006, ADR 0007): the mod's calculations a Building Block makes while it flies, through
    /// <c>ADDONS:CLAUDIA</c>. They run the same C# as the Planning Tools, on a Game Snapshot read at the call, and only read. The
    /// Script Check lets the Core Library call them and refuses them in a Mission Script. Inputs and results are plain values, as
    /// <c>KosValues</c> converts a kOS lexicon.
    /// </summary>
    public static class BlockCalls
    {
        /// <summary>
        /// <c>ADDONS:CLAUDIA:LANDINGSITE</c>, for <c>land</c> with a biome target: <c>find_landing_site</c>'s search on the body the
        /// craft orbits (any <c>body</c> given is ignored), taking its other inputs (<c>biome</c>, <c>daylight</c>,
        /// <c>max_slope_deg</c>, <c>within_hours</c>, <c>near</c>). Gives the first site, the soonest pass or the nearest the
        /// <c>near</c> point: <c>error</c> "", <c>lat</c>, <c>lng</c>, <c>biome</c>, <c>terrain_altitude</c>, <c>slope_deg</c>,
        /// <c>pass_ut</c>, <c>sun_elevation_deg</c>. With no site, or a search that could not run, only <c>error</c>, saying why.
        /// </summary>
        public static IDictionary<string, object> LandingSite(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var orbited = snapshot == null ? "" : snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            ask["body"] = orbited ?? "";
            var outcome = PlanningTools.Default().Run("find_landing_site", ToolTestGate.InputJson(ask), snapshot, body);
            var result = JsonReader.Parse(outcome.Json);
            if (outcome.IsError) return Error(JsonNode.Str(result["error"]));

            var sites = result["sites"];
            if (sites == null || sites.Items.Count == 0)
            {
                object biome;
                ask.TryGetValue("biome", out biome);
                var hours = (JsonNode.Double(result["window_hours"]) ?? 0).ToString("0.#", CultureInfo.InvariantCulture);
                // The tool's advice is for Claudia's inputs; the block gives its own.
                var note = JsonNode.Str(result["note"]);
                var advice = note.IndexOf("; widen", StringComparison.Ordinal);
                if (advice >= 0) note = note.Substring(0, advice);
                return Error("no site in the " + Convert.ToString(biome, CultureInfo.InvariantCulture) + " biome on " + orbited + " under the next "
                    + hours + " h of ground track: " + note);
            }
            var site = sites.Items[0];
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "lat", JsonNode.Double(site["latitude"]) ?? 0 },
                { "lng", JsonNode.Double(site["longitude"]) ?? 0 },
                { "biome", JsonNode.Str(site["biome"]) },
                { "terrain_altitude", JsonNode.Double(site["terrain_altitude"]) ?? 0 },
                { "slope_deg", JsonNode.Double(site["slope_deg"]) ?? 0 },
                { "pass_ut", JsonNode.Double(site["pass_ut"]) ?? 0 },
                { "sun_elevation_deg", JsonNode.Double(site["sun_elevation_deg"]) ?? 0 },
            };
        }

        private static IDictionary<string, object> Error(string reason)
        {
            return new Dictionary<string, object> { { "error", reason } };
        }
    }
}
