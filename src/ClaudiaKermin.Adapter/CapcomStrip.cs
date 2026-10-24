// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Text;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The minimal Capcom strip: a slim bar under the altimeter in Flight showing Claudia's latest line,
    /// the Mission Plan stepper, the Spending Cap, what is running and the quick replies for the current
    /// state, with "Show log" opening the Mission window. The portrait and live progress come later.
    /// </summary>
    public class CapcomStrip
    {
        private const float Width = 620;
        private readonly MissionWindow _window;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B04, "", "ClaudiaKerminCapcom", new Rect(0, 70, Width, 0), Vector2.zero, false);
        private GUIStyle _wrap;

        public bool UiHidden;

        /// <summary>Set before each OnGUI.</summary>
        public MissionView View;

        public CapcomStrip(MissionWindow window)
        {
            _window = window;
        }

        public void OnGUI()
        {
            if (UiHidden || View == null || View.Stage == MissionStage.None)
            {
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            // Under the altimeter at the top centre; it grows to fit and can't be dragged off.
            _frame.Rect = new Rect((Screen.width - Width) / 2f, 70, Width, _frame.Rect.height);
            _frame.Show(Draw, GUILayout.Width(Width));
        }

        /// <summary>Releases the mouse lock; call when the strip goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private void Draw(int id)
        {
            if (_wrap == null) _wrap = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };

            var line = View.Busy ? "Claudia is " + (View.Activity.Length > 0 ? View.Activity : "thinking") + "..."
                : View.LatestLine.Length > 0 ? "Claudia: " + View.LatestLine : "Claudia: standing by.";
            GUILayout.Label(line, _wrap);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Stepper(View), _wrap);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Spent " + MissionWindow.SpendText(View), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            var status = View.RunStatus.Length > 0 ? View.RunStatus : MissionWindow.StageText(View);
            if (View.ConfirmPrompt != null) status = View.ConfirmPrompt;
            GUILayout.Label(status, _wrap);

            GUILayout.BeginHorizontal();
            foreach (var reply in View.QuickReplies)
                if (GUILayout.Button(MissionWindow.Label(reply), GUILayout.ExpandWidth(false))) _window.QuickReplied(reply);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_window.Visible ? "Hide log" : "Show log", GUILayout.ExpandWidth(false)))
                _window.Visible = !_window.Visible;
            GUILayout.EndHorizontal();
        }

        private static string Stepper(MissionView view)
        {
            if (view.PlanSequences.Count == 0) return "Mission Plan: not yet";
            var text = new StringBuilder("Plan: ");
            for (var i = 0; i < view.PlanSequences.Count; i++)
            {
                if (i > 0) text.Append("  >  ");
                text.Append(i == view.CurrentSequence ? "[" + view.PlanSequences[i] + "]" : view.PlanSequences[i]);
            }
            return text.ToString();
        }
    }
}
