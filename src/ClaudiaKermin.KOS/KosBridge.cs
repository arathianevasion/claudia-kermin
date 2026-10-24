// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Adapter;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using kOS.Module;
using kOS.Safe.Compilation;
using kOS.Safe.Execution;
using kOS.Safe.Module;
using kOS.Safe.Utilities;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Runs kerboscript on a craft's kOS processor and learns how it ended. Source text is compiled in
    /// memory and pushed as a program context, the way kOS runs a boot file, so nothing is written to
    /// <c>0:/</c> or a local disk, and boot files are never touched. Processors are identified by part
    /// <c>persistentId</c>. All kOS calls happen on the main thread: starts and aborts in FixedUpdate
    /// (where kOS runs), end-of-run reads in Update.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KosBridge : MonoBehaviour
    {
        private const string ProgramContext = "program";

        public static KosBridge Instance { get; private set; }

        private readonly List<PendingStart> _pending = new List<PendingStart>();
        private readonly Dictionary<uint, ActiveRun> _runs = new Dictionary<uint, ActiveRun>();
        private readonly Queue<Action> _fixedWork = new Queue<Action>();

        /// <summary>The processor Claudia would use on this vessel: the player's choice if still aboard, else the one nearest the root.</summary>
        public static uint? ChooseProcessor(Vessel vessel, uint? preferred)
        {
            if (vessel == null) return null;
            var parts = vessel.parts.Select(p => new VesselPart(
                p.persistentId,
                p.parent == null ? (uint?)null : p.parent.persistentId,
                p.FindModuleImplementing<kOSProcessor>() != null)).ToList();
            return ProcessorChoice.Choose(parts, preferred);
        }

        /// <summary>
        /// Asks to run <paramref name="source"/> on the processor in part <paramref name="partId"/>.
        /// Exactly one of <paramref name="onRefused"/> (with the reason) or <paramref name="onOutcome"/>
        /// is called later, on the main thread.
        /// </summary>
        public void Start(uint partId, string label, string source, Action<RunOutcome> onOutcome, Action<string> onRefused)
        {
            _pending.Add(new PendingStart(partId, label, source, onOutcome, onRefused, Time.realtimeSinceStartup));
        }

        public bool IsRunning(uint partId)
        {
            return _runs.ContainsKey(partId) || _pending.Any(p => p.PartId == partId);
        }

        /// <summary>Stops the mod's program on that processor, records why, and makes the craft safe.</summary>
        public void Abort(uint partId, string reason)
        {
            _fixedWork.Enqueue(() =>
            {
                // A start still waiting for the processor never begins.
                foreach (var pending in _pending.Where(p => p.PartId == partId).ToList())
                {
                    _pending.Remove(pending);
                    Debug.Log(ModInfo.LogTag + " cancelled '" + pending.Label + "' before it started: " + reason);
                    pending.OnRefused("Aborted before it started: " + reason);
                }

                ActiveRun run;
                if (!_runs.TryGetValue(partId, out run) || run.Run.HasEnded) return;
                run.Run.Aborted(reason);
                Debug.Log(ModInfo.LogTag + " aborting '" + run.Label + "': " + reason);
                if (!run.Addon.IsStale) run.Addon.Shared.Cpu.BreakExecution(true);
                CraftSafety.MakeSafe(run.Vessel);
            });
        }

        /// <summary>From <c>ADDONS:CLAUDIA:REPORT</c>. Reports from programs the mod didn't start are ignored.</summary>
        public void OnReport(uint partId, IDictionary<string, object> report)
        {
            ActiveRun run;
            if (_runs.TryGetValue(partId, out run))
            {
                run.Run.Report(report);
                Debug.Log(ModInfo.LogTag + " report from '" + run.Label + "': " + KosValues.Format(report));
            }
            else
            {
                Debug.Log(ModInfo.LogTag + " ignored a report from a program Claudia didn't start");
            }
        }

        private void Awake()
        {
            Instance = this;
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            foreach (var pending in _pending) Guard(() => pending.OnRefused("Left Flight before the script could start."));
            _pending.Clear();
            // Deliver every outcome before this scene's bridge goes away.
            foreach (var run in _runs.Values.ToList())
            {
                run.Run.Interrupted("left Flight");
                Guard(() => Finish(run));
            }
            if (Instance == this) Instance = null;
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            foreach (var run in _runs.Values) run.Run.Interrupted("left Flight");
        }

        private void FixedUpdate()
        {
            while (_fixedWork.Count > 0) Guard(_fixedWork.Dequeue());
            foreach (var pending in _pending.ToList()) Guard(() => TryStart(pending));
        }

        private void Update()
        {
            foreach (var run in _runs.Values.ToList())
            {
                if (!run.Run.HasEnded && run.Addon.IsStale)
                {
                    // The part or its processor was destroyed mid-run; kOS may never pop the context.
                    run.Run.Interrupted("the kOS processor was lost");
                    run.OnPopContext(null);
                }
                // kOS prints a program's error only after its context pops, so read a frame later.
                if (run.Run.HasEnded && Time.frameCount > run.EndedFrame) Guard(() => Finish(run));
            }
        }

        private void TryStart(PendingStart pending)
        {
            var addon = ClaudiaAddon.ForPart(pending.PartId);
            var decision = RunStart.Decide(KosInternals.Result, StateOf(addon, pending.PartId), Time.realtimeSinceStartup - pending.Since);
            if (decision.Kind == StartDecisionKind.Wait) return;
            _pending.Remove(pending);
            if (decision.Kind == StartDecisionKind.Refuse)
            {
                Debug.Log(ModInfo.LogTag + " refused to start '" + pending.Label + "': " + decision.Reason);
                pending.OnRefused(decision.Reason);
                return;
            }
            try
            {
                Push(addon, pending);
            }
            catch (Exception e)
            {
                _runs.Remove(pending.PartId);
                Debug.LogError(ModInfo.LogTag + " couldn't start '" + pending.Label + "': " + e);
                pending.OnRefused("kOS wouldn't start the script: " + e.Message);
            }
        }

        private ProcessorState StateOf(ClaudiaAddon addon, uint partId)
        {
            var state = new ProcessorState { ModRunActive = _runs.ContainsKey(partId) };
            if (addon == null)
            {
                // A loaded processor part whose addon kOS hasn't made yet is still starting up
                // (HasUpdated stays false, so the start waits).
                Part part;
                state.Found = FlightGlobals.FindLoadedPart(partId, out part) && part.FindModuleImplementing<kOSProcessor>() != null;
                state.Powered = true;
                return state;
            }
            state.Found = true;
            state.Powered = addon.Processor.ProcessorMode == ProcessorModes.READY;
            try
            {
                state.Idle = addon.Shared.Interpreter.IsWaitingForCommand();
                state.HasUpdated = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // kOS throws here until the processor's first Update after Flight loads.
                state.HasUpdated = false;
            }
            return state;
        }

        // Start method C from the kOS round-trip spike, with the source compiled straight from memory.
        private void Push(ClaudiaAddon addon, PendingStart pending)
        {
            var shared = addon.Shared;
            var run = new ActiveRun(pending, addon, ReadTerminal(addon));
            var options = new CompilerOptions
            {
                LoadProgramsInSameAddressSpace = true,
                FuncManager = shared.FunctionManager,
                BindManager = shared.BindingMgr,
                AllowClobberBuiltins = SafeHouse.Config.AllowClobberBuiltIns,
                IsCalledFromRun = false,
            };

            _runs[pending.PartId] = run;
            shared.Cpu.AddPopContextNotifyee(run);
            shared.ScriptHandler.ClearContext(ProgramContext);
            var context = shared.Cpu.SwitchToProgramContext();
            context.Silent = false;
            shared.Cpu.YieldProgram(YieldFinishedCompile.RunScript(
                new SourcePath(pending.Label, pending.Source), 1, pending.Source, ProgramContext, options));
            Debug.Log(ModInfo.LogTag + " started '" + pending.Label + "' on part " + pending.PartId);
        }

        private void Finish(ActiveRun run)
        {
            // Removed first, so a throw below can't make Update finish the same run every frame.
            _runs.Remove(run.PartId);
            if (!run.Addon.IsStale) run.Addon.Shared.Cpu.RemovePopContextNotifyee(run);
            var outcome = run.Run.Finish(ReadTerminal(run.Addon));
            // kOS hands controls back as the program ends, so make safe again once it has.
            if (outcome.WasAborted) CraftSafety.MakeSafe(run.Vessel);
            Debug.Log(ModInfo.LogTag + " '" + run.Label + "' ended: " + outcome.Summary +
                      (outcome.ErrorText.Length > 0 ? "\n" + outcome.ErrorText : ""));
            run.OnOutcome(outcome);
        }

        private static IList<string> ReadTerminal(ClaudiaAddon addon)
        {
            if (addon.IsStale || addon.Shared.Screen == null) return new string[0];
            return addon.Shared.Screen.GetBuffer().Select(line => new string(line.ToArray())).ToList();
        }

        private static void Guard(Action work)
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " kOS bridge error: " + e);
            }
        }

        private sealed class PendingStart
        {
            public readonly uint PartId;
            public readonly string Label;
            public readonly string Source;
            public readonly Action<RunOutcome> OnOutcome;
            public readonly Action<string> OnRefused;
            public readonly float Since;

            public PendingStart(uint partId, string label, string source, Action<RunOutcome> onOutcome, Action<string> onRefused, float since)
            {
                PartId = partId;
                Label = label;
                Source = source;
                OnOutcome = onOutcome;
                OnRefused = onRefused;
                Since = since;
            }
        }

        /// <summary>A started program. kOS calls <see cref="OnPopContext"/> when it ends for any reason.</summary>
        private sealed class ActiveRun : IPopContextNotifyee
        {
            public readonly uint PartId;
            public readonly string Label;
            public readonly ClaudiaAddon Addon;
            public readonly KosRun Run;
            public readonly Action<RunOutcome> OnOutcome;
            public int EndedFrame = int.MaxValue;

            public ActiveRun(PendingStart start, ClaudiaAddon addon, IList<string> terminal)
            {
                PartId = start.PartId;
                Label = start.Label;
                OnOutcome = start.OnOutcome;
                Addon = addon;
                Run = new KosRun(terminal);
            }

            /// <summary>The processor's vessel now: after an undock or staging it follows the part.</summary>
            public Vessel Vessel { get { return Addon.IsStale ? null : Addon.Shared.Vessel; } }

            public bool OnPopContext(IProgramContext context)
            {
                if (Run.HasEnded) return false;
                Run.ProgramEnded();
                EndedFrame = Time.frameCount;
                // One program per notifyee: don't stay registered.
                return false;
            }
        }
    }
}
