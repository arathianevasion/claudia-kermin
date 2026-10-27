// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// kOS refuses a script that declares a name hiding one of its built-ins, but only inside the game
    /// (the bindings are registered there), so the Script Check misses it. The list in
    /// <c>Fixtures/kos-builtins-documented.txt</c> comes from the kOS documentation's bound-variable
    /// and function lists, not from the assembly.
    /// </summary>
    internal static class KosBuiltinNames
    {
        private static readonly Lazy<HashSet<string>> Loaded = new Lazy<HashSet<string>>(() =>
            new HashSet<string>(
                File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kos-builtins-documented.txt"))
                    .Select(l => l.Trim().ToLowerInvariant())
                    .Where(l => l.Length > 0 && !l.StartsWith("//"))));

        public static bool Contains(string name) { return Loaded.Value.Contains(name.ToLowerInvariant()); }

        /// <summary>Every declaration in <paramref name="source"/> that names a built-in, as "where line N: text".</summary>
        public static List<string> Clashes(string where, string source)
        {
            var bad = new List<string>();
            var lines = source.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripCommentAndStrings(lines[i]);
                foreach (var name in DeclaredNames(code))
                    if (Contains(name)) bad.Add(where + " line " + (i + 1) + ": declares '" + name + "': " + lines[i].Trim());
            }
            return bad;
        }

        /// <summary>
        /// Every function declared in <paramref name="source"/>, lowercased. A local, parameter or <c>set</c> target of the
        /// same name in another file hides the function in the bundle (kOS resolves the name to the function delegate).
        /// </summary>
        public static IEnumerable<string> FunctionNames(string source)
        {
            foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
                foreach (Match m in Regex.Matches(StripCommentAndStrings(line), @"\bfunction\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase))
                    yield return m.Groups[1].Value.ToLowerInvariant();
        }

        /// <summary>Every variable (not function) a line declares or sets that is in <paramref name="functions"/>, as "where line N: text".</summary>
        public static List<string> ClashesWithFunctions(string where, string source, ISet<string> functions)
        {
            var bad = new List<string>();
            var lines = source.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripCommentAndStrings(lines[i]);
                var declaredFunctions = new HashSet<string>(
                    Regex.Matches(code, @"\bfunction\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()));
                var names = DeclaredNames(code).Concat(
                    Regex.Matches(code, @"(?:^|[.{}]|\s)set\s+([A-Za-z_]\w*)\s+to\b", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value));
                foreach (var name in names)
                {
                    var lower = name.ToLowerInvariant();
                    if (functions.Contains(lower) && !declaredFunctions.Contains(lower))
                        bad.Add(where + " line " + (i + 1) + ": '" + name + "' hides the Core Library function of that name: " + lines[i].Trim());
                }
            }
            return bad;
        }

        /// <summary>Clashes in the backtick-quoted examples of the docs.</summary>
        public static List<string> ClashesInExamples(string docs)
        {
            var bad = new List<string>();
            foreach (Match span in Regex.Matches(docs, "`[^`]*`", RegexOptions.Singleline))
                bad.AddRange(Clashes("doc example", span.Value.Trim('`')));
            return bad;
        }

        private static string StripCommentAndStrings(string line)
        {
            var sb = new StringBuilder();
            var inString = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"') { inString = !inString; sb.Append('"'); continue; }
                if (inString) continue;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static IEnumerable<string> DeclaredNames(string code)
        {
            foreach (Match m in Regex.Matches(code, @"\b(?:local|global)\s+(?:function\s+)?([A-Za-z_]\w*)", RegexOptions.IgnoreCase))
                if (!m.Groups[1].Value.Equals("function", StringComparison.OrdinalIgnoreCase)) yield return m.Groups[1].Value;
            foreach (Match m in Regex.Matches(code, @"\bdeclare\s+(?:(?:local|global|function)\s+)*([A-Za-z_]\w*)", RegexOptions.IgnoreCase))
                if (!m.Groups[1].Value.Equals("parameter", StringComparison.OrdinalIgnoreCase)) yield return m.Groups[1].Value;
            foreach (Match m in Regex.Matches(code, @"\bfunction\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase))
                yield return m.Groups[1].Value;
            foreach (Match m in Regex.Matches(code, @"\bfor\s+([A-Za-z_]\w*)\s+in\b", RegexOptions.IgnoreCase))
                yield return m.Groups[1].Value;
            foreach (Match m in Regex.Matches(code, @"\bparameter\s+(.*?)(?:\.(?:\s|$)|$)", RegexOptions.IgnoreCase))
                foreach (var name in ParameterNames(m.Groups[1].Value)) yield return name;
        }

        /// <summary>The names in "a, b is max(1, 2), c": the identifier opening each top-level comma segment.</summary>
        private static IEnumerable<string> ParameterNames(string list)
        {
            var depth = 0;
            var start = 0;
            for (var i = 0; i <= list.Length; i++)
            {
                if (i < list.Length)
                {
                    if (list[i] == '(' || list[i] == '[') depth++;
                    else if (list[i] == ')' || list[i] == ']') depth--;
                    if (!(list[i] == ',' && depth == 0)) continue;
                }
                var seg = Regex.Match(list.Substring(start, i - start), @"^\s*([A-Za-z_]\w*)");
                if (seg.Success) yield return seg.Groups[1].Value;
                start = i + 1;
            }
        }
    }
}
