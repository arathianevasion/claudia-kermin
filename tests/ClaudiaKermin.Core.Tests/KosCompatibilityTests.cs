// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class KosCompatibilityTests
    {
        [Fact]
        public void NothingMissingIsCompatible()
        {
            var kos = KosCompatibility.FromMissing(new string[0]);

            Assert.True(kos.IsCompatible);
            Assert.Empty(kos.Missing);
        }

        [Fact]
        public void AnythingMissingIsIncompatibleAndNamesWhatIsMissing()
        {
            var kos = KosCompatibility.FromMissing(new[] { "kOS.Safe.Execution.CPU.BreakExecution", "kOS.Screen.Interpreter.IsWaitingForCommand" });

            Assert.False(kos.IsCompatible);
            Assert.Equal(2, kos.Missing.Count);
            Assert.Equal(
                "kOS is incompatible: Claudia Kermin can't find kOS internals it needs " +
                "(kOS.Safe.Execution.CPU.BreakExecution, kOS.Screen.Interpreter.IsWaitingForCommand), " +
                "so Claudia won't run scripts. Install kOS 1.6.",
                kos.Message);
        }
    }
}
