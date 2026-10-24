// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// The result of the startup check of the kOS internals the mod uses. While kOS is incompatible
    /// the mod stays out of execution.
    /// </summary>
    public sealed class KosCompatibility
    {
        public static readonly KosCompatibility Compatible = new KosCompatibility(new string[0]);

        /// <summary>The kOS types and members that were looked for and not found.</summary>
        public IList<string> Missing { get; private set; }

        public bool IsCompatible { get { return Missing.Count == 0; } }

        /// <summary>What to tell the player; "" when compatible.</summary>
        public string Message
        {
            get
            {
                if (IsCompatible) return "";
                return "kOS is incompatible: Claudia Kermin can't find kOS internals it needs (" +
                       string.Join(", ", Missing) + "), so Claudia won't run scripts. Install kOS 1.6.";
            }
        }

        private KosCompatibility(IEnumerable<string> missing)
        {
            Missing = missing.ToList().AsReadOnly();
        }

        public static KosCompatibility FromMissing(IEnumerable<string> missing)
        {
            return new KosCompatibility(missing);
        }
    }
}
