// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>
    /// The player config file (outside the save). Holds <c>debug_tools</c>, the models offered at Mission
    /// start (the first is the default), the default Spending Cap and the compaction trigger; later tickets
    /// add the price table and the rest of the spec's settings. Format: <c>name = value</c> lines,
    /// <c>#</c> comments. A missing or unreadable value keeps its default.
    /// </summary>
    public sealed class PlayerConfig
    {
        public const string FileName = "config.cfg";

        public bool DebugTools;

        /// <summary>The models offered in the Mission start form, default first. Never empty.</summary>
        public IList<string> Models = new List<string> { "claude-sonnet-5-5", "claude-opus-5-5" };

        /// <summary>USD.</summary>
        public decimal DefaultSpendingCap = 10m;

        /// <summary>Input tokens at which server-side compaction runs.</summary>
        public int CompactionTriggerTokens = 150000;

        public string DefaultModel
        {
            get { return Models[0]; }
        }

        private const string DefaultFile =
            "# Claudia Kermin player config\n"
            + "debug_tools = false\n"
            + "# Models offered when a Mission starts, default first. Others are at your own risk.\n"
            + "models = claude-sonnet-5-5, claude-opus-5-5\n"
            + "# Spending Cap prefilled in the Mission start form, in USD.\n"
            + "default_spending_cap = 10\n"
            + "# Input tokens at which the conversation is compacted server-side.\n"
            + "compaction_trigger = 150000\n";

        /// <summary>Loads the file, writing it with defaults first if it does not exist.</summary>
        public static PlayerConfig LoadOrCreate(string directory)
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, DefaultFile);
            }

            var config = new PlayerConfig();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var name = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                switch (name)
                {
                    case "debug_tools":
                        config.DebugTools = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "models":
                        var models = value.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
                        if (models.Count > 0) config.Models = models;
                        break;
                    case "default_spending_cap":
                        decimal cap;
                        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > 0)
                            config.DefaultSpendingCap = cap;
                        break;
                    case "compaction_trigger":
                        int trigger;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out trigger) && trigger > 0)
                            config.CompactionTriggerTokens = trigger;
                        break;
                }
            }
            return config;
        }
    }
}
