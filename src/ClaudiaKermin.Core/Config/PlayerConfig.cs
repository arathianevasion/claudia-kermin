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
    /// start (the first is the default), the default Spending Cap, the compaction trigger and the runaway multiplier; later tickets
    /// add the price table and the rest of the spec's settings. Format: <c>name = value</c> lines,
    /// <c>#</c> comments. A missing or unreadable value keeps its default; a name it doesn't know is ignored, such as
    /// the <c>wait_cap_orbits</c> and <c>wait_cap_ground_s</c> an older file may still hold (ADR 0004). The Mission defaults are written back by
    /// <see cref="SaveDefaults"/> (the Settings window, in the Space Center).
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

        /// <summary>Prefilled in the Mission start form: prefer to land in daylight.</summary>
        public bool DefaultDaylightLanding = true;

        /// <summary>Prefilled in the Mission start form: how Claudia trades time against delta-v.</summary>
        public Missions.TimeVersusDeltaV DefaultTimeVersusDeltaV = Missions.TimeVersusDeltaV.Balanced;

        /// <summary>Prefilled in the Mission start form: a remote Mission (Claudia at Mission Control); false is crewed (she flies aboard).</summary>
        public bool DefaultRemote;

        /// <summary>How much more delta-v a late Approval may cost (the next window against the planned one) before Claudia re-plans, in percent.</summary>
        public int LateApprovalToleranceDvPercent = 10;

        /// <summary>How long attitude, translation or throttle input must be held, in seconds, before it starts Manual Control.</summary>
        public double ManualHoldSeconds = 1;

        /// <summary>
        /// How long, in seconds of game time, the link may be down in Manual Control (with no other crew aboard) before
        /// Claudia takes the craft back; also how long a link must have been steady before Manual Control is offered.
        /// </summary>
        public double ManualLinkGraceSeconds = 5;

        /// <summary>How far the craft may drift from the pending plan's baseline, in percent, before Approve turns into Re-plan.</summary>
        public int ReplanTolerancePercent = 15;

        /// <summary>The runaway hard stop, as a multiple of a Sequence Plan's planned duration (waits included); 3 is 300%.</summary>
        public double RunawayMultiplier = 3;

        // ---- Telemetry Check defaults: they fill the gaps Claudia leaves in a Sequence Plan ----

        /// <summary>Delta-v used over this percent of the plan's expected delta-v crosses the default check.</summary>
        public int CheckDeltaVUsedPercent = 120;
        /// <summary>Time elapsed over this percent of the plan's expected duration crosses the default check.</summary>
        public int CheckDurationPercent = 150;
        /// <summary>Electric charge under this percent of capacity crosses the default check.</summary>
        public int CheckElectricChargePercent = 10;
        /// <summary>The delta-v left under what the Mission Plan still needs, less this percent, crosses the default check.</summary>
        public int CheckFuelMarginPercent = 10;

        public string DefaultModel
        {
            get { return Models[0]; }
        }

        private const string RemoteComment =
            "# Mission type prefilled in the start form: false is crewed (Claudia flies aboard), true is remote (Claudia at Mission Control).";

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
            + "# Mission Settings prefilled in the start form: daylight landing (true/false) and time versus delta-v (Fastest, Balanced, MostEfficient).\n"
            + "daylight_landing = true\n"
            + "time_vs_dv = Balanced\n"
            + RemoteComment + "\n"
            + "default_remote = false\n"
            + "# How much more delta-v, in percent, a late Approval may cost before Claudia re-plans instead of taking the next window.\n"
            + "late_approval_dv_tolerance = 10\n"
            + "# Seconds attitude, translation or throttle input must be held to start Manual Control.\n"
            + "manual_hold_s = 1\n"
            + "# Seconds of game time the link may be down in Manual Control, with no other crew aboard, before Claudia takes the craft back.\n"
            + "# Manual Control is offered only once the link has been steady this long.\n"
            + "manual_link_grace_s = 5\n"
            + "# How far, in percent, the craft may change from a pending plan before Approve becomes Re-plan.\n"
            + "replan_tolerance = 15\n"
            + "# A script still running at this percentage of its planned duration (waits included) is stopped and made safe.\n"
            + "runaway_multiplier = 300%\n"
            + "# Telemetry Check defaults, for what a Sequence Plan leaves unchecked: delta-v used over this percent of the plan,\n"
            + "# time elapsed over this percent of the plan, electric charge under this percent, and delta-v left under what the\n"
            + "# Mission Plan still needs less this percent.\n"
            + "check_dv_used = 120%\n"
            + "check_duration = 150%\n"
            + "check_ec = 10%\n"
            + "check_fuel_margin = 10%\n";

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
                    case "manual_hold_s":
                        double hold;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out hold) && hold > 0)
                            config.ManualHoldSeconds = hold;
                        break;
                    case "manual_link_grace_s":
                        double grace;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out grace) && grace >= 0)
                            config.ManualLinkGraceSeconds = grace;
                        break;
                    case "replan_tolerance":
                        int tolerance;
                        if (int.TryParse(value.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out tolerance) && tolerance >= 0 && tolerance <= 200)
                            config.ReplanTolerancePercent = tolerance;
                        break;
                    case "daylight_landing":
                        config.DefaultDaylightLanding = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "time_vs_dv":
                        Missions.TimeVersusDeltaV priority;
                        if (Enum.TryParse(value, true, out priority)) config.DefaultTimeVersusDeltaV = priority;
                        break;
                    case "default_remote":
                        config.DefaultRemote = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "late_approval_dv_tolerance":
                        int lateTolerance;
                        if (int.TryParse(value.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out lateTolerance) && lateTolerance >= 0 && lateTolerance <= 200)
                            config.LateApprovalToleranceDvPercent = lateTolerance;
                        break;
                    case "check_dv_used":
                        config.CheckDeltaVUsedPercent = Percent(value, 100, 1000, config.CheckDeltaVUsedPercent);
                        break;
                    case "check_duration":
                        config.CheckDurationPercent = Percent(value, 100, 1000, config.CheckDurationPercent);
                        break;
                    case "check_ec":
                        config.CheckElectricChargePercent = Percent(value, 0, 100, config.CheckElectricChargePercent);
                        break;
                    case "check_fuel_margin":
                        config.CheckFuelMarginPercent = Percent(value, 0, 100, config.CheckFuelMarginPercent);
                        break;
                    case "runaway_multiplier":
                        // "300%" or "3". Below 100% would stop a script before its own plan says it ends.
                        double multiplier;
                        if (double.TryParse(value.TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier))
                        {
                            if (value.EndsWith("%", StringComparison.Ordinal)) multiplier /= 100;
                            if (multiplier >= 1 && multiplier <= 100) config.RunawayMultiplier = multiplier;
                        }
                        break;
                }
            }
            return config;
        }

        /// <summary>A whole percent ("120%" or "120") from <paramref name="min"/> to <paramref name="max"/>; otherwise <paramref name="current"/>.</summary>
        private static int Percent(string value, int min, int max, int current)
        {
            int percent;
            return int.TryParse(value.TrimEnd('%').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out percent) && percent >= min && percent <= max
                ? percent : current;
        }

        /// <summary>Makes an offered model the default by moving it to the front of <see cref="Models"/>; a model not offered is ignored.</summary>
        public void SetDefaultModel(string model)
        {
            if (!Models.Contains(model)) return;
            var models = Models.Where(m => m != model).ToList();
            models.Insert(0, model);
            Models = models;
        }

        /// <summary>
        /// Writes the Mission defaults (the models in order, the Spending Cap, the Mission Settings and the Mission type)
        /// into the file, rewriting only their lines: every other line, comment and the order stay as they are. A key the
        /// file lacks is added at the end. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
        /// when the file can't be written.
        /// </summary>
        public void SaveDefaults(string directory)
        {
            var path = Path.Combine(directory, FileName);
            var text = File.Exists(path) ? File.ReadAllText(path) : DefaultFile;
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, WithDefaults(text));
        }

        private string WithDefaults(string text)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("models", string.Join(", ", Models)),
                new KeyValuePair<string, string>("default_spending_cap", DefaultSpendingCap.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("daylight_landing", DefaultDaylightLanding ? "true" : "false"),
                new KeyValuePair<string, string>("time_vs_dv", DefaultTimeVersusDeltaV.ToString()),
                new KeyValuePair<string, string>("default_remote", DefaultRemote ? "true" : "false"),
            };
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
            var body = text.Replace("\r\n", "\n");
            if (endsWithNewline) body = body.Substring(0, body.Length - 1);
            var lines = body.Length == 0 ? new List<string>() : body.Split('\n').ToList();

            var written = new HashSet<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                var name = trimmed.Substring(0, eq).Trim();
                foreach (var pair in values)
                {
                    if (pair.Key != name) continue;
                    // The indent is kept; a repeated key is rewritten everywhere, since the last one read wins.
                    lines[i] = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length) + name + " = " + pair.Value;
                    written.Add(name);
                }
            }
            var added = false;
            foreach (var pair in values)
            {
                if (written.Contains(pair.Key)) continue;
                if (pair.Key == "default_remote") lines.Add(RemoteComment);
                lines.Add(pair.Key + " = " + pair.Value);
                added = true;
            }
            return string.Join(newline, lines) + (endsWithNewline || added ? newline : "");
        }
    }
}
