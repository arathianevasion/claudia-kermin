// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>
    /// The effort each reply kind runs at, keyed by kind (a Mission turn's Purpose, plus the Craft Check and the Margin recheck).
    /// Built-in defaults, overridden by <c>effort.&lt;kind&gt; = low|medium|high</c> lines in the player config file. Effort is
    /// never a Mission Setting and never shown to the player. A value that isn't an effort, or a kind that doesn't exist,
    /// keeps the default and is noted in <see cref="Problems"/> for the Mission's event log.
    /// </summary>
    public sealed class EffortTable
    {
        public const string Prefix = "effort.";

        private static readonly string[] Levels = { "low", "medium", "high" };

        private static readonly KeyValuePair<string, string>[] DefaultEfforts =
        {
            new KeyValuePair<string, string>("mission_plan", "high"),
            new KeyValuePair<string, string>("sequence_plan", "high"),
            new KeyValuePair<string, string>("debrief", "high"),
            new KeyValuePair<string, string>("reply", "high"),
            new KeyValuePair<string, string>("change", "high"),
            new KeyValuePair<string, string>("settings", "high"),
            new KeyValuePair<string, string>("craft_check", "high"),
            new KeyValuePair<string, string>("margin_recheck", "high"),
            new KeyValuePair<string, string>("question", "low"),
            new KeyValuePair<string, string>("alert", "low"),
        };

        private readonly Dictionary<string, string> _efforts = new Dictionary<string, string>();
        private readonly List<string> _problems = new List<string>();

        public EffortTable()
        {
            foreach (var pair in DefaultEfforts) _efforts[pair.Key] = pair.Value;
        }

        /// <summary>What was wrong with the entries read from the config file, one line each; empty when all were fine.</summary>
        public IList<string> Problems
        {
            get { return _problems; }
        }

        /// <summary>The effort for a kind (<c>mission_plan</c>, <c>question</c>, ...).</summary>
        public string For(string kind)
        {
            string effort;
            if (!_efforts.TryGetValue(kind, out effort)) throw new ArgumentException("No effort kind " + kind, "kind");
            return effort;
        }

        /// <summary>Reads one <c>effort.&lt;kind&gt;</c> entry from the config file. <paramref name="name"/> includes the prefix.</summary>
        public void Read(string name, string value)
        {
            var kind = name.Substring(Prefix.Length);
            if (!_efforts.ContainsKey(kind))
            {
                _problems.Add("Config entry " + name + " is not a reply kind; ignored.");
                return;
            }
            var level = value.Trim().ToLowerInvariant();
            if (Array.IndexOf(Levels, level) < 0)
            {
                _problems.Add("Config entry " + name + " = " + value + " is not low, medium or high; using " + _efforts[kind] + ".");
                return;
            }
            _efforts[kind] = level;
        }
    }
}
