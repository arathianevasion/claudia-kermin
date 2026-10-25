// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// The heat maths behind <c>ADDONS:CLAUDIA:HOTTESTPART</c>, kept free of KSP: the addon reads each
    /// part's temperatures (kOS has no part temperature suffixes) and this turns them into a percent of
    /// the part's limit. Temperatures are in kelvin, as the game keeps them.
    /// </summary>
    public static class PartHeat
    {
        /// <summary>
        /// A part's heat as a percent of its limit: the hotter of its core (<paramref name="temp"/> against
        /// <paramref name="maxTemp"/>) and its skin (<paramref name="skin"/> against <paramref name="skinMax"/>).
        /// A limit of zero or less (or not a number) counts for nothing; a part with neither scores 0.
        /// </summary>
        public static double Percent(double temp, double maxTemp, double skin, double skinMax)
        {
            return Math.Max(Share(temp, maxTemp), Share(skin, skinMax));
        }

        private static double Share(double value, double limit)
        {
            if (double.IsNaN(value) || double.IsNaN(limit) || limit <= 0) return 0;
            return 100 * value / limit;
        }
    }
}
