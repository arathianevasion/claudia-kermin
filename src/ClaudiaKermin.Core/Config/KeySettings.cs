// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>The state behind the settings window's key field, so the window itself stays a thin shell.</summary>
    public sealed class KeySettings
    {
        private readonly ApiKeyStore _store;
        private readonly ClaudeClient _client;
        private readonly Background _background;

        public KeySettings(ApiKeyStore store, ClaudeClient client, Background background)
        {
            _store = store;
            _client = client;
            _background = background;
            Status = "";
        }

        /// <summary>Result of the latest save or check, for display.</summary>
        public string Status { get; private set; }

        public bool Checking { get; private set; }

        /// <summary>The key as shown on screen: last 4 characters only, or empty.</summary>
        public string MaskedKey
        {
            get { return ApiKeyStore.Mask(_store.Load()); }
        }

        public bool UsingEnvironmentKey
        {
            get { return _store.UsingEnvironmentKey; }
        }

        /// <summary>Saves the typed key, then checks the key in force on a worker thread.</summary>
        public void Save(string typed)
        {
            if (string.IsNullOrWhiteSpace(typed))
            {
                Status = "Enter a key first.";
                return;
            }
            try
            {
                _store.Save(typed);
            }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException)
            {
                Status = "Could not save the key: " + e.Message;
                return;
            }
            Check();
        }

        public void Check()
        {
            if (_store.Load() == null)
            {
                Status = "No key set.";
                return;
            }
            Checking = true;
            Status = "Checking key...";
            _background.Run(() => KeyCheck.Run(_client), result =>
            {
                Checking = false;
                Status = KeyCheck.Describe(result);
                if (_store.UsingEnvironmentKey)
                    Status += " (This checked ANTHROPIC_API_KEY, which takes priority over the saved key.)";
            });
        }
    }
}
