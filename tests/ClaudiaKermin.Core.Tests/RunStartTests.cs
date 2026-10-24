// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class RunStartTests
    {
        private static ProcessorState Ready()
        {
            return new ProcessorState { Found = true, Powered = true, HasUpdated = true, Idle = true };
        }

        [Fact]
        public void AnIdlePoweredProcessorStarts()
        {
            Assert.Equal(StartDecisionKind.Start, RunStart.Decide(KosCompatibility.Compatible, Ready(), 0).Kind);
        }

        [Fact]
        public void ABusyProcessorIsRefusedAndSaysWhy()
        {
            var state = Ready();
            state.Idle = false;

            var decision = RunStart.Decide(KosCompatibility.Compatible, state, 0);

            Assert.Equal(StartDecisionKind.Refuse, decision.Kind);
            Assert.Contains("busy", decision.Reason);
        }

        [Fact]
        public void AModRunAlreadyGoingIsRefused()
        {
            var state = Ready();
            state.ModRunActive = true;

            Assert.Equal(StartDecisionKind.Refuse, RunStart.Decide(KosCompatibility.Compatible, state, 0).Kind);
        }

        [Fact]
        public void WaitsForTheProcessorsFirstUpdateThenGivesUp()
        {
            var state = Ready();
            state.HasUpdated = false;

            Assert.Equal(StartDecisionKind.Wait, RunStart.Decide(KosCompatibility.Compatible, state, 2).Kind);
            var late = RunStart.Decide(KosCompatibility.Compatible, state, RunStart.MaxWaitSeconds + 1);
            Assert.Equal(StartDecisionKind.Refuse, late.Kind);
            Assert.Contains("didn't start", late.Reason);
        }

        [Fact]
        public void NoProcessorOrNoPowerIsRefused()
        {
            var missing = Ready();
            missing.Found = false;
            var unpowered = Ready();
            unpowered.Powered = false;

            Assert.Contains("no kOS processor", RunStart.Decide(KosCompatibility.Compatible, missing, 0).Reason);
            Assert.Contains("power", RunStart.Decide(KosCompatibility.Compatible, unpowered, 0).Reason);
        }

        [Fact]
        public void IncompatibleKosIsRefusedBeforeAnythingElse()
        {
            var kos = KosCompatibility.FromMissing(new[] { "kOS.Safe.Execution.CPU.BreakExecution" });

            var decision = RunStart.Decide(kos, Ready(), 0);

            Assert.Equal(StartDecisionKind.Refuse, decision.Kind);
            Assert.Equal(kos.Message, decision.Reason);
        }
    }
}
