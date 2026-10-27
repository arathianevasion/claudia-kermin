// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// One RCS switch a block reported through <c>ADDONS:CLAUDIA:RCSSWITCH</c> (RCS Use, #393): on or off, the reason, and the
    /// figures the block had to hand. Keys of the lexicon: <c>on</c> (boolean) and <c>reason</c> (one of <see cref="Reasons"/>),
    /// then each optional: <c>error_deg</c> (steering error), <c>rate_dps</c> (turn rate, deg/s), <c>wheels_s</c> and <c>left_s</c>
    /// (the wheels' turn time against the seconds left to the deadline, given together), <c>monoprop</c> and <c>monoprop_max</c>
    /// (units, given together).
    /// </summary>
    public sealed class RcsSwitch
    {
        public static readonly string[] Reasons =
        {
            "turning", "falling behind", "big turn", "no wheel torque", "translating", "aligned", "steering released", "block end", "no monoprop",
        };

        public bool On;
        public string Reason = "";
        public double? ErrorDeg, RateDps, WheelsS, LeftS, Monoprop, MonopropMax;

        /// <summary>Reads the lexicon; null with <paramref name="problem"/> set when it can't be a switch.</summary>
        public static RcsSwitch Parse(IDictionary<string, object> values, out string problem)
        {
            problem = null;
            object on, reason;
            if (values == null) { problem = "no lexicon"; return null; }
            if (!values.TryGetValue("on", out on) || !(on is bool)) { problem = "'on' must be true or false"; return null; }
            if (!values.TryGetValue("reason", out reason) || Array.IndexOf(Reasons, reason as string) < 0)
            {
                problem = "'reason' must be one of: " + string.Join(", ", Reasons);
                return null;
            }
            var found = new RcsSwitch { On = (bool)on, Reason = (string)reason };
            foreach (var key in new[] { "error_deg", "rate_dps", "wheels_s", "left_s", "monoprop", "monoprop_max" })
            {
                object raw;
                if (!values.TryGetValue(key, out raw) || raw == null) continue;
                double number;
                if (raw is string || raw is bool || !TryNumber(raw, out number)) { problem = "'" + key + "' must be a number"; return null; }
                switch (key)
                {
                    case "error_deg": found.ErrorDeg = number; break;
                    case "rate_dps": found.RateDps = number; break;
                    case "wheels_s": found.WheelsS = number; break;
                    case "left_s": found.LeftS = number; break;
                    case "monoprop": found.Monoprop = number; break;
                    default: found.MonopropMax = number; break;
                }
            }
            return found;
        }

        private static bool TryNumber(object raw, out double number)
        {
            try { number = Convert.ToDouble(raw, CultureInfo.InvariantCulture); return !double.IsNaN(number) && !double.IsInfinity(number); }
            catch (Exception) { number = 0; return false; }
        }

        /// <summary>"RCS on: turning (error 34.2°, rate 0.1°/s, wheels 85 s vs 60 s left, monoprop 30/40)"; the figures given, in that order.</summary>
        public string Line()
        {
            var figures = new List<string>();
            if (ErrorDeg.HasValue) figures.Add("error " + Format(ErrorDeg.Value, "0.0") + "°");
            if (RateDps.HasValue) figures.Add("rate " + Format(RateDps.Value, "0.0") + "°/s");
            if (WheelsS.HasValue && LeftS.HasValue) figures.Add("wheels " + Format(WheelsS.Value, "0") + " s vs " + Format(LeftS.Value, "0") + " s left");
            if (Monoprop.HasValue && MonopropMax.HasValue) figures.Add("monoprop " + Format(Monoprop.Value, "0") + "/" + Format(MonopropMax.Value, "0"));
            return "RCS " + (On ? "on" : "off") + ": " + Reason + (figures.Count > 0 ? " (" + string.Join(", ", figures.ToArray()) + ")" : "");
        }

        private static string Format(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The run's RCS switches: how often RCS went on and for how long, in all and per Building Block.</summary>
    public sealed class RcsUseTally
    {
        private sealed class Entry { public string Block; public int Times; }

        private readonly List<Entry> _blocks = new List<Entry>();
        private int _times;
        private double _seconds;
        private double? _onSince;

        public int TimesOn { get { return _times; } }
        public double SecondsOn { get { return _seconds; } }

        /// <summary>The block RCS went on in most often (the first, on a tie); "" when none, or when it went on outside any block.</summary>
        public string MostBlock { get; private set; }
        public int MostBlockTimes { get; private set; }

        public RcsUseTally() { MostBlock = ""; }

        public void Clear()
        {
            _blocks.Clear();
            _times = 0;
            _seconds = 0;
            _onSince = null;
            MostBlock = "";
            MostBlockTimes = 0;
        }

        /// <summary>A switch at <paramref name="ut"/> while <paramref name="block"/> flew; on while already on, or off while off, is not counted.</summary>
        public void Switched(double ut, string block, bool on)
        {
            if (on)
            {
                if (_onSince.HasValue) return;
                _onSince = ut;
                _times++;
                var entry = _blocks.Find(b => b.Block == (block ?? ""));
                if (entry == null) _blocks.Add(entry = new Entry { Block = block ?? "" });
                entry.Times++;
                if (entry.Times > MostBlockTimes) { MostBlock = entry.Block; MostBlockTimes = entry.Times; }
            }
            else Close(ut);
        }

        /// <summary>The tally as JSON for the save file (#404), an RCS-on stretch still open included; "" when nothing was ever switched on.</summary>
        public string ToJson()
        {
            if (_times == 0 && !_onSince.HasValue) return "";
            var json = new JsonWriter().BeginObject().Field("times", _times).Field("seconds", _seconds).Field("on_since", _onSince).Name("blocks").BeginArray();
            foreach (var b in _blocks) json.BeginObject().Field("block", b.Block).Field("times", b.Times).EndObject();
            return json.EndArray().EndObject().ToString();
        }

        /// <summary>Puts back what <see cref="ToJson"/> wrote; an empty or unreadable text leaves the tally cleared.</summary>
        public void Restore(string text)
        {
            Clear();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var root = JsonReader.Parse(text);
                _times = (int)(JsonNode.Long(root["times"]) ?? 0);
                _seconds = JsonNode.Double(root["seconds"]) ?? 0;
                _onSince = JsonNode.Double(root["on_since"]);
                var items = root["blocks"];
                if (items != null)
                    foreach (var node in items.Items.Where(n => n.Kind == JsonKind.Object))
                    {
                        var entry = new Entry { Block = JsonNode.Str(node["block"]) ?? "", Times = (int)(JsonNode.Long(node["times"]) ?? 0) };
                        if (entry.Times <= 0) continue;
                        _blocks.Add(entry);
                        // The first block to reach the highest count, as Switched keeps it.
                        if (entry.Times > MostBlockTimes) { MostBlock = entry.Block; MostBlockTimes = entry.Times; }
                    }
            }
            catch (JsonException)
            {
                Clear();
            }
        }

        /// <summary>The run ended (MissionControl.RunEnded calls this before the Debrief is built): a switch still on counts its seconds up to <paramref name="ut"/>.</summary>
        public void Close(double ut)
        {
            if (!_onSince.HasValue) return;
            _seconds += Math.Max(0, ut - _onSince.Value);
            _onSince = null;
        }
    }
}
