// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// Whether a Mission Script is a Custom Script, read from the script itself rather than from Claudia's
    /// own flag. A script is built from Building Blocks when it calls at least one and flies the craft only
    /// through Building Blocks and Core Library helpers (<c>claudia_*</c>): any direct flight control of its
    /// own (locking steering or throttle, waiting, nodes, warp, toggling systems, running other files) makes
    /// it a Custom Script. Plain kerboscript glue (<c>set</c>, <c>if</c>, <c>print</c>, reading a block's
    /// result, <c>stage.</c>, an <c>add node(...)</c> immediately followed by <c>exec_node()</c>, calling
    /// <c>ADDONS:CLAUDIA:REPORT</c>) does not; a script that calls no Building Block is a Custom Script anyway.
    /// </summary>
    public static class ScriptProvenance
    {
        private static readonly Regex Token = new Regex(@"[A-Za-z_][A-Za-z0-9_]*|:|\(|\.", RegexOptions.Compiled);

        private static readonly Regex AddNode = new Regex(@"\badd\s+node\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExecNodeNext = new Regex(@"\s*\.\s*((local\s+\w+\s+is|set\s+\w+\s+to|\w+\s+is)\s+)?exec_node\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Statements that fly the craft or touch files directly, whatever the script around them does.</summary>
        private static readonly HashSet<string> ControlStatements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lock", "unlock", "wait", "add", "remove", "warpto", "run", "runpath", "runoncepath", "toggle",
            "reboot", "shutdown", "switch", "kuniverse", "copypath", "movepath", "deletepath", "create", "createdir",
            "copy", "rename", "delete", "log", "edit",
        };

        /// <summary>Things <c>set</c> may assign that steer, throttle or retarget the craft.</summary>
        private static readonly HashSet<string> ControlTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "throttle", "steering", "target", "warp", "warpmode", "navmode", "control",
            "sas", "rcs", "gear", "lights", "brakes", "bays", "panels", "ladders", "legs", "chutes", "intakes",
            "radiators", "fuelcells", "isru", "drills", "abort", "sasmode",
        };

        /// <summary>Systems switched with <c>sas on.</c> and the like.</summary>
        private static readonly HashSet<string> Switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sas", "rcs", "gear", "lights", "brakes", "bays", "panels", "ladders", "legs", "chutes", "intakes",
            "radiators", "fuelcells", "isru", "drills", "abort", "ag1", "ag2", "ag3", "ag4", "ag5", "ag6", "ag7", "ag8", "ag9", "ag10",
        };

        public static bool IsCustom(string script)
        {
            var blocks = new HashSet<string>(CoreLibrary.Blocks.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
            return IsCustom(script, blocks);
        }

        /// <summary>The decision, for a given set of Building Block names.</summary>
        public static bool IsCustom(string script, ISet<string> blockNames)
        {
            var code = Strip(script ?? "");
            var matches = Token.Matches(code).Cast<Match>().ToList();
            var tokens = matches.Select(m => m.Value).ToList();
            var flownNodes = FlownNodeAdds(code);
            var calledBlock = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (!char.IsLetter(token[0]) && token[0] != '_') continue;
                var previous = i > 0 ? tokens[i - 1] : "";
                var next = i + 1 < tokens.Count ? tokens[i + 1] : "";
                var defining = previous.Equals("function", StringComparison.OrdinalIgnoreCase);

                // Redefining a shipped name would shadow the Core Library.
                if (defining && (blockNames.Contains(token) || token.StartsWith("claudia_", StringComparison.OrdinalIgnoreCase))) return true;
                if (previous == ":") continue; // a suffix (lex:add(...), stage:number), not a statement
                if (next == "(" && !defining && blockNames.Contains(token)) calledBlock = true;
                if (token.Equals("add", StringComparison.OrdinalIgnoreCase) && flownNodes.Contains(matches[i].Index)) continue;
                if (ControlStatements.Contains(token)) return true;
                if (Switches.Contains(token) && (next.Equals("on", StringComparison.OrdinalIgnoreCase) || next.Equals("off", StringComparison.OrdinalIgnoreCase))) return true;
                if (token.Equals("set", StringComparison.OrdinalIgnoreCase))
                {
                    // "set throttle to" and "set ship:control:pilotmainthrottle to": any step of the target names a control.
                    var j = i + 1;
                    while (j + 1 < tokens.Count && tokens[j + 1] == ":") j += 2;
                    for (var k = i + 1; k <= j && k < tokens.Count; k++)
                        if (tokens[k] != ":" && ControlTargets.Contains(tokens[k])) return true;
                }
            }
            return !calledBlock;
        }

        /// <summary>Where each <c>add node(...)</c> that the very next statement flies with <c>exec_node()</c> starts.</summary>
        private static HashSet<int> FlownNodeAdds(string code)
        {
            var found = new HashSet<int>();
            foreach (Match add in AddNode.Matches(code))
            {
                var depth = 0;
                var i = add.Index + add.Length - 1; // the "("
                for (; i < code.Length; i++)
                {
                    if (code[i] == '(') depth++;
                    else if (code[i] == ')' && --depth == 0) break;
                }
                if (i >= code.Length) continue;
                var after = ExecNodeNext.Match(code, i + 1);
                if (after.Success && after.Index == i + 1) found.Add(add.Index);
            }
            return found;
        }

        /// <summary>Drops comments and string contents, so a name inside either is not read as code.</summary>
        internal static string Strip(string script)
        {
            var text = new StringBuilder();
            var inString = false;
            for (var i = 0; i < script.Length; i++)
            {
                var c = script[i];
                if (inString)
                {
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; text.Append(' '); continue; }
                if (c == '/' && i + 1 < script.Length && script[i + 1] == '/')
                {
                    while (i < script.Length && script[i] != '\n') i++;
                    text.Append('\n');
                    continue;
                }
                text.Append(c);
            }
            return text.ToString();
        }
    }
}
