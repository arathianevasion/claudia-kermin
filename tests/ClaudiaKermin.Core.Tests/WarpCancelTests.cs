// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The player's warp cancel (#336, ADR 0008): ADDONS:CLAUDIA:WARPCANCELLED and who a rate change belongs to.</summary>
    public class WarpCancelTests
    {
        [Fact]
        public void ThePlayerLoweringWarpMidWaitCancelsIt()
        {
            var cancel = new WarpCancel();
            cancel.WaitStarted();
            Assert.True(cancel.RateChanged(1000, 100, byPlayer: true));
            Assert.True(cancel.Cancelled);
        }

        [Fact]
        public void ThePlayerStoppingPhysicsWarpCancelsIt()
        {
            var cancel = new WarpCancel();
            cancel.WaitStarted();
            Assert.True(cancel.RateChanged(3, 1, byPlayer: true));
            Assert.True(cancel.Cancelled);
        }

        [Fact]
        public void ThePlayerRaisingWarpIsNotACancel()
        {
            var cancel = new WarpCancel();
            cancel.WaitStarted();
            Assert.False(cancel.RateChanged(100, 1000, byPlayer: true));
            Assert.False(cancel.Cancelled);
        }

        [Fact]
        public void KspsOwnDropIsNotACancel()
        {
            var cancel = new WarpCancel();
            cancel.WaitStarted();
            Assert.False(cancel.RateChanged(1000, 1, byPlayer: false));
            Assert.False(cancel.Cancelled);
        }

        [Fact]
        public void ACancelHoldsForTheRestOfTheWaitAndTheNextWaitStartsFresh()
        {
            var cancel = new WarpCancel();
            cancel.WaitStarted();
            cancel.RateChanged(1000, 1, byPlayer: true);
            // The player raising it again, or the script asking, doesn't undo the cancel.
            cancel.RateChanged(1, 100, byPlayer: true);
            cancel.RateChanged(100, 1000, byPlayer: false);
            Assert.True(cancel.Cancelled);
            cancel.WaitStarted();
            Assert.False(cancel.Cancelled);
        }

        [Fact]
        public void ACancelOutsideAWaitDoesntCountButOneStaysReadableAfterItsWaitEnds()
        {
            var cancel = new WarpCancel();
            Assert.False(cancel.RateChanged(1000, 1, byPlayer: true));
            Assert.False(cancel.Cancelled);
            cancel.WaitStarted();
            cancel.RateChanged(1000, 1, byPlayer: true);
            cancel.WaitEnded();
            Assert.True(cancel.Cancelled);
        }

        [Fact]
        public void APlayersRaiseThatKspClampedLowerIsNotACancel()
        {
            var cancel = new WarpCancel();
            cancel.WaitStarted();
            // The player pressed '.' at 100x; KSP's altitude limit only allows 10x.
            Assert.False(cancel.RateChanged(100, 10, byPlayer: true, askedHigher: true));
            Assert.False(cancel.Cancelled);
        }

        // ---- whose change it was ----

        [Theory]
        [InlineData(WarpChangeSource.PlayerControl, false, true)]
        [InlineData(WarpChangeSource.KspClamp, false, false)]
        [InlineData(WarpChangeSource.KspClamp, true, false)]
        [InlineData(WarpChangeSource.Other, false, false)]
        [InlineData(WarpChangeSource.Other, true, false)]
        // A warp-to's own steps (its arrival, a kOS WARP under it) aren't the player's, unless a warp key is down: its coroutine reads them.
        [InlineData(WarpChangeSource.WarpToStep, false, false)]
        [InlineData(WarpChangeSource.WarpToStep, true, true)]
        public void AChangeIsThePlayersFromTheirControlsOrAWarpKeyDuringAWarpTo(WarpChangeSource source, bool warpKeyDown, bool expected)
        {
            Assert.Equal(expected, PlayerWarp.IsPlayers(source, warpKeyDown));
        }

        // ---- the warp trace ----

        /// <summary>A wait from UT 1000 to 9000 that asked for warp and reached 1000x.</summary>
        private static WarpTrace Warping()
        {
            var trace = new WarpTrace(0);
            trace.BlockStarted("coast", 0);
            trace.WaitStarted(0, 1000, 9000);
            trace.Requested(1, 1001, 9000, "rails", null, "");
            trace.Sample(2, 1100, 1000);
            return trace;
        }

        [Fact]
        public void APlayersStopReadsPlayerInTheTraceAndTheFallIsntReportedAgain()
        {
            var trace = Warping();
            trace.PlayerDropped(3, 2000, 1000, 1);
            // KSP lerps the rate down over the next frames; the fall is the player's, already named.
            Assert.False(trace.Sample(3, 2000, 400));
            Assert.False(trace.Sample(4, 2100, 1));

            var drop = Assert.Single(trace.Entries, e => e.Kind == WarpTraceKind.Drop);
            Assert.Equal("player", drop.Drop);
            Assert.Equal(1000, drop.RateReached);
            Assert.Equal(1, drop.RateTo);
            Assert.Equal("drop (player) real 3.0 s, UT 2000, from 1000x, in coast", trace.Lines()[1]);
            Assert.Equal(1, trace.Blocks[0].Drops);
        }

        [Fact]
        public void APlayersLoweringShowsItsRateAndAKspCutAfterItIsStillACut()
        {
            var trace = Warping();
            trace.PlayerDropped(3, 2000, 1000, 100);
            Assert.False(trace.Sample(3, 2000, 100));
            // Then KSP cuts the 100x the player left: the caller names that one.
            Assert.True(trace.Sample(4, 2100, 1));
            trace.Dropped(4, 2100, "cut", "");

            Assert.Equal(new[] { "player", "cut" }, trace.Entries.Where(e => e.Kind == WarpTraceKind.Drop).Select(e => e.Drop));
            Assert.Equal("drop (player) real 3.0 s, UT 2000, from 1000x, to 100x, in coast", trace.Lines()[1]);
            Assert.Equal("drop (cut) real 4.0 s, UT 2100, from 100x, in coast", trace.Lines()[2]);
        }

        [Fact]
        public void APlayersDropOutsideAWaitIsntTraced()
        {
            var trace = new WarpTrace(0);
            trace.PlayerDropped(3, 2000, 1000, 1);
            Assert.Empty(trace.Entries);
        }
    }
}
