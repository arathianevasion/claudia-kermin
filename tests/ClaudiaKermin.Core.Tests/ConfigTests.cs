// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claudia-test-" + Guid.NewGuid().ToString("N"));
        public string Sub { get { return System.IO.Path.Combine(Path, "ClaudiaKermin"); } }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    public class ConfigTests
    {
        private static Func<string, string> Env(string key = null)
        {
            return name => name == "ANTHROPIC_API_KEY" ? key : null;
        }

        [Fact]
        public void EnvironmentKeyBeatsStoredKey()
        {
            using (var dir = new TempDir())
            {
                new ApiKeyStore(dir.Sub, Env()).Save("sk-from-file-1111");
                var store = new ApiKeyStore(dir.Sub, Env("sk-from-env-2222"));

                Assert.Equal("sk-from-env-2222", store.Load());
                Assert.True(store.UsingEnvironmentKey);
                Assert.Equal("sk-from-file-1111", store.LoadStored());
            }
        }

        [Fact]
        public void StoredKeyUsedWhenEnvironmentUnsetOrBlank()
        {
            using (var dir = new TempDir())
            {
                new ApiKeyStore(dir.Sub, Env()).Save("  sk-from-file-1111\n");
                Assert.Equal("sk-from-file-1111", new ApiKeyStore(dir.Sub, Env()).Load());
                Assert.Equal("sk-from-file-1111", new ApiKeyStore(dir.Sub, Env("   ")).Load());
            }
        }

        [Fact]
        public void NoKeyAnywhereLoadsNull()
        {
            using (var dir = new TempDir())
                Assert.Null(new ApiKeyStore(dir.Sub, Env()).Load());
        }

        [Fact]
        public void KeyFileAndFolderAreOwnerOnly()
        {
            if (OperatingSystem.IsWindows()) return;
            using (var dir = new TempDir())
            {
                var store = new ApiKeyStore(dir.Sub, Env());
                store.Save("sk-secret-1234");
                store.Save("sk-secret-5678"); // overwriting keeps the mode

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir.Sub));
            }
        }

        [Fact]
        public void SavingAnEmptyKeyIsRejected()
        {
            using (var dir = new TempDir())
                Assert.Throws<ArgumentException>(() => new ApiKeyStore(dir.Sub, Env()).Save("   "));
        }

        [Fact]
        public void MaskShowsOnlyTheLastFourCharacters()
        {
            Assert.Equal("****abcd", ApiKeyStore.Mask("sk-ant-api03-SECRET-abcd"));
            Assert.Equal("", ApiKeyStore.Mask(null));
            Assert.DoesNotContain("SECRET", ApiKeyStore.Mask("sk-ant-api03-SECRET-abcd"));
        }

        [Fact]
        public void ConfigFolderIsResolvedPerOs()
        {
            Assert.Equal("/Users/a/Library/Application Support/ClaudiaKermin",
                ConfigDirectory.Resolve(HostOs.MacOS, "/Users/a", n => null));
            Assert.Equal("/home/a/.config/ClaudiaKermin",
                ConfigDirectory.Resolve(HostOs.Linux, "/home/a", n => null));
            Assert.Equal("/x/cfg/ClaudiaKermin",
                ConfigDirectory.Resolve(HostOs.Linux, "/home/a", n => n == "XDG_CONFIG_HOME" ? "/x/cfg" : null));
            Assert.Equal(Path.Combine("C:\\roaming", "ClaudiaKermin"),
                ConfigDirectory.Resolve(HostOs.Windows, "C:\\u", n => n == "APPDATA" ? "C:\\roaming" : null));
        }

        [Fact]
        public void PlayerConfigIsCreatedWithDebugToolsOff()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);

                Assert.False(config.DebugTools);
                Assert.Contains("debug_tools = false", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
            }
        }

        [Fact]
        public void PlayerConfigReadsDebugToolsOn()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "# c\ndebug_tools = true\n");
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DebugTools);
            }
        }

        [Fact]
        public void PlayerConfigOffersSonnetFirstThenOpusWithATenDollarCap()
        {
            using (var dir = new TempDir())
            {
                PlayerConfig.LoadOrCreate(dir.Sub);
                var config = PlayerConfig.LoadOrCreate(dir.Sub);

                Assert.Equal(new[] { "claude-sonnet-5-5", "claude-opus-5-5" }, config.Models);
                Assert.Equal("claude-sonnet-5-5", config.DefaultModel);
                Assert.Equal(10m, config.DefaultSpendingCap);
                Assert.Equal(150000, config.CompactionTriggerTokens);
            }
        }

        [Fact]
        public void PlayerConfigReadsModelsCapAndCompactionTriggerAndIgnoresBadValues()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName),
                    "models = claude-opus-5-5 , claude-sonnet-5-5\ndefault_spending_cap = 4.50\ncompaction_trigger = 80000\n");
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal("claude-opus-5-5", config.DefaultModel);
                Assert.Equal(4.5m, config.DefaultSpendingCap);
                Assert.Equal(80000, config.CompactionTriggerTokens);

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "models = ,\ndefault_spending_cap = -1\ncompaction_trigger = lots\n");
                config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal("claude-sonnet-5-5", config.DefaultModel);
                Assert.Equal(10m, config.DefaultSpendingCap);
                Assert.Equal(150000, config.CompactionTriggerTokens);
            }
        }

        [Fact]
        public void PlayerConfigReadsTheWaitCapsWithOneOrbitAndThreeHoursOnTheGroundByDefault()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(1.0, config.WaitCapOrbits);
                Assert.Equal(10800.0, config.WaitCapGroundSeconds);
                Assert.Contains("wait_cap_orbits = 1", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "wait_cap_orbits = 2.5\nwait_cap_ground_s = 3600\n");
                config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(2.5, config.WaitCapOrbits);
                Assert.Equal(3600.0, config.WaitCapGroundSeconds);

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "wait_cap_orbits = 0\nwait_cap_ground_s = soon\n");
                config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(1.0, config.WaitCapOrbits);
                Assert.Equal(10800.0, config.WaitCapGroundSeconds);
            }
        }

        [Fact]
        public void PlayerConfigReadsTheRunawayMultiplierWithThreeHundredPercentByDefault()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(3.0, config.RunawayMultiplier);
                Assert.Contains("runaway_multiplier = 300%", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "runaway_multiplier = 450%\n");
                Assert.Equal(4.5, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "runaway_multiplier = 2\n");
                Assert.Equal(2.0, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);

                // Under 100% would stop a script before its own plan says it ends.
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "runaway_multiplier = 50%\n");
                Assert.Equal(3.0, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);
            }
        }

        // ---- Saving the Mission defaults (Settings, Space Center) ----

        [Fact]
        public void SavedMissionDefaultsLoadBack()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.SetDefaultModel("claude-opus-5-5");
                config.DefaultSpendingCap = 7.5m;
                config.DefaultDaylightLanding = false;
                config.DefaultTimeVersusDeltaV = Missions.TimeVersusDeltaV.MostEfficient;
                config.DefaultRemote = true;
                config.SaveDefaults(dir.Sub);

                var loaded = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5" }, loaded.Models);
                Assert.Equal("claude-opus-5-5", loaded.DefaultModel);
                Assert.Equal(7.5m, loaded.DefaultSpendingCap);
                Assert.False(loaded.DefaultDaylightLanding);
                Assert.Equal(Missions.TimeVersusDeltaV.MostEfficient, loaded.DefaultTimeVersusDeltaV);
                Assert.True(loaded.DefaultRemote);
            }
        }

        [Fact]
        public void SavingMissionDefaultsRewritesOnlyTheirLinesAndKeepsEverythingElseInOrder()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                File.WriteAllText(path,
                    "# My own notes\n"
                    + "debug_tools = true\n"
                    + "models = claude-sonnet-5-5, claude-opus-5-5, claude-fable-5-1\n"
                    + "  # indented comment\n"
                    + "my_unknown_key = keep me\n"
                    + "default_spending_cap = 10\n"
                    + "\n"
                    + "price.claude-fable-5-1 = 10, 50\n"
                    + "daylight_landing = true\n"
                    + "time_vs_dv = Balanced\n"
                    + "default_remote = false\n"
                    + "runaway_multiplier = 250%\n");

                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.SetDefaultModel("claude-fable-5-1");
                config.DefaultSpendingCap = 3.25m;
                config.DefaultDaylightLanding = false;
                config.DefaultTimeVersusDeltaV = Missions.TimeVersusDeltaV.Fastest;
                config.DefaultRemote = true;
                config.SaveDefaults(dir.Sub);

                Assert.Equal(
                    "# My own notes\n"
                    + "debug_tools = true\n"
                    + "models = claude-fable-5-1, claude-sonnet-5-5, claude-opus-5-5\n"
                    + "  # indented comment\n"
                    + "my_unknown_key = keep me\n"
                    + "default_spending_cap = 3.25\n"
                    + "\n"
                    + "price.claude-fable-5-1 = 10, 50\n"
                    + "daylight_landing = false\n"
                    + "time_vs_dv = Fastest\n"
                    + "default_remote = true\n"
                    + "runaway_multiplier = 250%\n",
                    File.ReadAllText(path));
                Assert.Equal(2.5, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);
            }
        }

        [Fact]
        public void SavingMissionDefaultsAppendsTheKeysTheFileLacks()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                // An older file: no default_remote, and the player deleted the cap line. No final newline either.
                File.WriteAllText(path, "# old\r\ndebug_tools = false\r\nmodels = claude-sonnet-5-5\r\ndaylight_landing = true\r\ntime_vs_dv = Balanced");

                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.DefaultRemote = true;
                config.SaveDefaults(dir.Sub);

                var text = File.ReadAllText(path);
                Assert.StartsWith("# old\r\ndebug_tools = false\r\nmodels = claude-sonnet-5-5\r\ndaylight_landing = true\r\ntime_vs_dv = Balanced\r\n", text);
                Assert.EndsWith("default_spending_cap = 10\r\n# Mission type prefilled in the start form: false is crewed (Claudia flies aboard), true is remote (Claudia at Mission Control).\r\ndefault_remote = true\r\n", text);
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DefaultRemote);
            }
        }

        [Fact]
        public void ANewConfigFileOffersACrewedMissionByDefault()
        {
            using (var dir = new TempDir())
            {
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).DefaultRemote);
                Assert.Contains("default_remote = false", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
            }
        }

        [Fact]
        public void SavingChecksTheKeyAndReportsTheResult()
        {
            using (var dir = new TempDir())
            {
                var store = new ApiKeyStore(dir.Sub, Env());
                var background = new Background();
                var transport = new FakeTransport { Status = 401 };
                var settings = new KeySettings(store, new ClaudeClient(transport, store.Load, s => { }), background);

                settings.Save("sk-typed-9999");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (settings.Checking && DateTime.UtcNow < deadline)
                {
                    background.Drain();
                    System.Threading.Thread.Sleep(5);
                }

                Assert.Equal("Key was rejected by Anthropic.", settings.Status);
                Assert.Equal("****9999", settings.MaskedKey);
                Assert.Equal("sk-typed-9999", transport.Requests[0].Headers.Find(h => h.Key == "x-api-key").Value);
            }
        }
    }
}
