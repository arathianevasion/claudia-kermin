// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>#234: stock delta-v reads 0 while an engine is lit at a trickle throttle; that is unknown, not a real 0.</summary>
    public sealed class TrickleThrottleDeltaVTests
    {
        private static GameSnapshot Climbing(bool unreliable)
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "FLYING", DeltaVSimulator = true, Throttle = unreliable ? 0.0006 : 1 };
            snapshot.Stages.Add(new SnapshotStage { Number = 2, DeltaVVac = unreliable ? 0 : 1472, DeltaVUnreliable = unreliable });
            snapshot.Stages.Add(new SnapshotStage { Number = 0, DeltaVVac = 0 });
            return snapshot;
        }

        [Fact]
        public void An_unreliable_stage_gives_no_delta_v_left_used_or_margin()
        {
            var readings = LiveReadings.Read(Climbing(true), 165, 4058, 3400, 0);
            Assert.Null(LiveReadings.DeltaVLeftOf(Climbing(true)));
            Assert.Null(readings.DeltaVLeft);
            Assert.Null(readings.DeltaVUsed);
            Assert.Null(readings.FuelMarginPercent);
        }

        [Fact]
        public void No_delta_v_check_fires_on_an_unreliable_reading()
        {
            var readings = LiveReadings.Read(Climbing(true), 165, 4058, 3400, 0);
            foreach (var quantity in new[] { TelemetryQuantity.DeltaVLeft, TelemetryQuantity.DeltaVUsed, TelemetryQuantity.FuelMargin })
                Assert.Null(readings.Of(new TelemetryCheck { Quantity = quantity }));
        }

        [Fact]
        public void The_starting_delta_v_is_not_taken_from_an_unreliable_reading()
        {
            Assert.Null(LiveReadings.DeltaVLeftOf(Climbing(true)));
        }

        [Fact]
        public void A_normal_snapshot_reads_as_before()
        {
            var readings = LiveReadings.Read(Climbing(false), 165, 4058, 3400, 0);
            Assert.Equal(1472, readings.DeltaVLeft);
            Assert.Equal(2586, readings.DeltaVUsed);
            Assert.NotNull(readings.FuelMarginPercent);
        }

        [Fact]
        public void The_snapshot_json_says_the_delta_v_is_unreliable_and_round_trips()
        {
            var json = Climbing(true).ToJson(false);
            Assert.Contains("\"dv_reading\":\"unreliable at low throttle", json);
            Assert.Contains("\"dv_unreliable\":true", json);
            var parsed = GameSnapshot.Parse(json);
            Assert.True(parsed.DeltaVUnreliable);
            Assert.Equal(0.0006, parsed.Throttle);
            Assert.DoesNotContain("dv_reading", Climbing(false).ToJson(false));
        }
    }
}
