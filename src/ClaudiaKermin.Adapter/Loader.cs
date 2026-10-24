// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Chat;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.ScriptCheck;
using KSP.UI.Screens;
using MissionControl = ClaudiaKermin.Core.Missions.MissionControl;
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
        private MissionDriver _missions;
        private ApplicationLauncherButton _button;
        private ApplicationLauncherButton _chatButton;
        private ApplicationLauncherButton _missionButton;
        private Texture2D _missionIcon;
        private Texture2D _missionIconBright;
        private bool _missionBright;

        private void Awake()
        {
            DontDestroyOnLoad(this);
            Debug.Log(ModInfo.LoadedLine);

            var directory = ConfigDirectory.ResolveCurrent();
            var config = PlayerConfig.LoadOrCreate(directory);
            WindowMemory.Init(directory);
            var store = new ApiKeyStore(directory, System.Environment.GetEnvironmentVariable);
            var client = new ClaudeClient(new HttpWebRequestTransport(), store.Load, Debug.Log);
            _window = new SettingsWindow(new KeySettings(store, client, _background));

            // Raw stream dumps are a debug tool: offered only with debug_tools = true in the player config.
            var dumpFolder = config.DebugTools ? Path.Combine(directory, "stream-dumps") : null;
            var chat = new ChatSession(client, _background, () => store.Load() != null)
            {
                Model = config.DefaultModel,
                CompactionTriggerTokens = config.CompactionTriggerTokens,
            };
            _chatWindow = new ChatWindow(chat, dumpFolder);

            // Missions dump raw streams into their own Mission folder when debug_tools is on.
            // The real Script Check (ADR 0003) reads kOS.Safe, which kOS loads; without kOS it fails every script.
            var missions = new MissionControl(client, _background, config, () => store.Load() != null, new KosScriptCheck());
            _missions = new MissionDriver(missions, config.DebugTools);

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
            UnlockAll();
        }

        private void UnlockAll()
        {
            _window.Unlock();
            _chatWindow.Unlock();
            _missions.Window.Unlock();
            _missions.Strip.Unlock();
        }

        private void Update()
        {
            _background.Drain();
            _missions.Update();

            if (_missionButton != null)
            {
                // The window can be opened from elsewhere (Raise cap, Change something): keep its button in step.
                var pressed = _missionButton.toggleButton.CurrentState == KSP.UI.UIRadioButton.State.True;
                if (_missions.Window.Visible && !pressed) _missionButton.SetTrue(false);
                else if (!_missions.Window.Visible && pressed) _missionButton.SetFalse(false);

                // Claudia needs the player and the window is closed: the button flashes between its two tiles.
                var bright = _missions.Flashing && Mathf.FloorToInt(Time.realtimeSinceStartup * 2f) % 2 == 0;
                if (bright != _missionBright)
                {
                    _missionBright = bright;
                    _missionButton.SetTexture(bright ? _missionIconBright : _missionIcon);
                }
            }
        }

        private void OnGUI()
        {
            _window.OnGUI();
            if (ChatSceneLoaded())
            {
                _chatWindow.OnGUI();
                _missions.OnGUI();
            }
            else
            {
                _chatWindow.Unlock();
                _missions.Window.Unlock();
                _missions.Strip.Unlock();
            }
        }

        private static bool ChatSceneLoaded()
        {
            return HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.EDITOR;
        }

        private void HideUI()
        {
            _window.UiHidden = true;
            _chatWindow.UiHidden = true;
            _missions.Window.UiHidden = true;
            _missions.Strip.UiHidden = true;
        }

        private void ShowUI()
        {
            _window.UiHidden = false;
            _chatWindow.UiHidden = false;
            _missions.Window.UiHidden = false;
            _missions.Strip.UiHidden = false;
        }

        // The chat and the Mission keep running across the change; only the input locks are released.
        // The settings window closes: the buttons are removed here and rebuilt unpressed when the launcher is
        // ready in the next scene, or not at all if that scene doesn't allow them.
        private void SceneChanging(GameScenes scene)
        {
            RemoveButtons();
            _window.Visible = false;
            UnlockAll();
            _window.UiHidden = false;
            _chatWindow.UiHidden = false;
            _missions.Window.UiHidden = false;
            _missions.Strip.UiHidden = false;
            // Each time Flight loads (including a revert), the Craft Check starts collapsed.
            if (scene == GameScenes.FLIGHT) _missions.Window.FlightLoading();
        }

        private static bool SettingsSceneLoaded()
        {
            return HighLogic.LoadedScene == GameScenes.SPACECENTER || ChatSceneLoaded();
        }

        private void AddButtons()
        {
            RemoveButtons();
            if (SettingsSceneLoaded())
            {
                _button = ApplicationLauncher.Instance.AddModApplication(
                    () => _window.Visible = true, () => _window.Visible = false,
                    null, null, null, null, SettingsScenes, MakeIcon(new Color(0.85f, 0.45f, 0.2f, 1f)));
            }
            if (ChatSceneLoaded())
            {
                _chatButton = ApplicationLauncher.Instance.AddModApplication(
                    () => _chatWindow.Visible = true, () => _chatWindow.Visible = false,
                    null, null, null, null, ChatScenes, MakeIcon(new Color(0.25f, 0.6f, 0.85f, 1f)));
                if (_chatWindow.Visible) _chatButton.SetTrue(false);
            }
            if (ChatSceneLoaded())
            {
                var window = _missions.Window;
                if (_missionIcon == null)
                {
                    _missionIcon = MakeIcon(new Color(0.3f, 0.75f, 0.35f, 1f));
                    _missionIconBright = MakeIcon(new Color(1f, 0.9f, 0.2f, 1f));
                }
                _missionBright = false;
                _missionButton = ApplicationLauncher.Instance.AddModApplication(
                    () => window.Visible = true, () => window.Visible = false,
                    null, null, null, null, ChatScenes, _missionIcon);
                if (window.Visible) _missionButton.SetTrue(false);
            }
        }

        private void RemoveButtons()
        {
            if (ApplicationLauncher.Instance != null)
            {
                if (_button != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
                if (_chatButton != null) ApplicationLauncher.Instance.RemoveModApplication(_chatButton);
                if (_missionButton != null) ApplicationLauncher.Instance.RemoveModApplication(_missionButton);
            }
            _button = null;
            _chatButton = null;
            _missionButton = null;
        }

        // Plain placeholder tiles (orange: settings, blue: chat, green: Mission); real artwork is a later ticket.
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
