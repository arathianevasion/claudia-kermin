// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Kos;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:HOTTESTPART</c>, for the <c>reentry</c> block: kOS parts have no temperature
    /// suffixes, so the heat is read here. The maths is in <see cref="PartHeat"/>; this side only reads
    /// the parts of the vessel the CPU is on.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddHeatSuffixes()
        {
            AddSuffix("HOTTESTPART", new NoArgsSuffix<ScalarValue>(HottestPart, "The craft's hottest part, in percent of its limit (the hotter of its core and its skin)"));
        }

        private ScalarValue HottestPart()
        {
            var vessel = shared.Vessel;
            double hottest = 0;
            if (vessel != null)
            {
                foreach (var part in vessel.Parts)
                {
                    hottest = Math.Max(hottest, PartHeat.Percent(part.temperature, part.maxTemp, part.skinTemperature, part.skinMaxTemp));
                }
            }
            return ScalarValue.Create(hottest);
        }
    }
}
