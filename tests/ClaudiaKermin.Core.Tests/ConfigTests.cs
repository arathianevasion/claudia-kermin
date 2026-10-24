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
