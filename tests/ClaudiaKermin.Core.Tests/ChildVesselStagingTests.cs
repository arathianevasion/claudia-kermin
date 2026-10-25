// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// What leaves the craft at each separation stage, from the part tree in a Game Snapshot (#65, #100): the facts the
    /// Craft Check judges a planned Child Vessel by, and a staging plan that strands Claudia without a processor.
    /// </summary>
    public sealed class ChildVesselStagingTests
    {
        private const uint Pod = 1, PodProcessor = 2, ProbeDecoupler = 3, ProbeCore = 4, ProbeTank = 5, ProbeEngine = 6, BoosterDecoupler = 7, Booster = 8;

        private static SnapshotPart Part(uint id, uint? parent, string title, int stage = 0, double mass = 0.1, double dry = -1, params string[] tags)
        {
            var part = new SnapshotPart { Id = id, ParentId = parent, Title = title, Name = title, Stage = stage, MassT = mass, DryMassT = dry < 0 ? mass : dry };
            part.Tags.AddRange(tags);
            part.HasKosProcessor = tags.Contains("kos_processor");
            return part;
        }

        /// <summary>
        /// A crewed pod with Claudia's processor, a probe below it on a decoupler fired in stage 2, and a booster on a
        /// decoupler fired in stage 4 (first). The probe: core (with a 5 km antenna and 10 EC), a tank of 1.0 t (0.2 t dry)
        /// and a 20 kN, 300 s engine of 0.5 t.
        /// </summary>
        private static GameSnapshot Mothership()
        {
            var snapshot = new GameSnapshot();
            var parts = snapshot.Craft.Parts;
            var pod = Part(Pod, null, "Mk1 Pod", mass: 0.8, tags: new[] { "command", "crew_seat" });
            pod.CrewCapacity = 1;
            parts.Add(pod);
            parts.Add(Part(PodProcessor, Pod, "KAL9000", tags: "kos_processor"));
            parts.Add(Part(ProbeDecoupler, PodProcessor, "TD-12 Decoupler", stage: 2, tags: "decoupler"));
            var core = Part(ProbeCore, ProbeDecoupler, "Probodobodyne OKTO", stage: 1, mass: 0.2, tags: new[] { "command", "probe_core", "antenna", "kos_processor" });
            core.AntennaPower = 5000;
            core.ChargeMax = 10;
            parts.Add(core);
            parts.Add(Part(ProbeTank, ProbeCore, "FL-T100", mass: 1.0, dry: 0.2));
            var engine = Part(ProbeEngine, ProbeTank, "Spark", stage: 1, mass: 0.5, tags: "engine");
            engine.ThrustVacKn = 20;
            engine.ThrustAslKn = 16;
            engine.IspVac = 300;
            parts.Add(engine);
            parts.Add(Part(BoosterDecoupler, Pod, "TD-25 Decoupler", stage: 4, tags: "decoupler"));
            parts.Add(Part(Booster, BoosterDecoupler, "Swivel", stage: 5, mass: 1.5, tags: "engine"));
            return snapshot;
        }

        [Fact]
        public void EachSeparationStageSaysWhatLeavesTheCraftAndTheProbesOwnFigures()
        {
            var plan = ChildVessels.Staging(Mothership(), remote: false, processorPartId: PodProcessor);

            Assert.Equal(new[] { 4, 2 }, plan.Separations.Select(s => s.Stage));
            var booster = plan.Separations[0];
            Assert.False(booster.Candidate);
            Assert.Equal(new uint[] { BoosterDecoupler, Booster }, booster.PartIds);

            var probe = plan.Separations[1];
            Assert.True(probe.Candidate);
            Assert.Equal("Probodobodyne OKTO", probe.Name);
            Assert.Equal(new uint[] { ProbeCore }, probe.ProcessorPartIds);
            Assert.Equal(5000, probe.AntennaPower);
            Assert.Equal(10, probe.ChargeMax);
            Assert.Equal(1.8, probe.MassT, 6);
            // 300 s x 9.80665 x ln(1.8 / 1.0): the propellant is the 0.8 t the tank holds.
            Assert.Equal(1729.3, probe.DeltaVVac, 1);
            Assert.Equal(20, probe.ThrustVacKn);
            Assert.Empty(probe.Problems());
            Assert.Equal("", plan.Strands);
        }

        [Fact]
        public void AProbeWithNoProcessorNoAntennaOrNoPowerHasProblems()
        {
            var snapshot = Mothership();
            var core = snapshot.Craft.Parts.Single(p => p.Id == ProbeCore);
            core.HasKosProcessor = false;
            core.Tags.Remove("kos_processor");
            core.AntennaPower = 0;
            core.Tags.Remove("antenna");
            core.ChargeMax = 0;

            var probe = ChildVessels.Staging(snapshot, false, PodProcessor).Separations.Single(s => s.Stage == 2);
            Assert.True(probe.Candidate);
            Assert.Equal(new[] { "no kOS processor", "no antenna to reach Claudia", "no electric charge (no battery and nothing generating)" }, probe.Problems());

            // A generator is power enough without a battery.
            snapshot.Craft.Parts.Add(Part(20, ProbeCore, "OX-STAT", tags: "electric_generator"));
            Assert.DoesNotContain(ChildVessels.Staging(snapshot, false, PodProcessor).Separations.Single(s => s.Stage == 2).Problems(), p => p.Contains("electric"));
        }

        [Fact]
        public void AStagingPlanThatTakesClaudiasProcessorAwayFromHerSeatStrandsTheCraft()
        {
            var snapshot = Mothership();
            // Her only processor rides on the booster: once it goes in stage 4, her pod has none.
            snapshot.Craft.Parts.RemoveAll(p => p.Id == PodProcessor);
            snapshot.Craft.Parts.Single(p => p.Id == ProbeDecoupler).ParentId = Pod;
            snapshot.Craft.Parts.Add(Part(30, Booster, "KAL9000", stage: 5, tags: "kos_processor"));
            snapshot.Craft.Parts.Single(p => p.Id == ProbeCore).HasKosProcessor = false;

            var plan = ChildVessels.Staging(snapshot, false, 30);
            Assert.Equal("stage 4 separates Swivel with Claudia's kOS processor, leaving Mk1 Pod (where she sits) without one", plan.Strands);

            // Remote, the craft is wherever the processor goes: nothing is stranded.
            Assert.Equal("", ChildVessels.Staging(snapshot, true, 30).Strands);
        }

        [Fact]
        public void TheNewPartFactsRoundTripThroughTheSnapshotJson()
        {
            var parsed = GameSnapshot.Parse(Mothership().ToJson());
            var engine = parsed.Craft.Parts.Single(p => p.Id == ProbeEngine);
            Assert.Equal(0.5, engine.MassT);
            Assert.Equal(20, engine.ThrustVacKn);
            Assert.Equal(16, engine.ThrustAslKn);
            Assert.Equal(300, engine.IspVac);
            var tank = parsed.Craft.Parts.Single(p => p.Id == ProbeTank);
            Assert.Equal(0.2, tank.DryMassT);
            Assert.Equal(10, parsed.Craft.Parts.Single(p => p.Id == ProbeCore).ChargeMax);
        }
    }
}
