// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>What a turn has used so far, over every request it took (a Second look, a cut-off retry, tool steps); the per-plan log's figures (#286).</summary>
    internal sealed class TurnStats
    {
        public readonly SortedDictionary<string, int> Tools = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public int Requests;
        public decimal Cost;
        public long OutputTokens;
        public long ThinkingTokens;
        public double Seconds;
        private Stopwatch _timer;

        public void Sent()
        {
            _timer = Stopwatch.StartNew();
        }

        /// <summary>A request ended: its time, and (for a reply that came) its output and the thinking within it. <paramref name="cost"/> is what it added to the Mission's spend.</summary>
        public void Ended(decimal cost, AssistantMessage message)
        {
            Requests++;
            Cost += cost;
            if (_timer != null) Seconds += _timer.Elapsed.TotalSeconds;
            _timer = null;
            if (message == null) return;
            OutputTokens += message.Usage.OutputTokens;
            ThinkingTokens += ThinkingOf(message);
        }

        /// <summary>
        /// The API does not split thinking from the rest of the output, so it is the output less an estimate of the visible reply
        /// (its text and tool inputs, three characters to a token, the estimate the Spending Cap uses), never below zero.
        /// </summary>
        private static long ThinkingOf(AssistantMessage message)
        {
            long chars = 0;
            foreach (var block in message.Content)
            {
                var text = block as TextBlock;
                if (text != null) chars += text.Text.Length;
                var tool = block as ToolUseBlock;
                if (tool != null) chars += tool.InputJson.Length;
            }
            return Math.Max(0, message.Usage.OutputTokens - (chars + 2) / 3);
        }

        public void Called(string tool)
        {
            int n;
            Tools.TryGetValue(tool, out n);
            Tools[tool] = n + 1;
        }
    }

    /// <summary>
    /// <c>plans.jsonl</c> in the Mission folder (#286): one JSON line per Sequence Plan offered, with how it was made. When that
    /// sequence's Debrief commits, its line is rewritten (atomically, like the transcript) with a <c>debrief</c> object: one
    /// line per plan, never two to join. Best effort, like the event log. The cost baseline tool reads it.
    /// </summary>
    internal static class PlanLog
    {
        public const string FileName = "plans.jsonl";

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static void AppendPlan(string folder, string purpose, SequencePlan plan, TurnStats stats, string effort,
            string secondLook, bool cutOffRetry, bool scriptRewrite, IList<string> lints)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            var json = new JsonWriter().BeginObject()
                .Field("event", "plan")
                .Field("plan", Lines(path).Count + 1)
                .Field("name", plan.Name)
                .Field("purpose", purpose)
                .Field("manual", plan.Manual);
            json.Name("tools").BeginObject();
            foreach (var tool in stats.Tools) json.Field(tool.Key, tool.Value);
            json.EndObject();
            json.Field("tool_calls", stats.Tools.Values.Sum());
            json.Name("blocks").BeginObject();
            foreach (var block in BlocksCalled(plan.Script)) json.Field(block.Key, block.Value);
            json.EndObject();
            json.Field("custom_script", plan.CustomScript)
                .Field("custom_lines", plan.CustomScript ? CodeLines(plan.Script) : 0);
            json.Name("lints").BeginArray();
            foreach (var lint in lints) json.String(lint);
            json.EndArray();
            Turn(json, effort, stats, secondLook, cutOffRetry);
            json.Field("script_rewrite", scriptRewrite)
                .Field("at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .EndObject();
            File.AppendAllText(path, json + "\n", Utf8);
        }

        /// <summary>The Debrief committed: the latest plan's line gains <c>debrief</c>; a Debrief with no plan line before it (or a plan already debriefed) writes nothing.</summary>
        public static void SetDebrief(string folder, string outcome, TurnStats stats, string effort, string secondLook, bool cutOffRetry)
        {
            var path = Path.Combine(folder, FileName);
            var lines = Lines(path);
            if (lines.Count == 0 || lines[lines.Count - 1].Contains(",\"debrief\":")) return;
            var json = new JsonWriter().BeginObject().Field("outcome", outcome);
            Turn(json, effort, stats, secondLook, cutOffRetry);
            json.EndObject();
            var last = lines[lines.Count - 1];
            // The plan line's last field is "at": the debrief goes in front of it, so the order of the line's fields is fixed.
            var at = last.LastIndexOf(",\"at\":", StringComparison.Ordinal);
            if (at < 0) return;
            lines[lines.Count - 1] = last.Substring(0, at) + ",\"debrief\":" + json + last.Substring(at);
            MissionFolder.ReplaceAtomically(path, string.Join("\n", lines.ToArray()) + "\n");
        }

        private static void Turn(JsonWriter json, string effort, TurnStats stats, string secondLook, bool cutOffRetry)
        {
            json.Field("effort", effort ?? "")
                .Field("requests", stats.Requests)
                .Field("output_tokens", stats.OutputTokens)
                .Field("thinking_tokens", stats.ThinkingTokens)
                .Field("reply_seconds", Math.Round(stats.Seconds, 2))
                .Name("cost").Raw(stats.Cost.ToString(CultureInfo.InvariantCulture));
            json.Name("second_look");
            if (string.IsNullOrEmpty(secondLook)) json.Null();
            else json.String(secondLook);
            json.Field("cut_off_retry", cutOffRetry);
        }

        private static List<string> Lines(string path)
        {
            return File.Exists(path) ? File.ReadAllLines(path, Utf8).Where(l => l.Length > 0).ToList() : new List<string>();
        }

        /// <summary>Lines of the script that hold code: not blank, not a comment.</summary>
        internal static int CodeLines(string script)
        {
            return ScriptProvenance.Strip(script ?? "").Split('\n').Count(l => l.Trim().Length > 0);
        }

        /// <summary>The Building Blocks a script calls, with how many times each.</summary>
        internal static SortedDictionary<string, int> BlocksCalled(string script)
        {
            var code = ScriptProvenance.Strip(script ?? "");
            var called = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var block in CoreLibrary.Blocks)
            {
                var n = Regex.Matches(code, @"(?<![:\w])" + Regex.Escape(block.Name) + @"\s*\(", RegexOptions.IgnoreCase)
                    .Cast<Match>().Count(m => !Regex.IsMatch(code.Substring(0, m.Index), @"\bfunction\s+$", RegexOptions.IgnoreCase));
                if (n > 0) called[block.Name] = n;
            }
            return called;
        }
    }
}
