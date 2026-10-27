// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The rule for the test-only kOS bridge to Planning Tools (ADR 0006, <c>ADDONS:CLAUDIA:PLANNINGTOOL</c>): it is shut except
    /// while the debug block test runner is running a test, and then open only to that test's kOS processor. Mission Scripts never
    /// reach it: the runner is the only thing that opens it, and the Script Check refuses the call in a Mission Script besides.
    /// Main thread only, like the kOS calls that ask it.
    /// </summary>
    public sealed class ToolTestGate
    {
        /// <summary>
        /// The time a Planning Tool may take on the main thread in one frame, ms: a tenth of a 60 fps frame, so a call never
        /// stutters the game. Provisional, to be set from the timings the tests/tools scenarios log.
        /// </summary>
        public const double FrameBudgetMs = 1.7;

        /// <summary>The one gate the game uses: the block test runner opens it, the kOS addon asks it.</summary>
        public static readonly ToolTestGate Shared = new ToolTestGate();

        private uint? _part;

        public bool IsOpen
        {
            get { return _part.HasValue; }
        }

        /// <summary>A block test has started on the processor in part <paramref name="partId"/>.</summary>
        public void Open(uint partId)
        {
            _part = partId;
        }

        /// <summary>The block test has ended, however it ended.</summary>
        public void Close()
        {
            _part = null;
        }

        /// <summary>Why the processor in <paramref name="partId"/> may not call a Planning Tool now; null when it may.</summary>
        public string Refusal(uint partId)
        {
            if (!_part.HasValue)
                return "Planning Tools are reachable from kOS only while a block test is running; a Mission Script calls Building Blocks";
            if (_part.Value != partId) return "Planning Tools are open only to the processor running the block test";
            return null;
        }

        /// <summary>A kOS lexicon's plain values (as <c>KosValues.ToDictionary</c> gives them) as the tool's JSON input.</summary>
        public static string InputJson(IDictionary<string, object> input)
        {
            var json = new JsonWriter();
            Write(json, input);
            return json.ToString();
        }

        private static void Write(JsonWriter json, object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                json.BeginObject();
                foreach (var entry in dictionary)
                {
                    json.Name(entry.Key);
                    Write(json, entry.Value);
                }
                json.EndObject();
                return;
            }
            if (value is string) json.String((string)value);
            else if (value is double) json.Number((double)value);
            else if (value is bool) json.Bool((bool)value);
            else if (value is IEnumerable)
            {
                json.BeginArray();
                foreach (var item in (IEnumerable)value) Write(json, item);
                json.EndArray();
            }
            else if (value == null) json.Null();
            else json.String(System.Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A tool's JSON result as plain values for kOS: object → dictionary, array → list, number → double, string, bool; a JSON
        /// null becomes "", since a kOS lexicon can't hold nothing.
        /// </summary>
        public static IDictionary<string, object> ResultValues(string json)
        {
            return Plain(JsonReader.Parse(json)) as IDictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static object Plain(JsonNode node)
        {
            switch (node.Kind)
            {
                case JsonKind.Object:
                    var dictionary = new Dictionary<string, object>();
                    foreach (var member in node.Members) dictionary[member.Key] = Plain(member.Value);
                    return dictionary;
                case JsonKind.Array:
                    var list = new List<object>();
                    foreach (var item in node.Items) list.Add(Plain(item));
                    return list;
                case JsonKind.Number: return JsonNode.Double(node) ?? 0;
                case JsonKind.String: return node.StringValue;
                case JsonKind.Bool: return node.BoolValue;
                default: return "";
            }
        }

        /// <summary>The KSP.log line timing one call against <see cref="FrameBudgetMs"/>.</summary>
        public static string TimingLine(string tool, double snapshotMs, double callMs)
        {
            return "tool test timing: " + tool + " took " + callMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms (snapshot "
                + snapshotMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms) against a frame budget of "
                + FrameBudgetMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms: "
                + (callMs <= FrameBudgetMs ? "within" : "OVER") + " budget";
        }
    }
}
