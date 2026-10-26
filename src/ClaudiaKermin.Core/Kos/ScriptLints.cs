// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// Warn-only lints in the Mission Script Check for flight logic that re-builds a Building Block or
    /// Planning Tool: a hand-written landing-site search, a node-and-nextpatch nudge loop, and a staging
    /// loop that watches fuel. They never refuse a script; their text goes into the Custom Script badge's
    /// reason. Each reads loops (<c>for</c>, <c>until</c>, <c>from</c>, <c>when</c>) in the script's code,
    /// ignoring comments and strings.
    /// </summary>
    public static class ScriptLints
    {
        private static readonly Regex LoopKeyword = new Regex(@"\b(for|until|from|when)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SiteSearch = new Regex(@"\blatlng\s*\(|\bgeopositionlatlng\b|\bterrainheight\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NextPatch = new Regex(@":\s*nextpatch\b|\bnextpatch\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NodeUse = new Regex(@"\bnode\s*\(|\bnextnode\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Staging = new Regex(@"\bstage\s*\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FuelWatch = new Regex(
            @"\bstage\s*:\s*(liquidfuel|oxidizer|solidfuel|resources|deltav)\b|\b(liquidfuel|oxidizer|solidfuel|availablethrust|maxthrust)\b|:\s*amount\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>One warning per lint that fires, each naming the first line of the loop it found; empty for a clean script.</summary>
        public static IList<string> Warnings(string script)
        {
            var code = ScriptProvenance.Strip(script ?? "");
            var warnings = new List<string>();
            var siteLine = 0;
            var nudgeLine = 0;
            var fuelLine = 0;
            foreach (var loop in Loops(code))
            {
                var body = code.Substring(loop.Start, loop.End - loop.Start);
                var line = 1 + code.Take(loop.Start).Count(c => c == '\n');
                if (siteLine == 0 && SiteSearch.IsMatch(body)) siteLine = line;
                if (nudgeLine == 0 && NextPatch.IsMatch(body) && NodeUse.IsMatch(body)) nudgeLine = line;
                if (fuelLine == 0 && Staging.IsMatch(body) && FuelWatch.IsMatch(body)) fuelLine = line;
            }
            if (siteLine > 0)
                warnings.Add("line " + siteLine + ": a hand-written landing-site search loop; a Building Block or Planning Tool may already search for a site.");
            if (nudgeLine > 0)
                warnings.Add("line " + nudgeLine + ": a loop that nudges a maneuver node while reading the next patch; a Building Block may already shape that orbit.");
            if (fuelLine > 0)
                warnings.Add("line " + fuelLine + ": a staging loop that watches fuel; a Building Block's staging option may already stage when tanks run dry.");
            return warnings;
        }

        private struct Span
        {
            public int Start;
            public int End;
        }

        /// <summary>Each loop's text from its keyword to the end of its last braced block.</summary>
        private static IEnumerable<Span> Loops(string code)
        {
            foreach (Match m in LoopKeyword.Matches(code))
            {
                var before = code.Substring(0, m.Index).TrimEnd();
                if (before.EndsWith(":")) continue; // a suffix
                if (m.Value.Equals("until", StringComparison.OrdinalIgnoreCase)
                    && (before.EndsWith("wait", StringComparison.OrdinalIgnoreCase) || before.EndsWith("}"))) continue; // "wait until x." has no block; "from {..} until" is read from "from"
                var end = m.Index + m.Length;
                var groups = m.Value.Equals("from", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
                var found = false;
                for (var g = 0; g < groups; g++)
                {
                    var open = code.IndexOf('{', end);
                    if (open < 0) break;
                    var close = Close(code, open);
                    if (close < 0) break;
                    end = close + 1;
                    found = true;
                }
                if (found) yield return new Span { Start = m.Index, End = end };
            }
        }

        private static int Close(string code, int open)
        {
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }
    }
}
