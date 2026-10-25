// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class MissionListRulesTests
    {
        [Theory]
        [InlineData(MissionStatus.Active, "Active", ChipTone.Info)]
        [InlineData(MissionStatus.Paused, "Paused", ChipTone.Attention)]
        [InlineData(MissionStatus.Succeeded, "Succeeded", ChipTone.Success)]
        [InlineData(MissionStatus.Ended, "Ended", ChipTone.Danger)]
        [InlineData(MissionStatus.Lost, "Lost", ChipTone.Danger)]
        public void EveryStatusHasItsChip(MissionStatus status, string text, ChipTone tone)
        {
            var chip = MissionListRules.Status(new MissionListEntry { Status = status });
            Assert.Equal(text, chip.Text);
            Assert.Equal(tone, chip.Tone);
        }

        [Fact]
        public void CostIsReadAgainstTheSpendingCap()
        {
            Assert.Equal("$1.50 of $10.00", MissionListRules.Cost(new MissionListEntry { Cost = 1.5m, SpendingCap = 10m }));
        }

        [Fact]
        public void AFinishedMissionNamesItsFinalDebriefOrSaysItHasNone()
        {
            Assert.Equal("Debrief: Nominal", MissionListRules.DebriefTitle(new MissionListEntry { Status = MissionStatus.Succeeded, FinalDebrief = new LogEntry { Title = "Debrief: Nominal" } }));
            Assert.Equal("No Debrief", MissionListRules.DebriefTitle(new MissionListEntry { Status = MissionStatus.Ended }));
            Assert.Equal("", MissionListRules.DebriefTitle(new MissionListEntry { Status = MissionStatus.Paused }));
        }
    }
}
