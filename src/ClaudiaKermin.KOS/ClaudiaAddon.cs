// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Adapter;
using kOS;
using kOS.AddOns;
using kOS.Module;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Utilities;
using kOS.Suffixed.Part;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA</c>. kOS makes one per processor when the processor initialises, and makes
    /// new ones on every scene load (the old ones linger with destroyed processors), so the mod finds
    /// them by part <c>persistentId</c>, never by instance or kOS core id.
    /// </summary>
    [kOSAddon("CLAUDIA")]
    [KOSNomenclature("ClaudiaAddon")]
    public partial class ClaudiaAddon : kOS.Suffixed.Addon
    {
        private static readonly Dictionary<uint, ClaudiaAddon> ByPart = new Dictionary<uint, ClaudiaAddon>();

        public ClaudiaAddon(SharedObjects shared) : base(shared)
        {
            AddSuffix("REPORT", new OneArgsSuffix<Lexicon>(Report, "Report a Claudia script's result"));
            AddSuffix("SCIENCE", new OneArgsSuffix<Lexicon, PartValue>(Science, "What an experiment part can still earn here: lexicon experiment, available, tracked, left, situation, biome"));
            // The Core Library's claudia_begin and claudia_finish call these, so the Capcom strip can show progress with no Claude call.
            AddSuffix("BLOCKSTART", new OneArgsSuffix<StringValue>(BlockStart, "A Building Block began"));
            AddSuffix("BLOCKRESULT", new TwoArgsSuffix<StringValue, Lexicon>(BlockResult, "A Building Block returned its result"));
            // A block's trace line for KSP.log (#365), e.g. land's descent; the last one stands for the block if the run ends
            // without a Script Result. A report to the mod like BLOCKSTART, so the Script Check allows it anywhere.
            AddSuffix("BLOCKTRACE", new TwoArgsSuffix<StringValue, Lexicon>(BlockTrace, "A Building Block traced its state for the log"));
            // The same line to KSP.log, not kept as that last state (#407): the RCS Use trace and the SAS-off note, so a crash report
            // keeps the block's own state. A separate suffix, not an optional argument: kOS 1.6 calls only fixed-arity suffixes
            // reliably (an optional-argument BLOCKTRACE failed every call in game).
            AddSuffix("BLOCKNOTE", new TwoArgsSuffix<StringValue, Lexicon>(BlockNote, "A Building Block noted its state in KSP.log only; it doesn't stand for the block"));
            // RCS Use (#393): the Core Library and Claudia's helper calls report each RCS switch, for the mission log and the Debrief's
            // tally. A report to the mod like BLOCKSTART, so the Script Check allows it anywhere; a bad lexicon is logged and ignored.
            AddSuffix("RCSSWITCH", new OneArgsSuffix<Lexicon>(RcsSwitch,
                "Core Library and helper calls: RCS switched. Lexicon on (true/false), reason (turning, falling behind, big turn, no wheel torque, translating, aligned, steering released, block end or no monoprop), then any of error_deg (steering error), rate_dps (turn rate), wheels_s with left_s (the wheels' turn time against the seconds left to the deadline), monoprop with monoprop_max (units)"));
            // The Core Library's waits read this: true holds in-script warp at 1x while the Mission needs the player (ADR 0004).
            AddSuffix("WARPHOLD", new Suffix<BooleanValue>(() => ScriptWarpHold.Held, "In-script warp is held at 1x while the Mission needs the player"));
            // The Core Library's waits mark themselves for the warp trace (#335), which times the 1x inside them. Common calls,
            // like BLOCKSTART: a report to the mod, not a calculation (ADR 0006), so the Script Check allows them anywhere.
            AddSuffix("WAITSTART", new OneArgsSuffix<ScalarValue>(WaitStart, "A wait began; its warp must be over by this UT"));
            AddSuffix("WAITEND", new NoArgsVoidSuffix(WaitEnd, "The wait ended"));
            // The player lowered or stopped warp during this wait (#336, ADR 0008); the next WAITSTART resets it. Waits, and a
            // Custom Script's own loop, stop asking for warp once it is true, so the player is never overridden.
            AddSuffix("WARPCANCELLED", new Suffix<BooleanValue>(WarpCancelled, "The player lowered or stopped warp during the current wait"));
            // Which warp a wait may ask for now (#339): rails where KSP's own limits allow it, else physics warp where the
            // runtime gate would let it through (both switches on, the craft on a Coast), so a wait never trips the gate.
            AddSuffix("RAILSWARP", new Suffix<BooleanValue>(() => WarpTracing.RailsAllowed(), "KSP would allow rails warp above 1x now"));
            AddSuffix("PHYSICSWARP", new Suffix<BooleanValue>(PhysicsWarp, "Physics warp above 1x is allowed now: switched on and the craft on a Coast"));
            AddTerrainSuffixes();
            AddHeatSuffixes();
            AddToolSuffixes();
            AddBlockCallSuffixes();
            if (shared.KSPPart != null) ByPart[shared.KSPPart.persistentId] = this;
        }

        public SharedObjects Shared { get { return shared; } }

        public kOSProcessor Processor { get { return shared == null ? null : shared.Processor as kOSProcessor; } }

        /// <summary>The processor this instance belonged to is gone (scene change, part destroyed).</summary>
        public bool IsStale { get { return Processor == null || shared.Cpu == null || shared.KSPPart == null; } }

        public override BooleanValue Available()
        {
            return true;
        }

        /// <summary>The live addon on the part with this <c>persistentId</c>, or null.</summary>
        public static ClaudiaAddon ForPart(uint persistentId)
        {
            ClaudiaAddon addon;
            if (!ByPart.TryGetValue(persistentId, out addon)) return null;
            if (!addon.IsStale) return addon;
            ByPart.Remove(persistentId);
            return null;
        }

        /// <summary>
        /// For the <c>science</c> Building Block: the first science experiment on a part, judged where the
        /// vessel is now. <c>left</c> is the science still to gain from this experiment's subject (experiment,
        /// body, situation and biome); <c>tracked</c> says whether the game keeps score at all (it does not in
        /// Sandbox, where <c>left</c> is -1).
        /// </summary>
        private Lexicon Science(PartValue part)
        {
            var module = part == null ? null : part.Part.FindModulesImplementing<ModuleScienceExperiment>().FirstOrDefault();
            var vessel = part == null ? null : part.Part.vessel;
            var situation = vessel == null ? ExperimentSituations.SrfLanded : ScienceUtil.GetExperimentSituation(vessel);
            var definition = module == null ? null : ResearchAndDevelopment.GetExperiment(module.experimentID);
            var available = definition != null && vessel != null && definition.IsAvailableWhile(situation, vessel.mainBody);
            var biome = available && definition.BiomeIsRelevantWhile(situation)
                ? ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude)
                : "";
            var tracked = HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.Instance != null;
            double left = -1;
            if (available && tracked)
            {
                var subject = ResearchAndDevelopment.GetExperimentSubject(definition, situation, vessel.mainBody, biome, biome);
                left = subject == null ? -1 : Math.Max(0, subject.scienceCap - subject.science);
            }
            var result = new Lexicon();
            result.Add(new StringValue("experiment"), new StringValue(module == null ? "" : module.experimentID ?? ""));
            result.Add(new StringValue("available"), new BooleanValue(available));
            result.Add(new StringValue("tracked"), new BooleanValue(tracked));
            result.Add(new StringValue("left"), ScalarValue.Create(left));
            result.Add(new StringValue("situation"), new StringValue(situation.ToString()));
            result.Add(new StringValue("biome"), new StringValue(biome));
            return result;
        }

        private void Report(Lexicon result)
        {
            if (shared.KSPPart == null) return;
            var bridge = KosBridge.Instance;
            if (bridge != null) bridge.OnReport(shared.KSPPart.persistentId, KosValues.ToDictionary(result));
        }

        private void BlockStart(StringValue block)
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null) bridge.OnBlock(shared.KSPPart.persistentId, block.ToString(), null);
        }

        private void BlockResult(StringValue block, Lexicon result)
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null) bridge.OnBlock(shared.KSPPart.persistentId, block.ToString(), KosValues.ToDictionary(result));
        }

        private void BlockTrace(StringValue block, Lexicon state)
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null) bridge.OnBlockTrace(shared.KSPPart.persistentId, block.ToString(), KosValues.ToDictionary(state));
        }

        private void BlockNote(StringValue block, Lexicon state)
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null) bridge.OnBlockTrace(shared.KSPPart.persistentId, block.ToString(), KosValues.ToDictionary(state), false);
        }

        private void RcsSwitch(Lexicon values)
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null && values != null) bridge.OnRcsSwitch(shared.KSPPart.persistentId, KosValues.ToDictionary(values));
        }

        private void WaitStart(ScalarValue warpEnd)
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null && warpEnd != null) bridge.OnWait(shared.KSPPart.persistentId, warpEnd.GetDoubleValue());
        }

        private BooleanValue WarpCancelled()
        {
            var bridge = KosBridge.Instance;
            return shared.KSPPart != null && bridge != null && bridge.WarpCancelled(shared.KSPPart.persistentId);
        }

        /// <summary>The runtime gate's own inputs for a physics <c>WARP</c> above 1x (ADR 0008), read the same way.</summary>
        private BooleanValue PhysicsWarp()
        {
            var vessel = shared.KSPPart == null ? null : shared.KSPPart.vessel;
            return KosGates.PhysicsWarpAllowed && vessel != null && CoastGuard.OnCoast(vessel);
        }

        private void WaitEnd()
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null) bridge.OnWait(shared.KSPPart.persistentId, null);
        }
    }
}
