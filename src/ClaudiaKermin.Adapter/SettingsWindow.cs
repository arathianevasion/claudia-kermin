// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Adapter.Roster;
using ClaudiaKermin.Core.Config;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>The Claudia settings window: a masked API key field and the result of checking it.</summary>
    public class SettingsWindow
    {
        private const string KeyControl = "ClaudiaSettingsKey";

        private readonly KeySettings _settings;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B01, "Claudia Kermin settings", "ClaudiaKerminSettings", new Rect(200, 120, 360, 260), Vector2.zero, false);
        private string _typed = "";
        private string _uninstallStatus = "";

        public bool Visible;

        public SettingsWindow(KeySettings settings)
        {
            _settings = settings;
        }

        public void OnGUI()
        {
            if (!Visible)
            {
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            _frame.Show(Draw);
            _frame.SetTyping(GUI.GetNameOfFocusedControl() == KeyControl);
        }

        /// <summary>Releases the mouse lock; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private void Draw(int id)
        {
            GUILayout.Label("Anthropic API key");
            var saved = _settings.MaskedKey;
            if (saved.Length > 0)
                GUILayout.Label("Saved: " + saved);
            if (_settings.UsingEnvironmentKey)
                GUILayout.Label("ANTHROPIC_API_KEY is set and takes priority.");

            GUI.SetNextControlName(KeyControl);
            _typed = GUILayout.PasswordField(_typed, '*', 200);

            GUILayout.BeginHorizontal();
            GUI.enabled = !_settings.Checking && _typed.Trim().Length > 0;
            if (GUILayout.Button("Save and check"))
            {
                _settings.Save(_typed);
                _typed = "";
            }
            GUI.enabled = !_settings.Checking;
            if (GUILayout.Button("Check saved key"))
                _settings.Check();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_settings.Status.Length > 0)
                GUILayout.Label(_settings.Status);

            GUILayout.Space(8);
            GUILayout.Label("Before removing the mod");
            GUI.enabled = UninstallPrep.SaveIsLoaded;
            if (GUILayout.Button("Prepare to uninstall"))
                _uninstallStatus = UninstallPrep.Run();
            GUI.enabled = true;
            if (!UninstallPrep.SaveIsLoaded)
                GUILayout.Label("Load a save to use this.");
            if (_uninstallStatus.Length > 0)
                GUILayout.Label(_uninstallStatus);
            GUI.DragWindow();
        }
    }
}
