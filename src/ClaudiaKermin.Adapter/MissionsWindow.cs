// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Missions list, shown in the Space Center and the Tracking Station (and from the toolbar elsewhere): every Mission
    /// in the save, the one in play first, then Paused ones, then finished ones as history with their final Debrief and cost.
    /// It never edits a Mission; its buttons go to a Mission's vessel, resume a Paused Mission (which pauses the active one at
    /// its next sequence boundary) and delete a finished one.
    /// </summary>
    public class MissionsWindow
    {
        private readonly Action<MissionInput> _send;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B04, "", "ClaudiaKerminMissions", new Rect(220, 100, 520, 560), new Vector2(420, 260), true);

        private MissionView _view;
        private Vector2 _scroll;
        private string _showing = "";
        private string _confirmingDelete = "";

        public bool Visible;

        /// <summary>Set while the stock UI is hidden (F2).</summary>
        public bool UiHidden;

        public MissionsWindow(Action<MissionInput> send)
        {
            _send = send;
            _frame.Remember("missions");
        }

        public void OnGUI(MissionView view)
        {
            if (!Visible || UiHidden)
            {
                _confirmingDelete = "";
                Unlock();
                return;
            }
            _view = view;
            GUI.skin = HighLogic.Skin;
            _frame.Style = ClaudiaSkin.BareWindow;
            _frame.Show(Draw);
        }

        /// <summary>Releases the input locks; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private void Draw(int id)
        {
            DrawTitleBar();
            GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            GUILayout.Space(ClaudiaSkin.Gap);
            if (_view.Missions.Count == 0)
                GUILayout.Label(MissionListRules.Empty, ClaudiaSkin.Secondary);
            else
            {
                _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
                foreach (var entry in new List<MissionListEntry>(_view.Missions)) DrawEntry(entry);
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();
            _frame.DrawResizeZones();
            WindowFrame.DrawTooltip();
            GUI.DragWindow();
        }

        private void DrawTitleBar()
        {
            var bar = ClaudiaSkin.TitleBarHeight;
            var width = _frame.Rect.width;
            ClaudiaSkin.DrawIcon(new Rect(ClaudiaSkin.Padding, (bar - 8) / 2f, 8, 8), ClaudiaSkin.IconShape.Dot, ClaudiaSkin.Claudia);
            var title = new GUIContent("Claudia: Missions");
            GUI.Label(new Rect(ClaudiaSkin.Padding + 8 + ClaudiaSkin.Gap, 0, ClaudiaSkin.TitleAccent.CalcSize(title).x, bar), title, ClaudiaSkin.TitleAccent);

            var close = new Rect(width - ClaudiaSkin.Padding - ClaudiaSkin.MinClickable, (bar - ClaudiaSkin.MinClickable) / 2f, ClaudiaSkin.MinClickable, ClaudiaSkin.MinClickable);
            if (GUI.Button(close, new GUIContent("", "Close"), GUIStyle.none)) Visible = false;
            ClaudiaSkin.DrawIcon(new Rect(close.center.x - 6, close.center.y - 6, 12, 12), ClaudiaSkin.IconShape.Close,
                close.Contains(Event.current.mousePosition) ? ClaudiaSkin.Text : ClaudiaSkin.TextSecondary);
            GUILayout.Space(bar);
        }

        private void DrawEntry(MissionListEntry entry)
        {
            ClaudiaSkin.BeginCard();
            GUILayout.BeginHorizontal();
            var chip = MissionListRules.Status(entry);
            ClaudiaSkin.Chip(chip.Text, ClaudiaSkin.KindOf(chip.Tone));
            GUILayout.Label(entry.Goal, ClaudiaSkin.Body);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(MissionListRules.Cost(entry), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            if (entry.VesselName.Length > 0) GUILayout.Label(entry.VesselName, ClaudiaSkin.Secondary);
            GUILayout.EndHorizontal();

            var debrief = MissionListRules.DebriefTitle(entry);
            if (debrief.Length > 0) GUILayout.Label(debrief, ClaudiaSkin.Secondary);
            if (entry.FinalDebrief != null && _showing == entry.Id)
            {
                GUILayout.Label(entry.FinalDebrief.Text, ClaudiaSkin.Body);
                if (entry.FinalDebrief.Why.Length > 0) GUILayout.Label(entry.FinalDebrief.Why, ClaudiaSkin.Secondary);
            }

            GUILayout.BeginHorizontal();
            if (entry.FinalDebrief != null && ClaudiaSkin.SmallButton(_showing == entry.Id ? "Hide Debrief" : "Show Debrief", GUILayout.ExpandWidth(false)))
                _showing = _showing == entry.Id ? "" : entry.Id;
            if (entry.CanGoToVessel && ClaudiaSkin.SmallButton("Go to vessel", GUILayout.ExpandWidth(false))) _send(new GoToMissionVessel(entry.Id));
            if (entry.CanResume && ClaudiaSkin.SmallButton("Resume", GUILayout.ExpandWidth(false))) _send(new ResumeMission(entry.Id));
            GUILayout.FlexibleSpace();
            DrawDelete(entry);
            GUILayout.EndHorizontal();
            ClaudiaSkin.EndCard();
        }

        // Deleting takes the Mission's folder with it, so it asks first.
        private void DrawDelete(MissionListEntry entry)
        {
            if (!entry.CanDelete) return;
            if (_confirmingDelete != entry.Id)
            {
                if (ClaudiaSkin.SmallButton("Delete", GUILayout.ExpandWidth(false))) _confirmingDelete = entry.Id;
                return;
            }
            GUILayout.Label("Delete its folder?", ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            if (ClaudiaSkin.DangerButton("Delete", GUILayout.ExpandWidth(false)))
            {
                _confirmingDelete = "";
                _send(new DeleteMission(entry.Id));
            }
            if (ClaudiaSkin.SmallButton("Keep", GUILayout.ExpandWidth(false))) _confirmingDelete = "";
        }
    }
}
