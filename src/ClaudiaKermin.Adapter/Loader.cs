// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using KSP.UI.Screens;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Created once when KSP starts and kept across scenes (so requests survive scene changes).
    /// Announces that Claudia Kermin loaded, owns the Claude client and drains its results each frame.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Loader : MonoBehaviour
    {
        private readonly Background _background = new Background();
        private SettingsWindow _window;
        private ApplicationLauncherButton _button;

        private void Awake()
        {
            DontDestroyOnLoad(this);
            Debug.Log(ModInfo.LoadedLine);

            var directory = ConfigDirectory.ResolveCurrent();
            PlayerConfig.LoadOrCreate(directory);
            var store = new ApiKeyStore(directory, System.Environment.GetEnvironmentVariable);
            var client = new ClaudeClient(new HttpWebRequestTransport(), store.Load, Debug.Log);
            _window = new SettingsWindow(new KeySettings(store, client, _background));

            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            RemoveButton();
        }

        private void Update()
        {
            _background.Drain();
        }

        private void OnGUI()
        {
            _window.OnGUI();
        }

        private void AddButton()
        {
            if (_button != null) return;
            _button = ApplicationLauncher.Instance.AddModApplication(
                () => _window.Visible = true, () => _window.Visible = false,
                null, null, null, null, ApplicationLauncher.AppScenes.ALWAYS, MakeIcon());
        }

        private void RemoveButton()
        {
            if (_button == null) return;
            if (ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        // A plain placeholder tile; real artwork is a later ticket.
        private static Texture2D MakeIcon()
        {
            var icon = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            var pixels = new Color[38 * 38];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color(0.85f, 0.45f, 0.2f, 1f);
            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }
    }
}
