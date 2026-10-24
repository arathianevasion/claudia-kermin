// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Chat;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using KSP.UI.Screens;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Created once when KSP starts and kept across scenes (so requests survive scene changes).
    /// Announces that Claudia Kermin loaded, owns the Claude client and the chat, and drains their
    /// results each frame.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Loader : MonoBehaviour
    {
        // Settings: the Space Center, the VAB/SPH and Flight (map view counts as Flight); not the main
        // menu, the Tracking Station or other KSC buildings.
        private const ApplicationLauncher.AppScenes SettingsScenes =
            ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT
            | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.VAB
            | ApplicationLauncher.AppScenes.SPH;

        private const ApplicationLauncher.AppScenes ChatScenes =
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW
            | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH;

        private readonly Background _background = new Background(Debug.LogException);
        private SettingsWindow _window;
        private ChatWindow _chatWindow;
        private ApplicationLauncherButton _button;
        private ApplicationLauncherButton _chatButton;

        private void Awake()
        {
            DontDestroyOnLoad(this);
            Debug.Log(ModInfo.LoadedLine);

            var directory = ConfigDirectory.ResolveCurrent();
            var config = PlayerConfig.LoadOrCreate(directory);
            var store = new ApiKeyStore(directory, System.Environment.GetEnvironmentVariable);
            var client = new ClaudeClient(new HttpWebRequestTransport(), store.Load, Debug.Log);
            _window = new SettingsWindow(new KeySettings(store, client, _background));

            // Raw stream dumps are a debug tool: offered only with debug_tools = true in the player config.
            var dumpFolder = config.DebugTools ? Path.Combine(directory, "stream-dumps") : null;
            var chat = new ChatSession(client, _background, () => store.Load() != null);
            _chatWindow = new ChatWindow(chat, dumpFolder);

            GameEvents.onGUIApplicationLauncherReady.Add(AddButtons);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButtons);
            GameEvents.onHideUI.Add(HideUI);
            GameEvents.onShowUI.Add(ShowUI);
            GameEvents.onGameSceneLoadRequested.Add(SceneChanging);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButtons);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButtons);
            GameEvents.onHideUI.Remove(HideUI);
            GameEvents.onShowUI.Remove(ShowUI);
            GameEvents.onGameSceneLoadRequested.Remove(SceneChanging);
            RemoveButtons();
            _chatWindow.Unlock();
        }

        private void Update()
        {
            _background.Drain();
        }

        private void OnGUI()
        {
            _window.OnGUI();
            if (ChatSceneLoaded()) _chatWindow.OnGUI();
        }

        private static bool ChatSceneLoaded()
        {
            return HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.EDITOR;
        }

        private void HideUI()
        {
            _chatWindow.UiHidden = true;
        }

        private void ShowUI()
        {
            _chatWindow.UiHidden = false;
        }

        // The chat keeps running across the change; only the keyboard lock is released.
        // The settings window closes: its button is rebuilt unpressed in the next scene, or absent there.
        private void SceneChanging(GameScenes scene)
        {
            _window.Visible = false;
            _chatWindow.Unlock();
            _chatWindow.UiHidden = false;
        }

        private void AddButtons()
        {
            if (_button == null)
            {
                _button = ApplicationLauncher.Instance.AddModApplication(
                    () => _window.Visible = true, () => _window.Visible = false,
                    null, null, null, null, SettingsScenes, MakeIcon(new Color(0.85f, 0.45f, 0.2f, 1f)));
            }
            if (_chatButton == null)
            {
                _chatButton = ApplicationLauncher.Instance.AddModApplication(
                    () => _chatWindow.Visible = true, () => _chatWindow.Visible = false,
                    null, null, null, null, ChatScenes, MakeIcon(new Color(0.25f, 0.6f, 0.85f, 1f)));
                if (_chatWindow.Visible) _chatButton.SetTrue(false);
            }
        }

        private void RemoveButtons()
        {
            if (ApplicationLauncher.Instance != null)
            {
                if (_button != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
                if (_chatButton != null) ApplicationLauncher.Instance.RemoveModApplication(_chatButton);
            }
            _button = null;
            _chatButton = null;
        }

        // Plain placeholder tiles (orange: settings, blue: chat); real artwork is a later ticket.
        private static Texture2D MakeIcon(Color color)
        {
            var icon = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            var pixels = new Color[38 * 38];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = color;
            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }
    }
}
