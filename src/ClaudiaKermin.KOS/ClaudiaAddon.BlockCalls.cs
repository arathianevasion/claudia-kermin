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
    /// passes a block its goal instead. Each reads a fresh Game Snapshot of the processor's vessel and never changes the game.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddBlockCallSuffixes()
        {
            AddSuffix("LANDINGSITE", new OneArgsSuffix<Lexicon, Lexicon>(LandingSite,
                "Core Library only (land): find_landing_site's search on the body orbited; gives the first site as lexicon error, lat, lng, biome, ..."));
            AddSuffix("DEORBITBURN", new OneArgsSuffix<Lexicon, Lexicon>(DeorbitBurn,
                "Core Library only (deorbit): the deorbit burn that lands on lat, lng by the reentry predictor; lexicon error, burn_ut, prograde, normal, radial, dv, lat, lng, error_km, ..."));
            AddSuffix("REENTRYIMPACT", new OneArgsSuffix<Lexicon, Lexicon>(ReentryImpact,
                "Core Library only (deorbit): where the orbit as it is now comes down, held retrograde; lexicon error, lat, lng, landing_ut, error_km"));
        }

        private Lexicon DeorbitBurn(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var vessel = shared.Vessel;
            var found = BlockCalls.DeorbitBurn(KosValues.ToDictionary(input), SnapshotReader.Read(vessel), new PlanningBody(),
                new ReentryAir(vessel.mainBody), new ReentryCraft(vessel));
            Debug.Log(ModInfo.LogTag + " block call DEORBITBURN took " + Ms(watch) + " ms: " + Landing(found));
            return (Lexicon)KosValues.FromPlain(found);
        }

        private Lexicon ReentryImpact(Lexicon input)
        {
            var watch = Stopwatch.StartNew();
            var vessel = shared.Vessel;
            var landing = BlockCalls.ReentryImpact(KosValues.ToDictionary(input), SnapshotReader.Read(vessel), new PlanningBody(),
                new ReentryAir(vessel.mainBody), new ReentryCraft(vessel));
            Debug.Log(ModInfo.LogTag + " block call REENTRYIMPACT took " + Ms(watch) + " ms: " + Landing(landing));
            return (Lexicon)KosValues.FromPlain(landing);
        }

        /// <summary>A predicted landing for the log, so predicted against actual can be read back from KSP.log.</summary>
        private static string Landing(System.Collections.Generic.IDictionary<string, object> result)
        {
            var text = result["error"] as string ?? "";
            if (!result.ContainsKey("lat")) return text;
            return string.Format(CultureInfo.InvariantCulture, "{0}landing {1:0.0000}, {2:0.0000} at UT {3:0}, {4:0.00} km from the target{5}",
                text == "" ? "" : text + "; ", result["lat"], result["lng"], result["landing_ut"], result["error_km"],
                result.ContainsKey("burn_ut")
                    ? string.Format(CultureInfo.InvariantCulture, "; burn at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00} m/s", result["burn_ut"], result["prograde"], result["normal"])
                    : "");
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
