// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class SeaLevelThrustTests
    {
        private static SnapshotPart Engine(int stage, double vac, double ispVac, params double[][] curve)
        {
            var part = new SnapshotPart { Stage = stage, ThrustVacKn = vac, IspVac = ispVac };
            part.IspCurve.AddRange(curve);
            return part;
        }

        [Fact]
        public void AnEnginesThrustFollowsItsIspCurveAtTheBodysSeaLevelPressure()
        {
            var engine = Engine(1, 200, 320, new[] { 0.0, 320 }, new[] { 1.0, 280 }, new[] { 4.0, 160 });

            Assert.Equal(200, SeaLevelThrust.Of(engine, 0));
            Assert.Equal(175, SeaLevelThrust.Of(engine, 1), 6);
            Assert.Equal(137.5, SeaLevelThrust.Of(engine, 2.5), 6);
            Assert.Equal(100, SeaLevelThrust.Of(engine, 9), 6);
        }

        [Fact]
        public void WithoutACurveItFallsBackToTheOneAtmosphereThrust()
        {
            var engine = Engine(1, 200, 320);
            engine.ThrustAslKn = 150;

            Assert.Equal(150, SeaLevelThrust.Of(engine, 1), 6);
            Assert.Equal(175, SeaLevelThrust.Of(engine, 0.5), 6);
        }

        [Fact]
        public void AStagesSeaLevelThrustScalesTheSimulatorsVacuumThrustByItsEnginesFraction()
        {
            var snapshot = new GameSnapshot();
            snapshot.Craft.Parts.Add(Engine(2, 100, 300, new[] { 0.0, 300 }, new[] { 1.0, 150 }));
            snapshot.Craft.Parts.Add(Engine(0, 50, 300, new[] { 0.0, 300 }, new[] { 1.0, 270 }));
            var first = new SnapshotStage { Number = 2, ThrustVacKn = 100 };
            var last = new SnapshotStage { Number = 0, ThrustVacKn = 150 };

            Assert.Equal(50, SeaLevelThrust.ForStage(snapshot, first, 1), 6);
            Assert.Equal(95, SeaLevelThrust.ForStage(snapshot, last, 1), 6);
            Assert.Equal(150, SeaLevelThrust.ForStage(snapshot, last, 0));
        }

        [Fact]
        public void TheSameCraftGivesDifferentLaunchTwrOnDifferentBodiesWhateverTheDeltaVAppHasSelected()
        {
            var snapshot = new GameSnapshot();
            snapshot.Craft.Parts.Add(Engine(0, 100, 300, new[] { 0.0, 300 }, new[] { 1.0, 150 }));
            var stage = new SnapshotStage { Number = 0, ThrustVacKn = 100, StartMassT = 5 };
            var kerbin = new SnapshotBody { Name = "Kerbin", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1 };
            var duna = new SnapshotBody { Name = "Duna", Gm = 3.0136e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.2 };
            var mun = new SnapshotBody { Name = "Mun", Gm = 6.5138e10, Radius = 200000 };

            Assert.Equal(50 / (5 * kerbin.SurfaceGravity), CraftCheckPrompts.ThrustOverWeight(snapshot, stage, kerbin), 6);
            Assert.Equal(90 / (5 * duna.SurfaceGravity), CraftCheckPrompts.ThrustOverWeight(snapshot, stage, duna), 6);
            Assert.Equal(100 / (5 * mun.SurfaceGravity), CraftCheckPrompts.ThrustOverWeight(snapshot, stage, mun), 6);
        }

        [Fact]
        public void TheCurveAndTheBodysPressureSurviveTheSnapshotJson()
        {
            var snapshot = new GameSnapshot();
            snapshot.Craft.Parts.Add(Engine(0, 100, 300, new[] { 0.0, 300 }, new[] { 1.0, 150 }));
            snapshot.Bodies.Add(new SnapshotBody { Name = "Duna", SeaLevelAtm = 0.2 });

            var back = GameSnapshot.Parse(snapshot.ToJson(true));

            Assert.Equal(new List<double[]> { new[] { 0.0, 300 }, new[] { 1.0, 150 } }, back.Craft.Parts[0].IspCurve);
            Assert.Equal(0.2, back.Bodies[0].SeaLevelAtm);
        }
    }
}
