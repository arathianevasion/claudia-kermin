// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Checks, once at startup, that the kOS internals the bridge calls are there. It looks them up by
    /// name only, so a kOS with a different shape can't break the check itself. While kOS is
    /// incompatible the bridge refuses every start.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class KosInternals : MonoBehaviour
    {
        /// <summary>The check's result; Compatible until the check has run.</summary>
        public static KosCompatibility Result { get; private set; }

        // "Type, assembly", member, parameter count (-1: a property, -2: a field).
        private static readonly string[][] Needed =
        {
            new[] { "kOS.Safe.Execution.ICpu, kOS.Safe", "SwitchToProgramContext", "0" },
            new[] { "kOS.Safe.Execution.ICpu, kOS.Safe", "YieldProgram", "1" },
            new[] { "kOS.Safe.Execution.ICpu, kOS.Safe", "BreakExecution", "1" },
            new[] { "kOS.Safe.Execution.ICpu, kOS.Safe", "AddPopContextNotifyee", "1" },
            new[] { "kOS.Safe.Execution.ICpu, kOS.Safe", "RemovePopContextNotifyee", "1" },
            new[] { "kOS.Safe.Execution.IPopContextNotifyee, kOS.Safe", "OnPopContext", "1" },
            new[] { "kOS.Safe.Execution.IProgramContext, kOS.Safe", "Silent", "-1" },
            new[] { "kOS.Safe.Execution.YieldFinishedCompile, kOS.Safe", "RunScript", "5" },
            new[] { "kOS.Safe.Execution.InternalPath, kOS.Safe", "Line", "1" },
            new[] { "kOS.Safe.Compilation.Script, kOS.Safe", "ClearContext", "1" },
            new[] { "kOS.Safe.Compilation.CompilerOptions, kOS.Safe", "LoadProgramsInSameAddressSpace", "-1" },
            new[] { "kOS.Safe.Compilation.CompilerOptions, kOS.Safe", "IsCalledFromRun", "-1" },
            new[] { "kOS.Safe.Compilation.CompilerOptions, kOS.Safe", "AllowClobberBuiltins", "-1" },
            new[] { "kOS.Safe.Compilation.CompilerOptions, kOS.Safe", "FuncManager", "-1" },
            new[] { "kOS.Safe.Compilation.CompilerOptions, kOS.Safe", "BindManager", "-1" },
            new[] { "kOS.Safe.Screen.IInterpreter, kOS.Safe", "IsWaitingForCommand", "0" },
            new[] { "kOS.Safe.Screen.IScreenBuffer, kOS.Safe", "GetBuffer", "0" },
            new[] { "kOS.Safe.Screen.IScreenBufferLine, kOS.Safe", "ToArray", "0" },
            new[] { "kOS.Safe.Encapsulation.Lexicon, kOS.Safe", "GetEnumerator", "0" },
            new[] { "kOS.Safe.Encapsulation.ScalarValue, kOS.Safe", "GetDoubleValue", "0" },
            new[] { "kOS.Safe.Encapsulation.Suffixes.OneArgsSuffix`1, kOS.Safe", ".ctor", "2" },
            new[] { "kOS.Safe.Encapsulation.Suffixes.Suffix`1, kOS.Safe", ".ctor", "2" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "Cpu", "-1" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "Screen", "-1" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "Interpreter", "-1" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "ScriptHandler", "-1" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "FunctionManager", "-1" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "BindingMgr", "-1" },
            new[] { "kOS.Safe.SafeSharedObjects, kOS.Safe", "Processor", "-1" },
            new[] { "kOS.Safe.Utilities.SafeHouse, kOS.Safe", "Config", "-1" },
            new[] { "kOS.Safe.Encapsulation.IConfig, kOS.Safe", "AllowClobberBuiltIns", "-1" },
            new[] { "kOS.SharedObjects, kOS", "KSPPart", "-1" },
            new[] { "kOS.SharedObjects, kOS", "Vessel", "-1" },
            new[] { "kOS.Module.kOSProcessor, kOS", "ProcessorMode", "-1" },
            new[] { "kOS.Suffixed.Addon, kOS", ".ctor", "1" },
            // The runtime gates (RuntimeGatePatches).
            new[] { "kOS.Safe.Execution.CPU, kOS.Safe", "KOSFixedUpdate", "1" },
            new[] { "kOS.Safe.Execution.CPU, kOS.Safe", "shared", "-2" },
            new[] { "kOS.Safe.Encapsulation.Structure, kOS.Safe", "GetSuffix", "2" },
            new[] { "kOS.Safe.Encapsulation.Structure, kOS.Safe", "SetSuffix", "3" },
            new[] { "kOS.Suffixed.Config, kOS", "SetSuffix", "3" },
            new[] { "kOS.Suffixed.KUniverseValue, kOS", ".ctor", "1" },
            new[] { "kOS.Suffixed.TimeWarpValue, kOS", "SetModeAsString", "1" },
            new[] { "kOS.Suffixed.TimeWarpValue, kOS", "SetWarp", "1" },
            new[] { "kOS.Suffixed.TimeWarpValue, kOS", "SetRate", "1" },
            new[] { "kOS.Suffixed.TimeWarpValue, kOS", "WarpTo", "1" },
            new[] { "kOS.Safe.Persistence.Archive, kOS.Safe", "CreateFile", "1" },
            new[] { "kOS.Safe.Persistence.Archive, kOS.Safe", "CreateDirectory", "1" },
            new[] { "kOS.Safe.Persistence.Archive, kOS.Safe", "SaveFile", "3" },
            new[] { "kOS.Safe.Persistence.Archive, kOS.Safe", "Delete", "2" },
            new[] { "kOS.Safe.Persistence.Archive, kOS.Safe", "Clear", "0" },
            new[] { "kOS.Safe.Persistence.ArchiveFile, kOS.Safe", "Write", "1" },
            new[] { "kOS.Safe.Persistence.ArchiveFile, kOS.Safe", "Clear", "0" },
            new[] { "kOS.Safe.Function.DebugFreezeGame, kOS.Safe", "Execute", "1" },
            new[] { "kOS.Suffixed.VesselTarget, kOS", "Vessel", "-1" },
            new[] { "kOS.Suffixed.Part.PartValue, kOS", "Part", "-1" },
            new[] { "kOS.Suffixed.PartModuleField.PartModuleFields, kOS", "partModule", "-2" },
        };

        static KosInternals()
        {
            Result = KosCompatibility.Compatible;
        }

        private void Awake()
        {
            Result = Check();
            if (Result.IsCompatible)
            {
                Debug.Log(ModInfo.LogTag + " kOS internals check passed");
            }
            else
            {
                Debug.LogError(ModInfo.LogTag + " " + Result.Message);
                ScreenMessages.PostScreenMessage(Result.Message, 15f, ScreenMessageStyle.UPPER_CENTER);
            }
            Destroy(this);
        }

        public static KosCompatibility Check()
        {
            var missing = new List<string>();
            foreach (var need in Needed)
            {
                var typeAndAssembly = need[0].Split(',');
                var name = typeAndAssembly[0] + "." + need[1];
                try
                {
                    if (!Has(FindType(typeAndAssembly[0], typeAndAssembly[1].Trim()), need[1], int.Parse(need[2]))) missing.Add(name);
                }
                catch (Exception e)
                {
                    missing.Add(name + " (" + e.GetType().Name + ")");
                }
            }
            return KosCompatibility.FromMissing(missing);
        }

        // KSP loads plugins itself, so look in the loaded assemblies rather than asking the loader by name.
        private static Type FindType(string typeName, string assemblyName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
            return assembly == null ? null : assembly.GetType(typeName, false);
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static bool Has(Type type, string member, int parameters)
        {
            if (type == null) return false;
            if (member == ".ctor") return type.GetConstructors(Any).Any(c => c.GetParameters().Length == parameters);
            if (parameters == -2) return type.GetField(member, Any) != null;
            if (parameters < 0) return SelfAndInterfaces(type).Any(t => t.GetProperty(member, Any) != null);
            return SelfAndInterfaces(type).SelectMany(t => t.GetMethods(Any))
                .Any(m => m.Name == member && m.GetParameters().Length == parameters);
        }

        // Interface members live on the interface itself or on the interfaces it extends.
        private static IEnumerable<Type> SelfAndInterfaces(Type type)
        {
            return new[] { type }.Concat(type.GetInterfaces());
        }
    }
}
