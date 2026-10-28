// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The encounter search's expected failure (#439): running out of steps comes back as <c>error</c> text, never an exception,
    /// so a block call has a reason to fail with.
    /// </summary>
    public sealed class EncounterSearchTests
    {
        private static GameSnapshot Snapshot()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "kerbin-orbit-100km.json")));
        }

        private static void Search(int maxSteps, out double? found, out string error, out Patch patch)
        {
            var snapshot = Snapshot();
            var system = new BodySystem(snapshot.Bodies, maxSteps);
            var kerbin = system.Find("Kerbin");
            var orbit = Conic.FromState(kerbin.Gm, new Vec3(700000, 0, 0), new Vec3(0, 2300, 0), 0);
            SnapshotBody hit;
            found = system.FirstEncounter(kerbin, orbit, 0, double.PositiveInfinity, null, out hit, out error);
            patch = system.Follow(kerbin, orbit, 0, 3).Last();
        }

        [Fact]
        public void A_search_that_exceeds_its_steps_gives_error_text_and_no_exception()
        {
            double? found;
            string error;
            Patch patch;
            Search(1, out found, out error, out patch);
            Assert.Null(found);
            Assert.Equal("the encounter search took too many steps", error);
            Assert.Equal("the encounter search took too many steps", patch.Error);
        }

        [Fact]
        public void A_search_within_its_steps_has_no_error()
        {
            double? found;
            string error;
            Patch patch;
            Search(200000, out found, out error, out patch);
            Assert.Null(error);
            Assert.Null(patch.Error);
        }
    }
}
