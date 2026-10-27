// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>What a gated kOS call reaches (ADR 0003).</summary>
    public enum GateTarget
    {
        /// <summary>Anything the gates don't watch.</summary>
        Other,
        /// <summary><c>KUNIVERSE</c>: save, load, revert, switch and launch.</summary>
        KUniverse,
        /// <summary><c>CONFIG</c>, the global kOS settings.</summary>
        Config,
        /// <summary>Time warp, through <c>KUNIVERSE:TIMEWARP</c> or the <c>WARP</c> and <c>WARPMODE</c> bindings.</summary>
        TimeWarp,
        /// <summary>A write or delete on the Archive (<c>0:/</c>, the shared <c>Ships/Script</c> folder).</summary>
        Archive,
        /// <summary>The <c>debugfreezegame</c> function.</summary>
        DebugFreezeGame,
        /// <summary>A vessel's suffixes.</summary>
        Vessel,
        /// <summary>A part's suffixes (engines, docking ports, sensors and the rest included).</summary>
        Part,
        /// <summary>A part module's suffixes (kOS processor modules included).</summary>
        PartModule,
    }

    /// <summary>
    /// One kOS call, as a gate sees it: the facts the Harmony patches read off the live objects, and
    /// nothing else, so the decision runs without KSP.
    /// </summary>
    public sealed class GateCall
    {
        public GateTarget Target;

        /// <summary>The suffix or operation, as kOS names it (e.g. <c>QUICKSAVE</c>, <c>DOEVENT</c>, <c>MODE</c>).</summary>
        public string Suffix = "";

        /// <summary>An assignment (<c>SET x:SUFFIX TO ...</c>) rather than a read or a call.</summary>
        public bool IsSet;

        /// <summary>The value being set, when it matters to the decision (the warp mode).</summary>
        public string Value = "";

        /// <summary>The warp rate index being set.</summary>
        public int WarpRateIndex;

        /// <summary>KSP's warp mode is physics warp now.</summary>
        public bool PhysicsWarpMode;

        /// <summary>The warp rate index now in force; the mode switch to PHYSICS is allowed only at 0 (1x).</summary>
        public int CurrentWarpIndex;

        /// <summary>The craft is on a Coast now (<see cref="CoastCheck"/>).</summary>
        public bool OnCoast;

        /// <summary>
        /// Physics warp is allowed at all: the <c>physics_warp</c> player config now, and the Mission Setting later (ADR 0008).
        /// False unless the caller sets it, so a forgotten input refuses.
        /// </summary>
        public bool PhysicsWarpAllowed;

        /// <summary>The vessel, part or module belongs to a vessel other than the running processor's.</summary>
        public bool OtherVessel;

        /// <summary>The part module is a kOS processor.</summary>
        public bool KosProcessorModule;

        /// <summary>The part module is the kOS processor running the program.</summary>
        public bool RunningProcessor;
    }

    /// <summary>
    /// Which kOS calls the runtime gates refuse while a mod-started program runs (ADR 0003). The Script Check
    /// reads compiled opcodes, so anything computed at run time (a TIMEWARP handed in as a parameter, a vessel
    /// found by iterating a list) reaches these instead.
    /// </summary>
    public static class RuntimeGates
    {
        private static readonly HashSet<string> KUniverseCalls = Names(
            "REVERTTOLAUNCH", "REVERTTOEDITOR", "REVERTTO",
            "QUICKSAVE", "QUICKLOAD", "QUICKSAVETO", "QUICKLOADFROM",
            "FORCESETACTIVEVESSEL", "FORCEACTIVE",
            "LAUNCHCRAFT", "LAUNCHCRAFTFROM", "LAUNCHCRAFTWITHCREWFROM");

        private static readonly HashSet<string> VesselCalls = Names("STARTTRACKING", "STOPTRACKING");

        private static readonly HashSet<string> PartCalls = Names(
            "CONTROLFROM", "REMOVESYMMETRY", "UNDOCK", "ACTIVATE", "SHUTDOWN", "TOGGLEMODE", "TOGGLE");

        private static readonly HashSet<string> ModuleCalls = Names(
            "SETFIELD", "DOEVENT", "DOACTION",
            "DEPLOY", "RESET", "TRANSMIT", "DUMP", "DUMPDATA", "COLLECTALL",
            "ACTIVATE", "DEACTIVATE");

        /// <summary>The highest warp rate index physics warp may reach: 4x (<c>WARP</c> 0 to 3).</summary>
        public const int MaxPhysicsWarpIndex = 3;

        /// <summary>
        /// Physics warp from a mod-started program (ADR 0008): only with the switch on and on a Coast, and within
        /// <paramref name="otherLimit"/> (the cap, or the mode switch's 1x); null if it may go ahead.
        /// </summary>
        private static string PhysicsWarpRefusal(GateCall call, string what, string otherLimit)
        {
            var why = !call.PhysicsWarpAllowed ? "physics warp is off" : !call.OnCoast ? "off a Coast" : otherLimit;
            return why == null ? null : "physics warp (" + what + ", " + why + ")";
        }

        /// <summary>What the call tries, for "blocked: …", if a gate refuses it; null if it may go ahead.</summary>
        public static string Refuses(GateCall call)
        {
            var suffix = (call.Suffix ?? "").ToUpperInvariant();
            switch (call.Target)
            {
                case GateTarget.KUniverse:
                    if (call.IsSet) return "SET KUNIVERSE:" + suffix + (suffix == "ACTIVEVESSEL" ? " (switching vessels)" : "");
                    return KUniverseCalls.Contains(suffix) ? "KUNIVERSE:" + suffix : null;
                case GateTarget.Config:
                    return call.IsSet ? "SET CONFIG:" + suffix : null;
                case GateTarget.TimeWarp:
                    if (suffix == "MODE" && string.Equals((call.Value ?? "").Trim(), "PHYSICS", StringComparison.OrdinalIgnoreCase))
                        return PhysicsWarpRefusal(call, "warp mode PHYSICS", call.CurrentWarpIndex > 0 ? "not at 1x" : null);
                    if (suffix == "WARP" && call.PhysicsWarpMode && call.WarpRateIndex > 0)
                        return PhysicsWarpRefusal(call, "warp " + call.WarpRateIndex, call.WarpRateIndex > MaxPhysicsWarpIndex ? "above the 4x cap" : null);
                    // KSP's warp-to warps in whatever mode is set, never changing it (issue #133): in physics
                    // mode it would physics-warp past the WARP gate, so it is refused there (ADR 0004). Rails mode is fine.
                    if (suffix == "WARPTO" && call.PhysicsWarpMode)
                        return "physics warp (WARPTO in warp mode PHYSICS)";
                    return null;
                case GateTarget.Archive:
                    return "Archive " + suffix.ToLowerInvariant() + " (0:/)";
                case GateTarget.DebugFreezeGame:
                    return "debugfreezegame";
                case GateTarget.Vessel:
                    if (!call.OtherVessel) return null;
                    return Mutates(call, suffix, VesselCalls) ? Tried(call, suffix) + " on another vessel" : null;
                case GateTarget.Part:
                    if (!call.OtherVessel) return null;
                    return Mutates(call, suffix, PartCalls) ? Tried(call, suffix) + " on another vessel's part" : null;
                case GateTarget.PartModule:
                    if (!Mutates(call, suffix, ModuleCalls)) return null;
                    // A script may act on its own processor (turning itself off just ends it, with no Script Result).
                    if (call.KosProcessorModule && !call.RunningProcessor) return Tried(call, suffix) + " on another kOS processor";
                    return call.OtherVessel ? Tried(call, suffix) + " on another vessel's part" : null;
                default:
                    return null;
            }
        }

        private static bool Mutates(GateCall call, string suffix, HashSet<string> calls)
        {
            return call.IsSet || calls.Contains(suffix);
        }

        private static string Tried(GateCall call, string suffix)
        {
            return (call.IsSet ? "SET " : "") + suffix;
        }

        private static HashSet<string> Names(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.Ordinal);
        }
    }

    /// <summary>A runtime gate refused a call: the run it belongs to, and what it tried.</summary>
    public sealed class GateTrip
    {
        public int RunId { get; private set; }

        /// <summary>For the Debrief reason "blocked: <see cref="What"/>".</summary>
        public string What { get; private set; }

        public GateTrip(int runId, string what)
        {
            RunId = runId;
            What = what;
        }
    }

    /// <summary>
    /// The processors with a mod-started program running, by part <c>persistentId</c>, each with its run id.
    /// The gates apply only there: the player's own terminal and programs, on any processor with no mod
    /// run, are never touched. Main thread only.
    /// </summary>
    public sealed class GatedRuns
    {
        private readonly Dictionary<uint, int> _runs = new Dictionary<uint, int>();

        /// <summary>A mod-started program began on the processor; its gates are up.</summary>
        public void Open(uint processor, int runId)
        {
            _runs[processor] = runId;
        }

        /// <summary>That run ended. A late close for an earlier run leaves a newer one gated.</summary>
        public void Close(uint processor, int runId)
        {
            int current;
            if (_runs.TryGetValue(processor, out current) && current == runId) _runs.Remove(processor);
        }

        /// <summary>The run whose gates cover the processor, or null.</summary>
        public int? RunOn(uint? processor)
        {
            int runId;
            return processor.HasValue && _runs.TryGetValue(processor.Value, out runId) ? runId : (int?)null;
        }

        /// <summary>The gated processors' part <c>persistentId</c>s.</summary>
        public IEnumerable<uint> Processors
        {
            get { return _runs.Keys; }
        }

        /// <summary>True while any processor is gated; the patches check this first, as most calls happen with none.</summary>
        public bool Any
        {
            get { return _runs.Count > 0; }
        }

        /// <summary>
        /// The trip, if <paramref name="call"/> made by the program on <paramref name="executing"/> (null: no
        /// kOS processor is executing, e.g. a kOS window's own UI) is refused.
        /// </summary>
        public GateTrip Check(uint? executing, GateCall call)
        {
            var runId = RunOn(executing);
            if (!runId.HasValue) return null;
            var what = RuntimeGates.Refuses(call);
            return what == null ? null : new GateTrip(runId.Value, what);
        }
    }
}
