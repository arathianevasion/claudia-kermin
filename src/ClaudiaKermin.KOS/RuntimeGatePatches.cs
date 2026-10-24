// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Reflection;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using HarmonyLib;
using kOS;
using kOS.Module;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Exceptions;
using kOS.Safe.Execution;
using kOS.Safe.Function;
using kOS.Safe.Persistence;
using kOS.Suffixed;
using kOS.Suffixed.Part;
using kOS.Suffixed.PartModuleField;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The runtime gates (ADR 0003): Harmony prefixes on kOS internals that refuse the most harmful calls, but
    /// only from a processor running a mod-started program (<see cref="Runs"/>, keyed on the run id). kOS runs
    /// each processor's program inside <c>CPU.KOSFixedUpdate</c>, so a prefix there says which processor is
    /// executing; a call from anywhere else (the player's terminal on another processor, a kOS window, the mod
    /// itself) is never gated. A refused call throws a kOS error, which stops the program; the bridge has
    /// recorded the abort and its reason first, and makes the craft safe. What is refused is decided in
    /// <see cref="RuntimeGates"/>; this class only reads the facts off kOS's objects.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class RuntimeGatePatches : MonoBehaviour
    {
        private static GatedRuns Runs
        {
            get { return KosGates.Runs; }
        }

        private static bool patched;

        /// <summary>The processor whose program kOS is executing right now, or null.</summary>
        private static SharedObjects executing;

        /// <summary>Reads <c>PartModuleFields.partModule</c>; made in <see cref="Install"/>, so a missing field fails the install, not this type.</summary>
        private static AccessTools.FieldRef<PartModuleFields, PartModule> moduleOf;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                Install(new Harmony("claudiakermin.gates"));
                KosGates.Failure = null;
                Debug.Log(ModInfo.LogTag + " runtime gates installed");
            }
            catch (Exception e)
            {
                KosGates.Failure = "the runtime gates could not be installed (" + e.GetType().Name + ")";
                Debug.LogError(ModInfo.LogTag + " " + KosGates.Failure + "; Claudia won't run scripts: " + e);
            }
            Destroy(this);
        }

        private static void Install(Harmony harmony)
        {
            moduleOf = AccessTools.FieldRefAccess<PartModuleFields, PartModule>("partModule");
            Patch(harmony, Method(typeof(CPU), "KOSFixedUpdate", typeof(double)), nameof(ExecutingPrefix), nameof(ExecutingFinalizer));

            // KUNIVERSE, CONFIG and the vessel, part and module mutators all go through kOS's suffix dispatch.
            Patch(harmony, Method(typeof(Structure), "GetSuffix", typeof(string), typeof(bool)), nameof(GetSuffixPrefix));
            Patch(harmony, Method(typeof(Structure), "SetSuffix", typeof(string), typeof(object), typeof(bool)), nameof(SetSuffixPrefix));
            // CONFIG overrides SetSuffix and doesn't call the base for its own settings.
            Patch(harmony, Method(typeof(kOS.Suffixed.Config), "SetSuffix", typeof(string), typeof(object), typeof(bool)), nameof(SetSuffixPrefix));

            // Physics warp: the WARP and WARPMODE bindings and KUNIVERSE:TIMEWARP all end here, however the
            // TIMEWARP structure reached the script (a parameter, a variable, a lexicon).
            Patch(harmony, Method(typeof(TimeWarpValue), "SetModeAsString", typeof(StringValue)), nameof(WarpModePrefix));
            // SetWarp and SetRate, not the tiny SetWarpRate they share, which Mono may inline past a patch.
            Patch(harmony, Method(typeof(TimeWarpValue), "SetWarp", typeof(ScalarIntValue)), nameof(WarpIndexPrefix));
            Patch(harmony, Method(typeof(TimeWarpValue), "SetRate", typeof(ScalarValue)), nameof(WarpRatePrefix));

            // Archive writes and deletes (copypath, movepath, deletepath, log, writejson, compile...).
            Patch(harmony, Method(typeof(Archive), "CreateFile", typeof(VolumePath)), nameof(ArchiveWritePrefix));
            Patch(harmony, Method(typeof(Archive), "CreateDirectory", typeof(VolumePath)), nameof(ArchiveWritePrefix));
            Patch(harmony, Method(typeof(Archive), "SaveFile", typeof(VolumePath), typeof(FileContent), typeof(bool)), nameof(ArchiveWritePrefix));
            Patch(harmony, Method(typeof(Archive), "Delete", typeof(VolumePath), typeof(bool)), nameof(ArchiveDeletePrefix));
            Patch(harmony, Method(typeof(Archive), "Clear"), nameof(ArchiveDeletePrefix));
            Patch(harmony, Method(typeof(ArchiveFile), "Write", typeof(byte[])), nameof(ArchiveWritePrefix));
            Patch(harmony, Method(typeof(ArchiveFile), "Clear"), nameof(ArchiveDeletePrefix));

            Patch(harmony, Method(typeof(DebugFreezeGame), "Execute", typeof(SafeSharedObjects)), nameof(DebugFreezeGamePrefix));
        }

        private static MethodInfo Method(Type type, string name, params Type[] parameters)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void Patch(Harmony harmony, MethodInfo original, string prefix, string finalizer = null)
        {
            harmony.Patch(original,
                prefix: new HarmonyMethod(typeof(RuntimeGatePatches), prefix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(RuntimeGatePatches), finalizer));
        }

        // ---- Which processor is executing ----

        private static void ExecutingPrefix(SafeSharedObjects ___shared, out SharedObjects __state)
        {
            __state = executing;
            executing = ___shared as SharedObjects;
        }

        private static Exception ExecutingFinalizer(Exception __exception, SharedObjects __state)
        {
            executing = __state;
            return __exception;
        }

        /// <summary>The executing processor, if it is gated; null on the fast path that nearly every call takes.</summary>
        private static SharedObjects Gated()
        {
            var shared = executing;
            if (!Runs.Any || shared == null || shared.KSPPart == null) return null;
            return Runs.RunOn(shared.KSPPart.persistentId).HasValue ? shared : null;
        }

        // ---- The gates ----

        private static void GetSuffixPrefix(Structure __instance, string suffixName)
        {
            var shared = Gated();
            if (shared != null) Gate(shared, Classify(__instance, suffixName, false, shared));
        }

        private static void SetSuffixPrefix(Structure __instance, string suffixName)
        {
            var shared = Gated();
            if (shared != null) Gate(shared, Classify(__instance, suffixName, true, shared));
        }

        private static void WarpModePrefix(StringValue modeString)
        {
            var shared = Gated();
            if (shared != null)
                Gate(shared, new GateCall { Target = GateTarget.TimeWarp, Suffix = "MODE", IsSet = true, Value = modeString == null ? "" : modeString.ToString() });
        }

        private static void WarpIndexPrefix(ScalarIntValue newIndex)
        {
            var shared = Gated();
            if (shared != null) GateWarp(shared, newIndex == null ? 0 : (int)newIndex);
        }

        private static void WarpRatePrefix(ScalarValue desiredRate)
        {
            var shared = Gated();
            if (shared == null || desiredRate == null) return;
            // Any rate above 1x is a warp; kOS rounds it to a rate index (ties go up), so don't second-guess which.
            GateWarp(shared, desiredRate.GetDoubleValue() > 1 ? 1 : 0);
        }

        private static void GateWarp(SharedObjects shared, int index)
        {
            Gate(shared, new GateCall
            {
                Target = GateTarget.TimeWarp,
                Suffix = "WARP",
                IsSet = true,
                WarpRateIndex = index,
                PhysicsWarpMode = TimeWarp.WarpMode == TimeWarp.Modes.LOW,
            });
        }

        private static void ArchiveWritePrefix()
        {
            var shared = Gated();
            if (shared != null) Gate(shared, new GateCall { Target = GateTarget.Archive, Suffix = "write" });
        }

        private static void ArchiveDeletePrefix()
        {
            var shared = Gated();
            if (shared != null) Gate(shared, new GateCall { Target = GateTarget.Archive, Suffix = "delete" });
        }

        private static void DebugFreezeGamePrefix()
        {
            var shared = Gated();
            if (shared != null) Gate(shared, new GateCall { Target = GateTarget.DebugFreezeGame, Suffix = "debugfreezegame" });
        }

        /// <summary>The facts about a suffix use on a kOS structure; TIMEWARP is gated at its setters instead.</summary>
        private static GateCall Classify(Structure structure, string suffix, bool set, SharedObjects shared)
        {
            var call = new GateCall { Suffix = suffix ?? "", IsSet = set };
            if (structure is KUniverseValue)
            {
                call.Target = GateTarget.KUniverse;
            }
            else if (structure is kOS.Suffixed.Config)
            {
                call.Target = GateTarget.Config;
            }
            else if (structure is VesselTarget)
            {
                call.Target = GateTarget.Vessel;
                call.OtherVessel = Other(((VesselTarget)structure).Vessel, shared);
            }
            else if (structure is PartValue)
            {
                var part = ((PartValue)structure).Part;
                call.Target = GateTarget.Part;
                call.OtherVessel = part != null && Other(part.vessel, shared);
            }
            else if (structure is PartModuleFields)
            {
                var module = moduleOf((PartModuleFields)structure);
                call.Target = GateTarget.PartModule;
                call.OtherVessel = module != null && Other(module.vessel, shared);
                call.KosProcessorModule = module is kOSProcessor;
                call.RunningProcessor = module != null && ReferenceEquals(module, shared.Processor);
            }
            return call;
        }

        private static bool Other(Vessel vessel, SharedObjects shared)
        {
            return vessel != null && shared.Vessel != null && vessel.id != shared.Vessel.id;
        }

        private static void Gate(SharedObjects shared, GateCall call)
        {
            if (call.Target == GateTarget.Other) return;
            var processor = shared.KSPPart.persistentId;
            var trip = Runs.Check(processor, call);
            if (trip == null) return;
            Debug.Log(ModInfo.LogTag + " runtime gate refused '" + trip.What + "' on part " + processor + " (run " + trip.RunId + ")");
            var bridge = KosBridge.Instance;
            if (bridge != null) bridge.Tripped(processor, trip);
            // kOS catches this in KOSFixedUpdate, prints it and pops the program context: the program stops.
            throw new KOSException("Claudia Kermin's runtime gate refused " + trip.What + ", so the program was stopped.");
        }
    }
}
