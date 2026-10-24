// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Draws the Mission log: chat lines, and the Mission Plan, Sequence Plans and Debriefs as cards with
    /// a "Why" expander and, for a Sequence Plan, "Show script". Used by the Mission window and by the
    /// Capcom strip's Show log drawer, so a card looks and behaves the same in both. Plain text only:
    /// Claude's replies may contain '&lt;', which rich text would misread.
    /// </summary>
    public class LogDrawer
    {
        private Vector2 _scroll;
        private int _seenLogCount = -1;
        private GUIStyle _wrap;
        private GUIStyle _bold;
        private GUIStyle _script;

        // Expanded "why" and "Show script" per log entry.
        private readonly HashSet<LogEntry> _showWhy = new HashSet<LogEntry>();
        private readonly HashSet<LogEntry> _showScript = new HashSet<LogEntry>();

        /// <summary>Draws the log in a scroll view that follows new entries; <paramref name="options"/> size the view.</summary>
        public void Draw(MissionView view, params GUILayoutOption[] options)
        {
            Styles();
            if (view.Log.Count != _seenLogCount)
            {
                _seenLogCount = view.Log.Count;
                _scroll.y = float.MaxValue;
            }
            _scroll = GUILayout.BeginScrollView(_scroll, options);
            foreach (var entry in view.Log) DrawEntry(entry, view.Ut);
            if (view.Busy) GUILayout.Label("Claudia: (" + (view.Activity.Length > 0 ? view.Activity : "thinking") + "...)", _wrap);
            GUILayout.EndScrollView();
        }

        private void Styles()
        {
            if (_wrap != null) return;
            _wrap = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };
            _bold = new GUIStyle(_wrap) { fontStyle = FontStyle.Bold };
            // Scripts wrap to the card instead of running off the right edge; the log scrolls them.
            _script = new GUIStyle(GUI.skin.textArea) { wordWrap = true, richText = false };
        }

        private void DrawEntry(LogEntry entry, double now)
        {
            switch (entry.Kind)
            {
                case LogKind.Player:
                    GUILayout.Label("You: " + entry.Text, _wrap);
                    return;
                case LogKind.Claudia:
                    GUILayout.Label("Claudia: " + entry.Text + (entry.AnsweredBy.Length > 0 ? "  [answered by " + entry.AnsweredBy + "]" : ""), _wrap);
                    return;
                case LogKind.Notice:
                    GUILayout.Label("[" + entry.Text + "]", _wrap);
                    return;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(entry.Kind == LogKind.SequencePlan ? "Sequence Plan: " + entry.Title : entry.Title, _bold);
            if (entry.CustomScript) GUILayout.Label("[Custom Script]", _bold, GUILayout.ExpandWidth(false));
            // When the card was made, from now; the date is on hover.
            if (entry.Ut > 0 && now > 0) GUILayout.Label(TimeContent(entry.Ut, now), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            if (entry.CustomScript && entry.CustomScriptReason.Length > 0) GUILayout.Label("Custom Script: " + entry.CustomScriptReason, _wrap);
            GUILayout.Label(entry.Text, _wrap);
            GUILayout.BeginHorizontal();
            if (entry.Why.Length > 0 && GUILayout.Button(_showWhy.Contains(entry) ? "Hide why" : "Why", GUILayout.ExpandWidth(false)))
                Toggle(_showWhy, entry);
            if (entry.Script.Length > 0 && GUILayout.Button(_showScript.Contains(entry) ? "Hide script" : "Show script", GUILayout.ExpandWidth(false)))
                Toggle(_showScript, entry);
            GUILayout.EndHorizontal();
            if (_showWhy.Contains(entry)) GUILayout.Label("Why: " + entry.Why, _wrap);
            if (_showScript.Contains(entry)) GUILayout.TextArea(entry.Script, _script);
            GUILayout.EndVertical();
        }

        /// <summary>A time from now ("in 2d 3h 10m") whose hover tooltip is the Kerbin date; <see cref="WindowFrame.DrawTooltip"/> shows it.</summary>
        public static GUIContent TimeContent(double ut, double now)
        {
            return new GUIContent(KerbinTime.FromNow(ut, now), KerbinTime.Date(ut));
        }

        private static void Toggle(HashSet<LogEntry> set, LogEntry entry)
        {
            if (!set.Remove(entry)) set.Add(entry);
        }
    }
}
