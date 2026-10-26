// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// The Script Check (ADR 0003): compiles a script and reads its opcodes against the forbidden list. The
    /// core never references kOS; the adapter injects the real check (ClaudiaKermin.ScriptCheck), tests a fake.
    /// </summary>
    public interface IScriptCheck
    {
        ScriptCheckResult Check(string script);
    }

    /// <summary>A pass, or why the script failed: a compile error, the forbidden calls found, or both.</summary>
    public sealed class ScriptCheckResult
    {
        /// <summary>The compiler's message when the script doesn't compile; null when it does.</summary>
        public readonly string CompileError;
        /// <summary>Each forbidden call found, e.g. "line 3: COPYPATH (touch files)".</summary>
        public readonly IList<string> Forbidden;

        /// <summary>Lint warnings (see <see cref="ScriptLints"/>); they never make the check fail.</summary>
        public IList<string> Warnings = new List<string>();

        public ScriptCheckResult(string compileError, IList<string> forbidden)
        {
            CompileError = compileError;
            Forbidden = forbidden ?? new List<string>();
        }

        public static ScriptCheckResult Pass()
        {
            return new ScriptCheckResult(null, null);
        }

        public bool Passed
        {
            get { return CompileError == null && Forbidden.Count == 0; }
        }

        /// <summary>One line per problem, for Claude and for the player.</summary>
        public IList<string> Problems
        {
            get
            {
                var problems = new List<string>();
                if (CompileError != null) problems.Add("doesn't compile: " + CompileError);
                problems.AddRange(Forbidden);
                return problems;
            }
        }
    }
}
