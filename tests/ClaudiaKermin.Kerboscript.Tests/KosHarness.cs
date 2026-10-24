// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using kOS.Safe;
using kOS.Safe.Binding;
using kOS.Safe.Callback;
using kOS.Safe.Compilation;
using kOS.Safe.Compilation.KS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Execution;
using kOS.Safe.Function;
using kOS.Safe.Module;
using kOS.Safe.Persistence;
using kOS.Safe.Screen;
using kOS.Safe.Serialization;
using kOS.Safe.Utilities;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// Compiles kerboscript with kOS.Safe and runs it on a real kOS CPU with no KSP, the way kOS's own
    /// test suite does. Only kOS.Safe's built-ins exist (print, maths, strings), plus the
    /// <c>lexicon</c> and <c>list</c> constructors from <see cref="TestFunctions"/>; anything that needs
    /// the game (ship, time, stage) is unavailable, so only pure helpers can run here.
    /// </summary>
    internal static class KosHarness
    {
        private const string Done = "__claudia_test_done__";
        private static readonly object Gate = new object();
        private static bool _initialised;

        /// <summary>Compiles <paramref name="source"/>; returns null or the compiler's message.</summary>
        public static string CompileError(string source)
        {
            lock (Gate)
            {
                Init();
                try
                {
                    var shared = NewShared();
                    var parts = shared.ScriptHandler.Compile(GlobalPath.FromString("0:/test.ks"), 1, source, "test", Options(shared));
                    return parts == null ? "compiler returned no code" : null;
                }
                catch (Exception e)
                {
                    return e.GetType().Name + ": " + e.Message;
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="source"/> to the end and returns what it printed, one entry per
        /// <c>print</c>. Throws if it doesn't compile, fails at run time or runs too long.
        /// </summary>
        public static IList<string> Run(string source)
        {
            lock (Gate)
            {
                Init();
                var printed = new List<string>();
                var errors = new List<string>();
                var shared = NewShared();
                CapturingScreen screen;
                shared.Screen = CapturingScreen.Create(printed, out screen);
                ((TestLogger)SafeHouse.Logger).Errors = errors;

                var cpu = new CPU(shared);
                shared.Cpu = cpu;
                cpu.Boot();
                printed.Clear();

                var parts = shared.ScriptHandler.Compile(GlobalPath.FromString("0:/test.ks"), 1,
                    source + "\nprint \"" + Done + "\".\n", "test", Options(shared));
                cpu.GetCurrentContext().AddParts(parts);

                for (var step = 0; step < 20000; step++)
                {
                    cpu.KOSFixedUpdate(0.02);
                    if (printed.Contains(Done))
                    {
                        printed.Remove(Done);
                        return printed;
                    }
                    if (errors.Count > 0) break;
                }
                throw new InvalidOperationException("script did not finish: " + string.Join("\n", errors.Concat(screen.All)));
            }
        }

        private static CompilerOptions Options(SafeSharedObjects shared)
        {
            return new CompilerOptions
            {
                LoadProgramsInSameAddressSpace = false,
                IsCalledFromRun = false,
                AllowClobberBuiltins = false,
                FuncManager = shared.FunctionManager,
                BindManager = shared.BindingMgr,
            };
        }

        private static void Init()
        {
            if (_initialised) return;
            SafeHouse.Init(Stub<IConfig>.Create(), new VersionInfo(1, 6, 0, 1), "", false, System.IO.Path.GetTempPath());
            SafeHouse.Logger = new TestLogger();
            // kOS's walk also registers the [Function]s in assemblies already loaded (this one's
            // TestFunctions); naming this assembly too would register them twice.
            AssemblyWalkAttribute.Walk(new[] { typeof(SafeHouse).Assembly });
            Opcode.InitMachineCodeData();
            CompiledObject.InitTypeData();
            SafeSerializationMgr.CheckIDumperStatics();
            _initialised = true;
        }

        private static SafeSharedObjects NewShared()
        {
            var shared = new SafeSharedObjects();
            shared.FunctionManager = new FunctionManager(shared);
            shared.BindingMgr = Stub<IBindingManager>.Create();
            shared.GameEventDispatchManager = Stub<IGameEventDispatchManager>.Create();
            shared.Processor = Stub<IProcessor>.Create();
            shared.ScriptHandler = new KSScript();
            shared.UpdateHandler = new UpdateHandler();
            shared.VolumeMgr = new VolumeManager();
            shared.Logger = SafeHouse.Logger;
            return shared;
        }

        /// <summary>Any kOS interface, answering every call with a harmless default.</summary>
        public class Stub<T> : DispatchProxy
        {
            public static T Create()
            {
                return DispatchProxy.Create<T, Stub<T>>();
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                var type = method.ReturnType;
                if (method.Name == "get_InstructionsPerUpdate") return 2000;
                if (type == typeof(string)) return "";
                return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }

        /// <summary>The terminal: each <c>print</c> becomes one entry; everything else is a no-op.</summary>
        public class CapturingScreen : Stub<IScreenBuffer>
        {
            public List<string> Printed;
            public readonly List<string> All = new List<string>();

            public static IScreenBuffer Create(List<string> printed, out CapturingScreen screen)
            {
                var proxy = DispatchProxy.Create<IScreenBuffer, CapturingScreen>();
                screen = (CapturingScreen)(object)proxy;
                screen.Printed = printed;
                return proxy;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if ((method.Name == "Print" || method.Name == "PrintLine") && args.Length > 0)
                {
                    Printed.Add((string)args[0]);
                    All.Add((string)args[0]);
                }
                if (method.Name == "get_ColumnCount") return 80;
                if (method.Name == "get_RowCount") return 40;
                return base.Invoke(method, args);
            }
        }

        private sealed class TestLogger : ILogger
        {
            public List<string> Errors = new List<string>();
            public void Log(string text) { }
            public void Log(Exception e) { Errors.Add(e.Message); }
            public void SuperVerbose(string s) { }
            public void LogWarning(string s) { }
            public void LogWarningAndScreen(string s) { }
            public void LogException(Exception exception) { Errors.Add(exception.Message); }
            public void LogError(string s) { Errors.Add(s); }
        }
    }

    /// <summary>
    /// Stand-ins for kOS's <c>lexicon</c> and <c>list</c> constructors, which live in kOS.dll (it needs
    /// KSP). They build the same kOS.Safe structures the real ones do.
    /// </summary>
    internal static class TestFunctions
    {
        [Function("lexicon", "lex")]
        public class FunctionLexicon : CollectionFunction
        {
            public override void Execute(SafeSharedObjects shared)
            {
                ReturnValue = new Lexicon(PopAll(shared));
            }
        }

        [Function("list")]
        public class FunctionList : CollectionFunction
        {
            public override void Execute(SafeSharedObjects shared)
            {
                ReturnValue = new ListValue(PopAll(shared));
            }
        }

        public abstract class CollectionFunction : SafeFunctionBase
        {
            protected List<Structure> PopAll(SafeSharedObjects shared)
            {
                var args = new Structure[CountRemainingArgs(shared)];
                for (var i = args.Length - 1; i >= 0; i--) args[i] = PopStructureAssertEncapsulated(shared, true);
                AssertArgBottomAndConsume(shared);
                return args.ToList();
            }
        }
    }
}
