// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
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
    public class ClaudiaAddon : kOS.Suffixed.Addon
    {
        private static readonly Dictionary<uint, ClaudiaAddon> ByPart = new Dictionary<uint, ClaudiaAddon>();

        public ClaudiaAddon(SharedObjects shared) : base(shared)
        {
            AddSuffix("REPORT", new OneArgsSuffix<Lexicon>(Report, "Report a Claudia script's result"));
            AddSuffix("SCIENCE", new OneArgsSuffix<Lexicon, PartValue>(Science, "What an experiment part can still earn here: lexicon experiment, available, tracked, left, situation, biome"));
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
                left = Math.Max(0, subject.scienceCap - subject.science);
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
    }
}
