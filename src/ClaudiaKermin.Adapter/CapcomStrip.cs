// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Text;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Capcom strip: a slim bar under the altimeter in Flight with Claudia's portrait and latest line,
    /// the Mission Plan stepper, the Spending Cap, what is running and the quick replies for the current
    /// state. "Show log" drops a drawer from it with the Mission log: the plan and Debrief cards, each with
    /// its why and, for a Sequence Plan, its script.
    /// </summary>
    public class CapcomStrip
    {
        private const float Width = 620;
        private const float PortraitSize = 56;
        private const float DrawerHeight = 280;
        private const string PortraitUrl = "ClaudiaKermin/Icons/ClaudiaPortrait";

        private readonly MissionWindow _window;
        private readonly LogDrawer _log;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B04, "", "ClaudiaKerminCapcom", new Rect(0, 70, Width, 0), Vector2.zero, false);
        private GUIStyle _wrap;
        private Texture2D _portrait;

        public bool UiHidden;

        /// <summary>The Show log drawer is open.</summary>
        public bool DrawerOpen;

        /// <summary>Set before each OnGUI.</summary>
        public MissionView View;

        /// <param name="send">Takes what the drawer's transcript sends: Warning acknowledgements and the approval checkbox.</param>
        public CapcomStrip(MissionWindow window, Action<MissionInput> send)
        {
            _window = window;
            _log = new LogDrawer(send);
        }

        /// <summary>Flight is loading: the Craft Check in the drawer starts collapsed again.</summary>
        public void FlightLoading()
        {
            _log.FlightLoading();
        }

        /// <summary>The strip is on screen, so the player can see Claudia's line without a window.</summary>
        public bool Showing
        {
            get { return !UiHidden && View != null && View.Stage != MissionStage.None; }
        }

        public void OnGUI()
        {
            if (!Showing)
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

            GUILayout.BeginHorizontal();
            GUILayout.Box(Portrait(), GUILayout.Width(PortraitSize), GUILayout.Height(PortraitSize));
            GUILayout.BeginVertical();

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
            if (View.PlanStale.Length > 0) status += " | The craft has changed (" + View.PlanStale + "): Re-plan.";
            else if (View.Stage == MissionStage.Running && !View.ManualControlAllowed) status += " | Manual Control unavailable: no link, no other crew aboard.";
            else if (View.Locks != ControlLocks.None) status += " | Staging, groups, SAS, RCS, gear and brakes are locked while Claudia flies.";
            GUILayout.Label(status, _wrap);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            foreach (var reply in View.QuickReplies)
                if (MissionWindowRules.InActionRow(reply) && GUILayout.Button(MissionWindow.Label(reply, View), GUILayout.ExpandWidth(false))) _window.QuickReplied(reply);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(DrawerOpen ? "Hide log" : "Show log", GUILayout.ExpandWidth(false))) DrawerOpen = !DrawerOpen;
            GUILayout.EndHorizontal();

            // With the drawer closed, confirming a typed approval is still here, where the buttons used to be.
            if (!DrawerOpen && View.ConfirmPrompt != null) _log.DrawApproval(View);
            if (DrawerOpen) _log.Draw(View, Width - 40, GUILayout.Height(DrawerHeight));
            WindowFrame.DrawTooltip();
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

        // The Winged glare portrait from GameData when it loaded; otherwise a plain blue tile.
        private Texture2D Portrait()
        {
            if (_portrait != null) return _portrait;
            var icon = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(PortraitUrl, false) : null;
            if (icon != null) return _portrait = icon;
            var tile = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            tile.SetPixels(new[] { new Color(0.25f, 0.6f, 0.85f), new Color(0.25f, 0.6f, 0.85f), new Color(0.25f, 0.6f, 0.85f), new Color(0.25f, 0.6f, 0.85f) });
            tile.Apply();
            return _portrait = tile;
        }
    }
}
