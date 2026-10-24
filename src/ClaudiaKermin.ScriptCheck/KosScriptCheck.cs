// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClaudiaKermin.Core.Kos;
using kOS.Safe;
using kOS.Safe.Binding;
using kOS.Safe.Compilation;
using kOS.Safe.Compilation.KS;
using kOS.Safe.Function;
using kOS.Safe.Persistence;
using kOS.Safe.Utilities;

namespace ClaudiaKermin.ScriptCheck
{
    /// <summary>
    /// The Script Check (ADR 0003): compiles the script with kOS.Safe, as kOS would, and reads every opcode
    /// (OpcodeCall targets and suffix identifiers) against the forbidden list. The mod runs it before any
    /// script is offered for Approval; CI runs it over every shipped script. It fails closed: if kOS.Safe
    /// can't be loaded or the check throws, the script fails.
    /// </summary>
    public sealed class KosScriptCheck : IScriptCheck
    {
        public ScriptCheckResult Check(string script)
        {
            try
            {
                return Run(script ?? "");
            }
            catch (Exception e)
            {
                return new ScriptCheckResult(null, new List<string> { "the Script Check couldn't run (" + e.GetType().Name + ": " + e.Message + ")" });
            }
        }

        // Kept apart so a missing kOS.Safe fails inside Check's try, not when Check is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ScriptCheckResult Run(string script)
        {
            // Inside KSP kOS has set its logger; outside it (CI, tests) the compiler needs one.
            if (SafeHouse.Logger == null) SafeHouse.Logger = new QuietLogger();

            var options = new CompilerOptions
            {
                // The options the kOS bridge (ClaudiaKermin.KOS) compiles with, so the check sees what runs.
                LoadProgramsInSameAddressSpace = true,
                IsCalledFromRun = false,
                AllowClobberBuiltins = false,
                FuncManager = new BuiltInNames(),
                BindManager = new NoBindings(),
            };
            List<CodePart> parts;
            try
            {
                parts = new KSScript().Compile(GlobalPath.FromString("0:/mission-script.ks"), 1, script, "scriptcheck", options);
            }
            catch (Exception e)
            {
                return new ScriptCheckResult(e.Message.Trim(), null);
            }
            if (parts == null) return new ScriptCheckResult("the compiler returned no code", null);

            var sections = new List<List<Opcode>>();
            foreach (var part in parts)
            {
                sections.Add(part.FunctionsCode);
                sections.Add(part.InitializationCode);
                sections.Add(part.MainCode);
            }
            return new ScriptCheckResult(null, OpcodeReader.Read(sections));
        }

        /// <summary>The compiler only asks whether a name is a built-in; nothing is ever called.</summary>
        private sealed class BuiltInNames : IFunctionManager
        {
            public void Load() { }
            public void CallFunction(string functionName) { throw new NotSupportedException(); }
            public bool Exists(string functionName) { return ForbiddenList.BuiltIns.Contains(functionName); }
        }

        private sealed class NoBindings : IBindingManager
        {
            public void Load() { }
            public void AddBoundVariable(string name, BindingGetDlg getDelegate, BindingSetDlg setDelegate) { }
            public void AddGetter(string name, BindingGetDlg dlg) { }
            public void AddGetter(IEnumerable<string> names, BindingGetDlg dlg) { }
            public void AddSetter(string name, BindingSetDlg dlg) { }
            public void AddSetter(IEnumerable<string> names, BindingSetDlg dlg) { }
            public bool HasGetter(string name) { return false; }
            public bool HasSetter(string name) { return false; }
            public void MarkVolatile(string name) { }
            public void PreUpdate() { }
            public void PostUpdate() { }
            public void ToggleFlyByWire(string paramName, bool enabled) { }
            public void SelectAutopilotMode(string autopilotMode) { }
        }

        private sealed class QuietLogger : ILogger
        {
            public void Log(string text) { }
            public void Log(Exception e) { }
            public void SuperVerbose(string s) { }
            public void LogWarning(string s) { }
            public void LogWarningAndScreen(string s) { }
            public void LogException(Exception exception) { }
            public void LogError(string s) { }
        }
    }
}
