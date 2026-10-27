// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The craft's nose-first drag table in the Game Snapshot (#369), for plan_ascent.</summary>
    public class NoseDragTests
    {
        private static GameSnapshot WithTable()
        {
            var snapshot = new GameSnapshot();
            snapshot.Craft.Name = "Mun Shot";
            snapshot.Craft.NoseDrag.Add(new[] { 0.0, 1.2 });
            snapshot.Craft.NoseDrag.Add(new[] { 1.0, 2.9 });
            return snapshot;
        }

        [Fact]
        public void The_table_survives_the_recording_json()
        {
            var back = GameSnapshot.Parse(WithTable().ToJson());

            Assert.Equal(new List<double[]> { new[] { 0.0, 1.2 }, new[] { 1.0, 2.9 } }, back.Craft.NoseDrag);
        }

        [Fact]
        public void Claudias_facts_leave_the_table_out()
        {
            // Only Planning Tools read it, from the snapshot itself; it would only lengthen every turn.
            Assert.DoesNotContain("nose_drag", WithTable().ToJson(false));
        }

        [Fact]
        public void A_snapshot_recorded_before_the_table_parses_with_none()
        {
            var back = GameSnapshot.Parse("{\"craft\":{\"name\":\"Old\",\"parts\":[]}}");

            Assert.Equal("Old", back.Craft.Name);
            Assert.Empty(back.Craft.NoseDrag);
        }
    }
}
