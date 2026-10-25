// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Settings: the API key is reachable during a Mission; Prepare to uninstall is not.</summary>
    public class SettingsRulesTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheApiKeyIsAlwaysShown(bool duringMission)
        {
            Assert.True(SettingsRules.ShowApiKey(duringMission));
        }

        [Fact]
        public void UninstallIsHiddenOnlyDuringAMission()
        {
            Assert.False(SettingsRules.ShowUninstall(true));
            Assert.True(SettingsRules.ShowUninstall(false));
        }

        [Fact]
        public void TheSubtitleOnlyNamesWhatIsShown()
        {
            Assert.Equal("API key", SettingsRules.Subtitle(true));
            Assert.Contains("Prepare to uninstall", SettingsRules.Subtitle(false));
        }
    }
}
