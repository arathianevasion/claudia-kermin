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
