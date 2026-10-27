// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// A Building Block's trace line (<c>ADDONS:CLAUDIA:BLOCKTRACE</c>, #365): a lexicon of its state, written to KSP.log
    /// as it comes, the way the warp trace is, so a craft destroyed mid-block can be diagnosed afterwards. land traces its
    /// descent every 2 s and at each phase change.
    /// </summary>
    public static class BlockTrace
    {
        /// <summary>"land trace at UT 428272.4: phase braking, h 450, v_h 201.5", the values in the lexicon's order.</summary>
        public static string Line(string block, double ut, IDictionary<string, object> state)
        {
            var values = (state ?? new Dictionary<string, object>()).Select(pair => pair.Key + " " + Value(pair.Value));
            return (string.IsNullOrEmpty(block) ? "?" : block) + " trace at UT " + ut.ToString("0.0", CultureInfo.InvariantCulture) + ": "
                + string.Join(", ", values.ToArray());
        }

        private static string Value(object value)
        {
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is double || value is float || value is int)
                return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);
            return value == null ? "" : value.ToString();
        }
    }
}
