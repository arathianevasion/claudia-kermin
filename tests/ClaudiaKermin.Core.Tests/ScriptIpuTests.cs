// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The kOS IPU while a Mission Script is Running (#347, ADR 0008): when to raise it and when to put the player's value back.</summary>
    public class ScriptIpuTests
    {
        [Fact]
        public void ARunStartingRaisesTheIpuAndKeepsThePlayersValue()
        {
            var ipu = new ScriptIpu(1000);
            var action = ipu.RunStarted(200);
            Assert.Equal(1000, action.SetTo);
            Assert.Equal(200, action.Persist);
            Assert.False(action.ClearPersisted);
            Assert.Contains("1000", action.Log);
            Assert.Contains("200", action.Log);
        }

        [Theory]
        [InlineData(IpuEnd.Ended)]
        [InlineData(IpuEnd.Interrupted)]
        [InlineData(IpuEnd.ManualControl)]
        public void ThePlayersValueIsPutBackWhenTheRunEnds(IpuEnd how)
        {
            var ipu = new ScriptIpu(1000);
            ipu.RunStarted(200);
            var action = ipu.RunEnded(how);
            Assert.Equal(200, action.SetTo);
            Assert.True(action.ClearPersisted);
            Assert.Null(action.Persist);
            Assert.Contains("200", action.Log);
        }

        [Fact]
        public void AnEndWithNothingRaisedDoesNothing()
        {
            var action = new ScriptIpu(1000).RunEnded(IpuEnd.Ended);
            Assert.Null(action.SetTo);
            Assert.False(action.ClearPersisted);
        }

        [Fact]
        public void TwoRunsAtOnceKeepItRaisedUntilTheLastEnds()
        {
            var ipu = new ScriptIpu(1000);
            ipu.RunStarted(200);
            var second = ipu.RunStarted(1000);
            Assert.Null(second.SetTo);
            Assert.Null(second.Persist);
            Assert.Null(ipu.RunEnded(IpuEnd.Ended).SetTo);
            Assert.Equal(200, ipu.RunEnded(IpuEnd.Interrupted).SetTo);
        }

        [Fact]
        public void ARestartAfterARestoreSavesTheNewCurrentValue()
        {
            var ipu = new ScriptIpu(1000);
            ipu.RunStarted(200);
            ipu.RunEnded(IpuEnd.Ended);
            Assert.Equal(150, ipu.RunStarted(150).Persist);
        }

        [Fact]
        public void AnOutcomeSaysHowTheRunEnded()
        {
            Assert.Equal(IpuEnd.Interrupted, ScriptIpu.EndOf(new RunOutcome(RunOutcomeKind.Interrupted, null, "", "left Flight", false, null)));
            Assert.Equal(IpuEnd.ManualControl, ScriptIpu.EndOf(new RunOutcome(RunOutcomeKind.Aborted, null, "", "the player took Manual Control", true, null)));
            Assert.Equal(IpuEnd.Ended, ScriptIpu.EndOf(new RunOutcome(RunOutcomeKind.Aborted, null, "", "player pressed Abort", true, null)));
            Assert.Equal(IpuEnd.Ended, ScriptIpu.EndOf(new RunOutcome(RunOutcomeKind.Finished, null, "", null, false, null)));
        }

        [Fact]
        public void LoadingAfterACrashPutsThePersistedValueBack()
        {
            var action = new ScriptIpu(1000).Loaded(200);
            Assert.Equal(200, action.SetTo);
            Assert.True(action.ClearPersisted);
            Assert.Contains("200", action.Log);
        }

        [Fact]
        public void LoadingWithNothingPersistedDoesNothing()
        {
            var action = new ScriptIpu(1000).Loaded(null);
            Assert.Null(action.SetTo);
            Assert.False(action.ClearPersisted);
        }

        [Theory]
        [InlineData(10, 50)]
        [InlineData(50, 50)]
        [InlineData(1000, 1000)]
        [InlineData(2000, 2000)]
        [InlineData(5000, 2000)]
        public void TheScriptIpuIsClampedToKosRange(int asked, int used)
        {
            Assert.Equal(used, ScriptIpu.Clamp(asked));
            Assert.Equal(used, new ScriptIpu(asked).RunStarted(100).SetTo);
        }
    }
}
