// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>check_script</c>: the Mission Script Check (compile plus the forbidden list) over a draft script, so Claudia can fix a
    /// refused script before she replies instead of losing a turn to it. It is the same check, over the same bundle (the script
    /// first, then the Core Library, so the lines are the script's), that the reply and the start of a run go through. Passing
    /// it skips nothing: the reply's own Script Check still runs.
    /// </summary>
    internal sealed class CheckScript : IPlanningTool
    {
        /// <summary>Problems listed in a result; the rest are counted, so the result stays within the module's size limit.</summary>
        private const int MaxProblems = 8;
        private const int MaxProblemChars = 300;

        private static readonly Regex ForbiddenLine = new Regex(@"^line (\d+): ", RegexOptions.CultureInvariant);
        private static readonly Regex CompileLine = new Regex(@"line[: ]+(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly IScriptCheck _check;

        public CheckScript(IScriptCheck check)
        {
            _check = check;
        }

        public string Name
        {
            get { return "check_script"; }
        }

        public string Description
        {
            get
            {
                return "Runs the Script Check on a draft Mission Script, the same check your Sequence Plan's script goes through: does it "
                    + "compile, and does it call anything on the forbidden list. Returns ok, and when not ok the problems, each with its "
                    + "line (when the compiler gives one), its kind (compile or forbidden) and its text. Call it on the Mission Script you "
                    + "have written, the one that runs the Building Blocks, before you reply, and fix what it finds, so the reply isn't "
                    + "refused. Passing it does not skip the check on your reply, and it says nothing about whether the script does what "
                    + "the plan needs. Don't call it on a reply with no script, and never to test that tools work.";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"script\":{\"type\":\"string\",\"description\":\"The Mission Script, as it would go in the Sequence Plan's script field.\"}"
                    + "},\"required\":[\"script\"],\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "checking the script"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
        {
            var result = _check.Check(CoreLibrary.Bundle(JsonNode.Str(input["script"])));
            var problems = new List<KeyValuePair<string, string>>();
            if (result.CompileError != null) problems.Add(new KeyValuePair<string, string>("compile", result.CompileError));
            foreach (var found in result.Forbidden) problems.Add(new KeyValuePair<string, string>("forbidden", found));

            var json = new JsonWriter().BeginObject().Field("ok", result.Passed);
            if (!result.Passed)
            {
                json.Name("problems").BeginArray();
                for (var i = 0; i < problems.Count && i < MaxProblems; i++)
                {
                    var kind = problems[i].Key;
                    var text = problems[i].Value.Trim();
                    var line = LineOf(kind, ref text);
                    if (text.Length > MaxProblemChars) text = text.Substring(0, MaxProblemChars - 3) + "...";
                    json.BeginObject();
                    if (line > 0) json.Field("line", line);
                    json.Field("kind", kind).Field("text", text).EndObject();
                }
                json.EndArray();
                if (problems.Count > MaxProblems) json.Field("more_problems", problems.Count - MaxProblems);
            }
            json.EndObject();

            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = result.Passed ? "ok" : problems.Count.ToString(CultureInfo.InvariantCulture) + (problems.Count == 1 ? " problem" : " problems"),
            };
        }

        /// <summary>The script line a problem names, or 0; a forbidden finding's "line N: " prefix moves into the line field.</summary>
        private static long LineOf(string kind, ref string text)
        {
            var match = kind == "forbidden" ? ForbiddenLine.Match(text) : CompileLine.Match(text);
            if (!match.Success) return 0;
            if (kind == "forbidden") text = text.Substring(match.Length);
            long line;
            return long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out line) ? line : 0;
        }
    }
}
