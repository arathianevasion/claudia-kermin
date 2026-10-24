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
    /// start (the first is the default), the default Spending Cap, the compaction trigger and the wait caps; later tickets
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
        /// <summary>The API rejects a compaction trigger below this.</summary>
        public const int MinCompactionTriggerTokens = 50000;

        /// <summary>What each model costs; add models with <c>price.&lt;model&gt; = input, output</c> lines (USD per million tokens).</summary>
        public PriceTable Prices = PriceTable.Default();

        /// <summary>The delta-v margin the Craft Check wants over the Delta-v Map, in percent.</summary>
        public int CraftCheckMarginPercent = 15;

        /// <summary>The longest wait a Building Block sits through in orbit, in orbits; longer waits become a Due Time.</summary>
        public double WaitCapOrbits = 1;

        /// <summary>The longest wait a Building Block sits through on the ground (or with no closed orbit), in seconds.</summary>
        public double WaitCapGroundSeconds = 10800;

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
            + "# Delta-v margin, in percent, the Craft Check wants over the Delta-v Map.\n"
            + "craft_check_margin = 15\n"
            + "# Input tokens at which the conversation is compacted server-side (at least 50000).\n"
            + "compaction_trigger = 150000\n"
            + "# Price table, USD per million tokens (input, output). A model not listed is charged at the highest price here.\n"
            + "# Add a model you want to try at your own risk: price.<model> = input, output\n"
            + "price.claude-sonnet-5-5 = 2, 10\n"
            + "price.claude-opus-5-5 = 4, 20\n"
            + "price.claude-sonnet-5 = 2, 10\n"
            + "price.claude-opus-5 = 5, 25\n"
            + "price.claude-opus-4-8 = 5, 25\n"
            + "price.claude-fable-5-1 = 10, 50\n"
            + "# Longest wait inside a Maneuver Sequence: orbits in orbit, seconds on the ground. Longer waits become a Due Time.\n"
            + "wait_cap_orbits = 1\n"
            + "wait_cap_ground_s = 10800\n";

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
                if (name.StartsWith("price.", StringComparison.Ordinal))
                {
                    var rates = value.Split(',');
                    decimal input, output;
                    if (name.Length > "price.".Length && rates.Length == 2
                        && decimal.TryParse(rates[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out input)
                        && decimal.TryParse(rates[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out output)
                        && input > 0 && output > 0)
                        config.Prices.Set(name.Substring("price.".Length), new ModelPrice(input, output));
                    continue;
                }
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
                    case "craft_check_margin":
                        int margin;
                        if (int.TryParse(value.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out margin) && margin >= 0 && margin <= 200)
                            config.CraftCheckMarginPercent = margin;
                        break;
                    case "compaction_trigger":
                        int trigger;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out trigger) && trigger >= MinCompactionTriggerTokens)
                            config.CompactionTriggerTokens = trigger;
                        break;
                    case "wait_cap_orbits":
                        double orbits;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out orbits) && orbits > 0)
                            config.WaitCapOrbits = orbits;
                        break;
                    case "wait_cap_ground_s":
                        double ground;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ground) && ground > 0)
                            config.WaitCapGroundSeconds = ground;
                        break;
                }
            }
            return config;
        }
    }
}
