// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Text;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>
    /// The player's API key: <c>ANTHROPIC_API_KEY</c> when set, otherwise a plain-text file in the
    /// config folder readable only by its owner.
    /// </summary>
    public sealed class ApiKeyStore
    {
        public const string EnvVar = "ANTHROPIC_API_KEY";
        public const string FileName = "api_key";

        private readonly string _directory;
        private readonly Func<string, string> _env;

        public ApiKeyStore(string directory, Func<string, string> env)
        {
            _directory = directory;
            _env = env;
        }

        public string FilePath
        {
            get { return Path.Combine(_directory, FileName); }
        }

        public bool UsingEnvironmentKey
        {
            get { return !string.IsNullOrWhiteSpace(_env(EnvVar)); }
        }

        /// <summary>The key in force (environment first, then file), or null.</summary>
        public string Load()
        {
            var fromEnv = _env(EnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            return LoadStored();
        }

        /// <summary>The key in the file, ignoring the environment, or null.</summary>
        public string LoadStored()
        {
            if (!File.Exists(FilePath)) return null;
            var text = File.ReadAllText(FilePath, Encoding.UTF8).Trim();
            return text.Length == 0 ? null : text;
        }

        public void Save(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) throw new ArgumentException("API key is empty.", "key");

            Directory.CreateDirectory(_directory);
            OwnerOnly.Directory(_directory);
            // Create the file empty and restrict it before the key goes in, so it is never world-readable.
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "");
            OwnerOnly.File(FilePath);
            File.WriteAllText(FilePath, key, new UTF8Encoding(false));
        }

        /// <summary>The key as shown on screen: only the last 4 characters.</summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var tail = key.Length <= 4 ? key : key.Substring(key.Length - 4);
            return "****" + tail;
        }
    }
}
