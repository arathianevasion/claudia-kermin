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
        // The one toolbar button: the Space Center, the VAB/SPH and Flight (map view counts as Flight); not the
        // main menu, the Tracking Station or other KSC buildings.
        private const ApplicationLauncher.AppScenes ButtonScenes =
            ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT
            | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.VAB
            | ApplicationLauncher.AppScenes.SPH;

        private const string IconUrl = "ClaudiaKermin/Icons/ClaudiaIcon";
        private const string AttentionIconUrl = "ClaudiaKermin/Icons/ClaudiaIconAttention";

        private readonly Background _background = new Background(Debug.LogException);
        private SettingsWindow _window;
        private ChatWindow _chatWindow;
        private MissionDriver _missions;
        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private Texture2D _iconAttention;
        private bool _attentionShown;

        private void Awake()
        {
            DontDestroyOnLoad(this);
            Debug.Log(ModInfo.LoadedLine);

            var directory = ConfigDirectory.ResolveCurrent();
            var config = PlayerConfig.LoadOrCreate(directory);
            WindowMemory.Init(directory);
            var store = new ApiKeyStore(directory, System.Environment.GetEnvironmentVariable);
            var client = new ClaudeClient(new HttpWebRequestTransport(), store.Load, Debug.Log);
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
            _window = new SettingsWindow(new KeySettings(store, client, _background), _missions, config, directory, _chatWindow);
            _missions.Window.OpenSettings = () => _window.Visible = true;

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
            _missions.Locks.Release();
        }

        private void Update()
        {
            _background.Drain();
            _missions.Update();

            if (_button != null)
            {
                // A window can be opened or closed from elsewhere (Raise cap, Change something, Settings' Cancel
                // and Apply): keep the button's pressed state in step with whatever it toggles in this scene.
                var open = Toggled;
                var pressed = _button.toggleButton.CurrentState == KSP.UI.UIRadioButton.State.True;
                if (open && !pressed) _button.SetTrue(false);
                else if (!open && pressed) _button.SetFalse(false);

                // Claudia needs the player and the window is closed: the button flashes between its two frames.
                var attention = _missions.Flashing && Mathf.FloorToInt(Time.realtimeSinceStartup * 2f) % 2 == 0;
                if (attention != _attentionShown)
                {
                    _attentionShown = attention;
                    _button.SetTexture(attention ? _iconAttention : _icon);
                }
            }
        }

        // The Space Center button toggles Settings (in its Space Center context); the VAB/SPH and Flight/Map
        // toggle the Mission window.
        private bool Toggled
        {
            get { return HighLogic.LoadedScene == GameScenes.SPACECENTER ? _window.Visible : _missions.Window.Visible; }
        }

        private void ButtonOn()
        {
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER) _window.Visible = true;
            else _missions.Window.Visible = true;
        }

        private void ButtonOff()
        {
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER) _window.Visible = false;
            else _missions.Window.Visible = false;
        }

        private void OnGUI()
        {
            _window.OnGUI();
            // The chat also opens in the Space Center, from Settings' Basic chat (debug).
            if (SettingsSceneLoaded()) _chatWindow.OnGUI();
            else _chatWindow.Unlock();
            if (ChatSceneLoaded())
            {
                _missions.OnGUI();
            }
            else
            {
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
            if (!SettingsSceneLoaded()) return;
            if (_icon == null)
            {
                _icon = LoadIcon(IconUrl, ClaudiaSkin.IconGreen);
                _iconAttention = LoadIcon(AttentionIconUrl, ClaudiaSkin.Attention);
            }
            _attentionShown = false;
            _button = ApplicationLauncher.Instance.AddModApplication(
                ButtonOn, ButtonOff, null, null, null, null, ButtonScenes, _icon);
            if (Toggled) _button.SetTrue(false);
        }

        private void RemoveButtons()
        {
            if (ApplicationLauncher.Instance != null && _button != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        // The Winged glare artwork from GameData; a plain tile in the icon colour if the PNG didn't load.
        private static Texture2D LoadIcon(string url, Color fallback)
        {
            var icon = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(url, false) : null;
            return icon != null ? icon : MakeIcon(fallback);
        }

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
