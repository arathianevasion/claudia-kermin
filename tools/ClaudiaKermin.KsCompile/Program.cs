// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using kOS.Safe;
using kOS.Safe.Binding;
using kOS.Safe.Compilation;
using kOS.Safe.Compilation.KS;
using kOS.Safe.Function;
using kOS.Safe.Persistence;
using kOS.Safe.Utilities;

namespace ClaudiaKermin.KsCompile
{
    /// <summary>
    /// Compiles each .ks file (or every .ks under each directory) with kOS.Safe's compiler.
    /// Exit code 0: all compiled; 1: at least one failed; 2: bad usage or no files found.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: ClaudiaKermin.KsCompile <file.ks | directory>...");
                return 2;
            }

            var files = new List<string>();
            foreach (var arg in args)
            {
                if (Directory.Exists(arg))
                    files.AddRange(Directory.GetFiles(arg, "*.ks", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal));
                else if (File.Exists(arg))
                    files.Add(arg);
                else
                {
                    Console.Error.WriteLine("not found: " + arg);
                    return 2;
                }
            }

            if (files.Count == 0)
            {
                Console.Error.WriteLine("no .ks files found in: " + string.Join(" ", args));
                return 2;
            }

            SafeHouse.Logger = new StderrLogger();
            var failures = 0;
            foreach (var file in files)
            {
                var error = Compile(file);
                if (error == null)
                {
                    Console.WriteLine("ok   " + file);
                }
                else
                {
                    failures++;
                    Console.WriteLine("FAIL " + file);
                    Console.WriteLine("     " + error.Replace("\n", "\n     "));
                }
            }

            Console.WriteLine(failures == 0
                ? "compiled " + files.Count + " file(s)"
                : failures + " of " + files.Count + " file(s) failed to compile");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Returns null if the file compiles, otherwise the compiler's message.</summary>
        private static string Compile(string file)
        {
            var options = new CompilerOptions
            {
                LoadProgramsInSameAddressSpace = false,
                IsCalledFromRun = false,
                AllowClobberBuiltins = false,
                FuncManager = new NoFunctions(),
                BindManager = new NoBindings(),
            };

            try
            {
                var path = GlobalPath.FromString("0:/" + Path.GetFileName(file));
                var parts = new KSScript().Compile(path, 1, File.ReadAllText(file), "kscompile", options);
                return parts == null ? "compiler returned no code" : null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// Syntax checking needs no real built-ins: calls to unknown names compile as user
        /// function calls, which kOS resolves at run time.
        /// </summary>
        private sealed class NoFunctions : IFunctionManager
        {
            public void Load() { }
            public void CallFunction(string functionName) { throw new NotSupportedException(); }
            public bool Exists(string functionName) { return false; }
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

        /// <summary>Sends kOS warnings and errors to stderr and drops its routine log chatter.</summary>
        private sealed class StderrLogger : ILogger
        {
            public void Log(string text) { }
            public void Log(Exception e) { }
            public void SuperVerbose(string s) { }
            public void LogWarning(string s) { Console.Error.WriteLine("warning: " + s); }
            public void LogWarningAndScreen(string s) { Console.Error.WriteLine("warning: " + s); }
            public void LogException(Exception exception) { Console.Error.WriteLine(exception); }
            public void LogError(string s) { Console.Error.WriteLine("error: " + s); }
        }
    }
}
