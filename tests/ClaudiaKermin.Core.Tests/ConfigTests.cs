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
        public void TheManualControlLinkGraceIsFiveSecondsByDefaultAndReadFromTheFile()
        {
            using (var dir = new TempDir())
            {
                Assert.Equal(5, PlayerConfig.LoadOrCreate(dir.Sub).ManualLinkGraceSeconds);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("manual_link_grace_s = 5", File.ReadAllText(path));

                File.WriteAllText(path, "manual_link_grace_s = 12.5\n");
                Assert.Equal(12.5, PlayerConfig.LoadOrCreate(dir.Sub).ManualLinkGraceSeconds);
                File.WriteAllText(path, "manual_link_grace_s = -1\n");
                Assert.Equal(5, PlayerConfig.LoadOrCreate(dir.Sub).ManualLinkGraceSeconds);
            }
        }

        [Fact]
        public void PhysicsWarpIsOnByDefaultWrittenToTheFileAndReadFromIt()
        {
            using (var dir = new TempDir())
            {
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).PhysicsWarp);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("physics_warp = true", File.ReadAllText(path));

                File.WriteAllText(path, "physics_warp = false\n");
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).PhysicsWarp);
                File.WriteAllText(path, "physics_warp = TRUE\n");
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).PhysicsWarp);
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
        public void PlayerConfigHasNoWaitCapsAndIgnoresTheWaitCapKeysAnOlderFileStillHolds()
        {
            using (var dir = new TempDir())
            {
                PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.DoesNotContain("wait_cap", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));

                // An older config.cfg (ADR 0004): the keys are ignored, not errors, and the lines around them still read.
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName),
                    "craft_check_margin = 20\nwait_cap_orbits = 2.5\nwait_cap_ground_s = soon\nreplan_tolerance = 30\n");
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(20, config.CraftCheckMarginPercent);
                Assert.Equal(30, config.ReplanTolerancePercent);
                Assert.Null(typeof(PlayerConfig).GetField("WaitCapOrbits"));
                Assert.Null(typeof(PlayerConfig).GetField("WaitCapGroundSeconds"));
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
                config.DefaultAutoMode = true;
                config.SaveDefaults(dir.Sub);

                var loaded = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5" }, loaded.Models);
                Assert.Equal("claude-opus-5-5", loaded.DefaultModel);
                Assert.Equal(7.5m, loaded.DefaultSpendingCap);
                Assert.False(loaded.DefaultDaylightLanding);
                Assert.Equal(Missions.TimeVersusDeltaV.MostEfficient, loaded.DefaultTimeVersusDeltaV);
                Assert.True(loaded.DefaultRemote);
                Assert.True(loaded.DefaultAutoMode);
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
                    + "default_auto_mode = false\n"
                    + "runaway_multiplier = 250%\n");

                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.SetDefaultModel("claude-fable-5-1");
                config.DefaultSpendingCap = 3.25m;
                config.DefaultDaylightLanding = false;
                config.DefaultTimeVersusDeltaV = Missions.TimeVersusDeltaV.Fastest;
                config.DefaultRemote = true;
                config.DefaultAutoMode = true;
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
                    + "default_auto_mode = true\n"
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
                // An older file: no default_remote or default_auto_mode, and the player deleted the cap line. No final newline either.
                File.WriteAllText(path, "# old\r\ndebug_tools = false\r\nmodels = claude-sonnet-5-5\r\ndaylight_landing = true\r\ntime_vs_dv = Balanced");

                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.DefaultRemote = true;
                config.DefaultAutoMode = true;
                config.SaveDefaults(dir.Sub);

                var text = File.ReadAllText(path);
                Assert.StartsWith("# old\r\ndebug_tools = false\r\nmodels = claude-sonnet-5-5\r\ndaylight_landing = true\r\ntime_vs_dv = Balanced\r\n", text);
                Assert.EndsWith("default_spending_cap = 10\r\n# Mission type prefilled in the start form: false is crewed (Claudia flies aboard), true is remote (Claudia at Mission Control).\r\ndefault_remote = true\r\n"
                    + "# Start new Missions in Auto Mode: Claudia's plans after the first Mission Plan are approved for you while the Spending Cap has room.\r\ndefault_auto_mode = true\r\n", text);
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DefaultRemote);
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DefaultAutoMode);
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
        public void ANewConfigFileStartsMissionsWithAutoModeOff()
        {
            using (var dir = new TempDir())
            {
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).DefaultAutoMode);
                Assert.Contains("default_auto_mode = false", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
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
