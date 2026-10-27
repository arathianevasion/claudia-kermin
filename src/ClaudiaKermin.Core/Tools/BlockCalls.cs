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
    /// <c>ADDONS:CLAUDIA</c>: the site search, the reentry predictor with the deorbit burn search on it, and the turn time. They run the same C#
    /// as the Planning Tools, on a Game Snapshot read at the call (the reentry calls also read the air and the craft's drag cubes
    /// live, through <see cref="IReentryAir"/> and <see cref="IReentryCraft"/>), and only read. The turn time is the exception:
    /// it needs no Snapshot, only the craft's torque and moment of inertia, read live into a <see cref="TurnCraft"/>. The
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

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DEORBITBURN</c>, for <c>deorbit</c>: the deorbit burn search (<see cref="DeorbitBurn"/>) from the craft's
        /// orbit, by the reentry predictor with <paramref name="air"/> and <paramref name="craft"/>. Takes <c>lat</c>, <c>lng</c>
        /// (the target), <c>earliest_ut</c>, <c>max_wait</c> (s), <c>ground_alt</c> (m), <c>accuracy_km</c> and <c>isp</c> (s; 0 or
        /// missing for none); <c>leave_out</c> (<see cref="LeftOut"/>) is for the adapter's <paramref name="craft"/>. Gives <c>error</c> "", <c>burn_ut</c>, <c>prograde</c>, <c>normal</c>, <c>radial</c> (m/s, maneuver
        /// node components), <c>dv</c>, and the landing: <c>lat</c>, <c>lng</c>, <c>error_km</c>, <c>landing_ut</c>. A best burn
        /// outside the accuracy gives its <c>error</c> with the landing; a search that could not run, only <c>error</c>.
        /// </summary>
        public static IDictionary<string, object> DeorbitBurn(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body,
            IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            SnapshotBody at;
            Conic orbit;
            BodyFrame frame;
            var problem = Reentry(snapshot, body, null, out at, out orbit, out frame);
            if (problem != null) return Error(problem);
            var plan = Tools.DeorbitBurn.Find(at, orbit, new DeorbitAsk
            {
                TargetLatitude = Number(ask, "lat", 0),
                TargetLongitude = Number(ask, "lng", 0),
                EarliestUt = Number(ask, "earliest_ut", snapshot.Ut),
                MaxWait = Number(ask, "max_wait", at.RotationPeriod),
                GroundAltitude = Number(ask, "ground_alt", 0),
                AccuracyKm = Number(ask, "accuracy_km", 10),
                Isp = Number(ask, "isp", 0),
            }, air, craft, frame);
            if (plan.Landing == null || plan.Landing.Error != null) return Error(plan.Error ?? plan.Landing.Error);
            var result = Landing(plan.Landing, plan.ErrorKm);
            result["error"] = plan.Error ?? "";
            result["burn_ut"] = plan.BurnUt;
            result["prograde"] = plan.Prograde;
            result["normal"] = plan.Normal;
            result["radial"] = 0.0;
            result["dv"] = plan.DeltaV;
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:REENTRYIMPACT</c>, for <c>deorbit</c> once it has burned: where the craft's orbit as it is now comes
        /// down, by the reentry predictor. Takes <c>ground_alt</c> (m) and, for the distance, <c>lat</c> and <c>lng</c>. Given
        /// <c>orbit_sma</c>, <c>orbit_ecc</c>, <c>orbit_inc</c>, <c>orbit_lan</c>, <c>orbit_argpe</c>, <c>orbit_mna</c> (degrees, as kOS
        /// gives them), <c>orbit_epoch</c> (UT) and <c>orbit_body</c> (name), it predicts that orbit instead of the craft's: <c>return</c>
        /// asks it of its planned path round Kerbin before the craft is there, with <c>from_ut</c> (UT, default now) the UT that
        /// path begins, so the fall is not taken from an earlier pass of the same conic. Gives
        /// <c>error</c> "", <c>lat</c>, <c>lng</c>, <c>landing_ut</c> and <c>error_km</c> (-1 without a target); or only
        /// <c>error</c>, saying why there is no landing point.
        /// </summary>
        public static IDictionary<string, object> ReentryImpact(IDictionary<string, object> input, GameSnapshot snapshot, IPlanningBody body,
            IReentryAir air, IReentryCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            SnapshotBody at;
            Conic orbit;
            BodyFrame frame;
            var problem = Reentry(snapshot, body, ask, out at, out orbit, out frame);
            if (problem != null) return Error(problem);
            var landing = ReentryPredictor.Predict(at, orbit, Number(ask, "from_ut", snapshot.Ut), Number(ask, "ground_alt", 0), craft.MassKg, air, craft, frame);
            if (landing.Error != null) return Error(landing.Error);
            var km = ask.ContainsKey("lat") && ask.ContainsKey("lng")
                ? LandingSiteSearch.ArcDegrees(landing.Latitude, landing.Longitude, Number(ask, "lat", 0), Number(ask, "lng", 0)) * Math.PI / 180 * at.Radius / 1000
                : -1;
            var result = Landing(landing, km);
            result["error"] = "";
            return result;
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:TURNTIME</c>, for a block that turns to a burn: how long to leave for the turn (<see cref="Tools.TurnTime"/>),
        /// from the torque and moment of inertia the adapter reads into <paramref name="craft"/>. Takes <c>max_stopping_time</c> (s,
        /// kOS's <c>steeringmanager:maxstoppingtime</c>; 2 if missing). Gives <c>error</c> "", <c>turn_time</c> (s, at least 30),
        /// <c>turn_s</c> (the 190° turn alone) and <c>top_rate</c> (deg/s); or only <c>error</c>, "no usable torque".
        /// </summary>
        public static IDictionary<string, object> TurnTime(IDictionary<string, object> input, TurnCraft craft)
        {
            var ask = new Dictionary<string, object>(input ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var turn = Tools.TurnTime.Predict(craft, Number(ask, "max_stopping_time", Tools.TurnTime.KosMaxStoppingTime));
            if (turn.Error != null) return Error(turn.Error);
            return new Dictionary<string, object>
            {
                { "error", "" },
                { "turn_time", turn.Seconds },
                { "turn_s", turn.TurnSeconds },
                { "top_rate", turn.TopRateDegS },
            };
        }

        /// <summary>
        /// The parts a reentry call leaves out of the craft (#326): <c>leave_out</c>, the kOS UIDs of the parts <c>deorbit</c> drops
        /// after its burn, so the predictor flies only what stays. One UID may be given bare. The adapter builds the craft from it.
        /// </summary>
        public static IList<string> LeftOut(IDictionary<string, object> input)
        {
            var uids = new List<string>();
            object value;
            if (input == null || !new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase).TryGetValue("leave_out", out value) || value == null)
                return uids;
            var many = value as System.Collections.IEnumerable;
            if (value is string || many == null) many = new[] { value };
            foreach (var uid in many)
                if (uid != null) uids.Add(Convert.ToString(uid, CultureInfo.InvariantCulture));
            return uids;
        }

        /// <summary>The body, the craft's orbit and how the frame sits on the body, for a reentry call; or why there are none.</summary>
        private static string Reentry(GameSnapshot snapshot, IPlanningBody body, IDictionary<string, object> ask, out SnapshotBody at, out Conic orbit, out BodyFrame frame)
        {
            at = null;
            orbit = null;
            frame = null;
            var elements = snapshot == null ? null : snapshot.Orbit;
            if (ask != null && ask.ContainsKey("orbit_sma"))
            {
                elements = new SnapshotOrbit
                {
                    SemiMajorAxis = Number(ask, "orbit_sma", 0),
                    Eccentricity = Number(ask, "orbit_ecc", 0),
                    Inclination = Number(ask, "orbit_inc", 0),
                    Lan = Number(ask, "orbit_lan", 0),
                    ArgumentOfPeriapsis = Number(ask, "orbit_argpe", 0),
                    MeanAnomalyAtEpoch = Number(ask, "orbit_mna", 0) * Math.PI / 180,
                    Epoch = Number(ask, "orbit_epoch", 0),
                    Body = ask.ContainsKey("orbit_body") ? ask["orbit_body"] as string : null,
                };
            }
            if (elements == null || elements.SemiMajorAxis == 0) return "the Game Snapshot has no orbit for the craft";
            var name = string.IsNullOrEmpty(elements.Body) ? snapshot.Body : elements.Body;
            at = new BodySystem(snapshot.Bodies).Find(name);
            if (at == null || at.Gm <= 0) return "the body table has no " + name;
            if (at.AtmosphereHeight <= 0) return "body has no atmosphere";
            orbit = Conic.FromElements(at.Gm, elements.SemiMajorAxis, elements.Eccentricity, elements.Inclination, elements.Lan,
                elements.ArgumentOfPeriapsis, elements.MeanAnomalyAtEpoch, elements.Epoch);
            string error;
            frame = BodyFrame.Read(at, body, snapshot.Ut, out error);
            return error;
        }

        private static Dictionary<string, object> Landing(ReentryPrediction landing, double km)
        {
            return new Dictionary<string, object>
            {
                { "lat", landing.Latitude },
                { "lng", landing.Longitude },
                { "landing_ut", landing.LandingUt },
                { "error_km", km },
            };
        }

        private static double Number(IDictionary<string, object> ask, string key, double fallback)
        {
            object value;
            if (!ask.TryGetValue(key, out value) || !(value is double)) return fallback;
            return (double)value;
        }

        private static IDictionary<string, object> Error(string reason)
        {
            return new Dictionary<string, object> { { "error", reason } };
        }
    }
}
