// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Diagnostics;
using System.Globalization;
using ClaudiaKermin.Adapter;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Tools;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The block-internal calls (ADR 0006, ADR 0007): the mod's calculations a Building Block makes while it flies, on the same C#
    /// as the Planning Tools (<see cref="BlockCalls"/>). The Script Check lets only the Core Library call them; a Mission Script
    /// passes a block its goal instead. Each reads a fresh Game Snapshot of the processor's vessel (TURNTIME reads only its torque
    /// and moment of inertia) and never changes the game.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddBlockCallSuffixes()
        {
            AddSuffix("LANDINGSITE", new OneArgsSuffix<Lexicon, Lexicon>(LandingSite,
                "Core Library only (land): find_landing_site's search on the body orbited; gives the first site as lexicon error, lat, lng, biome, ..."));
            AddSuffix("DEORBITBURN", new OneArgsSuffix<Lexicon, Lexicon>(DeorbitBurn,
                "Core Library only (deorbit): the deorbit burn that lands on lat, lng by the reentry predictor; lexicon error, burn_ut, prograde, normal, radial, dv, lat, lng, error_km, ...; leave_out (part UIDs) flies the craft without them"));
            AddSuffix("REENTRYIMPACT", new OneArgsSuffix<Lexicon, Lexicon>(ReentryImpact,
                "Core Library only (deorbit, return): where the orbit as it is now, or the one given as orbit_sma, orbit_ecc, ..., comes down, held retrograde; lexicon error, lat, lng, landing_ut, error_km"));
            AddSuffix("TURNTIME", new OneArgsSuffix<Lexicon, Lexicon>(TurnTime,
                "Core Library only (a block that turns to a burn): the time to leave for a 190 degree turn on the wheels and RCS (if on), given max_stopping_time; lexicon error, turn_time, turn_s, top_rate"));
            AddSuffix("EJECTIONBURN", new OneArgsSuffix<Lexicon, Lexicon>(EjectionBurn,
                "Core Library only (transfer_to): the cheapest ejection to target (a moon's name) arriving at target_pe, within one synodic period from earliest_ut or window; lexicon error, ut, prograde, normal, radial, dv, encounter_ut, arrival_ut, target_pe_actual, ..."));
        }

        private Lexicon EjectionBurn(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var node = BlockCalls.EjectionBurn(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel));
            // The search's time, against the minutes RSVP took, and the node, so block tests can read it back from KSP.log.
            Debug.Log(ModInfo.LogTag + " block call EJECTIONBURN took " + Ms(watch) + " ms: " + (node["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.0} m/s); Pe {5:0} m at UT {6:0}; searched {7:0} s from UT {8:0.0}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["target_pe_actual"], node["arrival_ut"], node["synodic"], node["search_from"])
                : node["error"]));
            return (Lexicon)KosValues.FromPlain(node);
        }

        private Lexicon TurnTime(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var craft = TurnCraftReader.Read(shared.Vessel);
            var turn = BlockCalls.TurnTime(KosValues.ToDictionary(input), craft);
            // The prediction, so block tests can read it back from KSP.log against the turn the block flew.
            Debug.Log(ModInfo.LogTag + " block call TURNTIME took " + Ms(watch) + " ms: " + string.Format(CultureInfo.InvariantCulture,
                "{0}; wheels {1:0.##}/{2:0.##} kN m, RCS {3:0.##}/{4:0.##} kN m{5}, MOI {6:0.##}/{7:0.##} t m2 (pitch/yaw)",
                turn["error"] as string == ""
                    ? string.Format(CultureInfo.InvariantCulture, "turn_time {0:0.0} s, turn {1:0.0} s at up to {2:0.0} deg/s", turn["turn_time"], turn["turn_s"], turn["top_rate"])
                    : turn["error"],
                craft.WheelPitch, craft.WheelYaw, craft.RcsPitch, craft.RcsYaw, craft.RcsOn ? " (on)" : " (off)", craft.MoiPitch, craft.MoiYaw));
            return (Lexicon)KosValues.FromPlain(turn);
        }

        private Lexicon DeorbitBurn(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var vessel = shared.Vessel;
            var ask = KosValues.ToDictionary(input);
            var found = BlockCalls.DeorbitBurn(ask, SnapshotReader.Read(vessel), new PlanningBody(),
                new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask)));
            Debug.Log(ModInfo.LogTag + " block call DEORBITBURN took " + Ms(watch) + " ms: " + Landing(found));
            return (Lexicon)KosValues.FromPlain(found);
        }

        private Lexicon ReentryImpact(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var vessel = shared.Vessel;
            var ask = KosValues.ToDictionary(input);
            var landing = BlockCalls.ReentryImpact(ask, SnapshotReader.Read(vessel), new PlanningBody(),
                new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask)));
            Debug.Log(ModInfo.LogTag + " block call REENTRYIMPACT took " + Ms(watch) + " ms: " + Landing(landing));
            return (Lexicon)KosValues.FromPlain(landing);
        }

        /// <summary>A predicted landing for the log, so predicted against actual can be read back from KSP.log.</summary>
        private static string Landing(System.Collections.Generic.IDictionary<string, object> result)
        {
            var text = result["error"] as string ?? "";
            if (!result.ContainsKey("lat")) return text;
            // The frame the ground was read in (#326): stock's inverse-rotation angle and whether the frame turns with the body.
            return string.Format(CultureInfo.InvariantCulture, "{0}landing {1:0.0000}, {2:0.0000} at UT {3:0}, {4:0.00} km from the target{5}; inverse rotation {6:0.00} deg{7}",
                text == "" ? "" : text + "; ", result["lat"], result["lng"], result["landing_ut"], result["error_km"],
                result.ContainsKey("burn_ut")
                    ? string.Format(CultureInfo.InvariantCulture, "; burn at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00} m/s", result["burn_ut"], result["prograde"], result["normal"])
                    : "",
                Planetarium.InverseRotAngle, FlightGlobals.RefFrameIsRotating ? " (rotating frame)" : "");
        }

        private static string Ms(Stopwatch watch)
        {
            return watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private Lexicon LandingSite(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var snapshot = SnapshotReader.Read(shared.Vessel);
            var site = BlockCalls.LandingSite(KosValues.ToDictionary(input), snapshot, new PlanningBody());
            // The search runs on the main thread, as kOS does: its time against the frame budget is for tuning it.
            var found = site["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "{0} at {1}, {2}", site["biome"], site["lat"], site["lng"])
                : site["error"];
            Debug.Log(ModInfo.LogTag + " block call LANDINGSITE took "
                + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms: " + found);
            return (Lexicon)KosValues.FromPlain(site);
        }
    }
}
