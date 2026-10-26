// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public sealed class SnapshotOrbitElementsTests
    {
        private static GameSnapshot WithElements()
        {
            var snapshot = new GameSnapshot { Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                Apoapsis = 100000, Periapsis = 90000, Inclination = 12, Eccentricity = 0.01, Period = 1900, SemiMajorAxis = 695000,
                Lan = 45.5, ArgumentOfPeriapsis = 120.25, MeanAnomalyAtEpoch = 1.75, Epoch = 86400, Body = "Kerbin",
            };
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38,
                Sma = 12000000, Eccentricity = 0, Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0, MeanAnomalyAtEpoch = 1.7, Epoch = 0,
            });
            return snapshot;
        }

        [Fact]
        public void TheCraftOrbitElementsRoundTripThroughTheSnapshotJson()
        {
            var again = GameSnapshot.Parse(WithElements().ToJson());
            Assert.Equal(45.5, again.Orbit.Lan);
            Assert.Equal(120.25, again.Orbit.ArgumentOfPeriapsis);
            Assert.Equal(1.75, again.Orbit.MeanAnomalyAtEpoch);
            Assert.Equal(86400, again.Orbit.Epoch);
            Assert.Equal("Kerbin", again.Orbit.Body);
            Assert.Equal(WithElements().ToJson(), again.ToJson());
        }

        [Fact]
        public void TheBodyConstantsRoundTripInTheRecordingButNotThePerTurnSnapshot()
        {
            var again = GameSnapshot.Parse(WithElements().ToJson());
            Assert.Equal(2429559.1, again.Bodies[0].SoiRadius);
            Assert.Equal(138984.38, again.Bodies[0].RotationPeriod);
            Assert.Equal(1.7, again.Bodies[0].MeanAnomalyAtEpoch);
            Assert.DoesNotContain("soi_radius", WithElements().ToJson(false));
        }

        [Fact]
        public void AnOlderRecordedSnapshotWithoutTheNewFieldsStillLoads()
        {
            var old = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            Assert.NotNull(old.Orbit);
            Assert.Equal(0, old.Orbit.Lan);
            Assert.Equal(0, old.Orbit.Epoch);
            Assert.Equal("", old.Orbit.Body);
            Assert.Equal(0, old.Bodies[0].SoiRadius);
        }

        [Fact]
        public void TheBodyTableCarriesEveryConstantAndIsEmptyWithoutBodies()
        {
            var table = MissionPrompts.BodyTable(WithElements().Bodies);
            Assert.Contains("\"soi_radius\":2429559.1", table);
            Assert.Contains("\"rotation_period\":138984.38", table);
            Assert.Contains("\"mean_anomaly_at_epoch\":1.7", table);
            Assert.Equal("", MissionPrompts.BodyTable(null));
            Assert.Equal("", MissionPrompts.BodyTable(new GameSnapshot().Bodies));
        }
    }
}
