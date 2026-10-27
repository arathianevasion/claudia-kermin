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
            // The Core Library's waits read this: true holds in-script warp at 1x while the Mission needs the player (ADR 0004).
            AddSuffix("WARPHOLD", new Suffix<BooleanValue>(() => ScriptWarpHold.Held, "In-script warp is held at 1x while the Mission needs the player"));
            // The Core Library's waits mark themselves for the warp trace (#335), which times the 1x inside them. Common calls,
            // like BLOCKSTART: a report to the mod, not a calculation (ADR 0006), so the Script Check allows them anywhere.
            AddSuffix("WAITSTART", new OneArgsSuffix<ScalarValue>(WaitStart, "A wait began; its warp must be over by this UT"));
            AddSuffix("WAITEND", new NoArgsVoidSuffix(WaitEnd, "The wait ended"));
            // The player lowered or stopped warp during this wait (#336, ADR 0008); the next WAITSTART resets it. Waits, and a
            // Custom Script's own loop, stop asking for warp once it is true, so the player is never overridden.
            AddSuffix("WARPCANCELLED", new Suffix<BooleanValue>(WarpCancelled, "The player lowered or stopped warp during the current wait"));
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

        private void WaitEnd()
        {
            var bridge = KosBridge.Instance;
            if (shared.KSPPart != null && bridge != null) bridge.OnWait(shared.KSPPart.persistentId, null);
        }
    }
}
