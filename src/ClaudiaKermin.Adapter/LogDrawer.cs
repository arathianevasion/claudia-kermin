// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Draws the transcript: the player's lines as bubbles, Claudia's under her name, notices as muted lines, and the
    /// Mission Plan, Sequence Plans and Debriefs as cards with a "Why" expander and, for a Sequence Plan, "Show script";
    /// then the Busy line. Given a send, it leads with the Craft Check, whose Warnings it acknowledges. Used by the Mission
    /// window and by the Capcom strip's Show log drawer, so an entry looks and behaves the same in both. Plain text only:
    /// Claude's replies may contain '&lt;', which rich text would misread.
    /// </summary>
    public class LogDrawer
    {
        private readonly Action<MissionInput> _send;
        private Vector2 _scroll;
        private int _seenLogCount = -1;
        private GUIStyle _bold, _script;
        private GUISkin _stylesFor;

        // Expanded "why" and "Show script" per log entry.
        private readonly HashSet<LogEntry> _showWhy = new HashSet<LogEntry>();
        private readonly HashSet<LogEntry> _showScript = new HashSet<LogEntry>();

        // The Craft Check: a new result (a different set of findings) brings the transcript back to the top, where it is.
        private string _seenCheckResult = "";
        // In Flight the Craft Check starts collapsed to one line so the Mission log gets the room; reset when Flight loads.
        private bool _checkCollapsed = true;

        /// <summary>The Mission log alone (the Capcom strip's drawer).</summary>
        public LogDrawer()
        {
        }

        /// <summary>The whole transcript, led by the Craft Check; <paramref name="send"/> takes its acknowledgements.</summary>
        public LogDrawer(Action<MissionInput> send)
        {
            _send = send;
        }

        /// <summary>Flight is loading: the Craft Check starts collapsed again.</summary>
        public void FlightLoading()
        {
            _checkCollapsed = true;
        }

        /// <summary>
        /// Draws the transcript in a scroll view that follows new entries; <paramref name="width"/> is the width the entries
        /// get (for the player's bubbles), and <paramref name="options"/> size the view.
        /// </summary>
        public void Draw(MissionView view, float width, params GUILayoutOption[] options)
        {
            Styles();
            if (view.Log.Count != _seenLogCount)
            {
                _seenLogCount = view.Log.Count;
                _scroll.y = float.MaxValue;
            }
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, options);
            if (_send != null && (view.StartForm.CanStart || view.CraftCheck.State != CraftCheckState.None)) DrawCraftCheck(view);
            if (_send != null && view.StartForm.CanStart && view.StartForm.Refusal.Length > 0) GUILayout.Label(view.StartForm.Refusal, ClaudiaSkin.DangerText);
            foreach (var entry in view.Log) DrawEntry(entry, view.Ut, width);
            if (view.Busy) GUILayout.Label("Claudia is " + (view.Activity.Length > 0 ? view.Activity : "thinking") + "…", ClaudiaSkin.Muted);
            GUILayout.EndScrollView();
        }

        private void Styles()
        {
            if (_bold != null && ReferenceEquals(_stylesFor, HighLogic.Skin)) return;
            _stylesFor = HighLogic.Skin;
            _bold = new GUIStyle(ClaudiaSkin.Body) { fontStyle = FontStyle.Bold };
            // Scripts wrap to the card instead of running off the right edge; the transcript scrolls them.
            _script = new GUIStyle(ClaudiaSkin.TextField) { wordWrap = true, font = ClaudiaSkin.Mono.font, fontSize = 12 };
        }

        private void DrawEntry(LogEntry entry, double now, float width)
        {
            switch (entry.Kind)
            {
                case LogKind.Player:
                    DrawBubble(entry.Text, width);
                    return;
                case LogKind.Claudia:
                    GUILayout.Label("Claudia", ClaudiaSkin.ClaudiaName);
                    GUILayout.Label(entry.Text, ClaudiaSkin.Body);
                    if (entry.AnsweredBy.Length > 0) GUILayout.Label("[answered by " + entry.AnsweredBy + "]", ClaudiaSkin.Secondary);
                    return;
                case LogKind.Notice:
                    GUILayout.Label(entry.Text, ClaudiaSkin.Notice);
                    return;
            }
            DrawCard(entry, now);
        }

        // Right-aligned, as wide as the text up to 80% of the transcript.
        private static void DrawBubble(string text, float width)
        {
            var style = ClaudiaSkin.PlayerBubble;
            var bubbleWidth = Mathf.Min(style.CalcSize(new GUIContent(text)).x + 1, Mathf.Max(80f, width * 0.8f));
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, style, GUILayout.Width(bubbleWidth));
            GUILayout.EndHorizontal();
        }

        private void DrawCard(LogEntry entry, double now)
        {
            ClaudiaSkin.BeginCard();
            GUILayout.BeginVertical(ClaudiaSkin.CardHeaderRow);
            GUILayout.BeginHorizontal();
            GUILayout.Label(KindText(entry.Kind), ClaudiaSkin.KindLabel, GUILayout.ExpandWidth(false));
            if (entry.Kind == LogKind.Debrief && entry.Outcome.Length > 0) ClaudiaSkin.Chip(entry.Outcome, OutcomeChip(entry.Outcome));
            GUILayout.FlexibleSpace();
            // When the card was made, from now; the date is on hover.
            if (entry.Ut > 0 && now > 0) GUILayout.Label(TimeContent(entry.Ut, now), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            if (entry.Title.Length > 0) GUILayout.Label(entry.Title, _bold);
            GUILayout.EndVertical();

            if (entry.CustomScript)
            {
                ClaudiaSkin.Chip("Custom Script", ClaudiaSkin.ChipKind.Info);
                if (entry.CustomScriptReason.Length > 0) GUILayout.Label(entry.CustomScriptReason, ClaudiaSkin.Secondary);
            }
            GUILayout.Label(entry.Text, ClaudiaSkin.Body);
            if (entry.Why.Length > 0 || entry.Script.Length > 0)
            {
                GUILayout.BeginHorizontal();
                if (entry.Why.Length > 0 && ClaudiaSkin.SmallButton(_showWhy.Contains(entry) ? "Hide why" : "Why", GUILayout.ExpandWidth(false)))
                    Toggle(_showWhy, entry);
                if (entry.Script.Length > 0 && ClaudiaSkin.SmallButton(_showScript.Contains(entry) ? "Hide script" : "Show script", GUILayout.ExpandWidth(false)))
                    Toggle(_showScript, entry);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (_showWhy.Contains(entry)) GUILayout.Label("Why: " + entry.Why, ClaudiaSkin.Secondary);
            if (_showScript.Contains(entry)) GUILayout.TextArea(entry.Script, _script);
            ClaudiaSkin.EndCard();
        }

        private static string KindText(LogKind kind)
        {
            return kind == LogKind.MissionPlan ? "MISSION PLAN" : kind == LogKind.SequencePlan ? "SEQUENCE PLAN" : "DEBRIEF";
        }

        private static ClaudiaSkin.ChipKind OutcomeChip(string outcome)
        {
            var o = outcome.ToLowerInvariant();
            if (o.StartsWith("off", StringComparison.Ordinal)) return ClaudiaSkin.ChipKind.Attention;
            if (o.StartsWith("fail", StringComparison.Ordinal)) return ClaudiaSkin.ChipKind.Danger;
            return o.StartsWith("nominal", StringComparison.Ordinal) ? ClaudiaSkin.ChipKind.Success : ClaudiaSkin.ChipKind.Info;
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

        // ---- Craft Check (moved here from the Mission window as it was; the next ticket restyles it into a card) ----

        private void DrawCraftCheck(MissionView view)
        {
            var check = view.CraftCheck;
            var form = view.StartForm;

            // A check that needs the player is never hidden in Flight: Running, needs the simulator, errored, or Start Mission held closed.
            var needsPlayer = check.State == CraftCheckState.Running || check.State == CraftCheckState.NeedsSimulator
                || check.State == CraftCheckState.Error
                || (check.State == CraftCheckState.Done && form.CanStart && !check.StartAllowed);
            if (HighLogic.LoadedSceneIsFlight)
            {
                // Forced open without touching the player's choice, which comes back once the check no longer needs them.
                var collapsed = _checkCollapsed && !needsPlayer;
                GUILayout.BeginHorizontal();
                GUILayout.Label(collapsed ? CollapsedHeading(check) : "Craft Check", _bold);
                if (!needsPlayer && ClaudiaSkin.SmallButton(collapsed ? "Expand" : "Collapse", GUILayout.ExpandWidth(false)))
                    _checkCollapsed = !_checkCollapsed;
                GUILayout.EndHorizontal();
                if (collapsed) return;
            }
            else
            {
                GUILayout.Label("Craft Check", _bold);
            }

            // Chosen in the Settings window.
            foreach (var processor in check.Processors)
                if (processor.Chosen) GUILayout.Label("Claudia's kOS processor: " + processor.Title, ClaudiaSkin.Secondary);

            // Any result after a pause (Running, Error, ...) is new, even if it matches the last one.
            if (check.State != CraftCheckState.Done) _seenCheckResult = "";
            switch (check.State)
            {
                case CraftCheckState.None:
                    GUILayout.Label("Not checked yet. Start Mission checks the craft first, or press Check again.", ClaudiaSkin.Body);
                    break;
                case CraftCheckState.Running:
                    GUILayout.Label("Claudia is checking the craft...", ClaudiaSkin.Body);
                    break;
                case CraftCheckState.NeedsSimulator:
                case CraftCheckState.Error:
                    GUILayout.Label(check.Message, ClaudiaSkin.Body);
                    break;
                case CraftCheckState.Done:
                    DrawCraftCheckResult(view);
                    break;
            }
            if (form.CanStart && !check.StartAllowed && check.State == CraftCheckState.Done && !check.Stale)
                GUILayout.Label(check.Verdict == CraftVerdict.Fail
                    ? "Start Mission is closed: fix every Blocker in the craft and check again."
                    : "Start Mission opens once you have acknowledged every Warning.", ClaudiaSkin.Body);
        }

        private static string VerdictText(CraftCheckView check)
        {
            return check.Verdict == CraftVerdict.Pass ? "Pass"
                : check.Verdict == CraftVerdict.PassWithWarnings ? "Pass with Warnings" : "Fail";
        }

        /// <summary>The one line shown while collapsed, e.g. "Craft Check: Pass with Warnings (2)".</summary>
        private static string CollapsedHeading(CraftCheckView check)
        {
            if (check.State == CraftCheckState.None) return "Craft Check: not checked yet";
            if (check.State != CraftCheckState.Done) return "Craft Check";
            return "Craft Check: " + VerdictText(check)
                + (check.Verdict == CraftVerdict.Pass ? "" : " (" + check.Findings.Count.ToString(CultureInfo.InvariantCulture) + ")")
                + (check.Stale ? " - the craft changed since" : "");
        }

        private void DrawCraftCheckResult(MissionView view)
        {
            var check = view.CraftCheck;
            GUILayout.Label(VerdictText(check) + (check.Stale ? " (the craft changed since; Start Mission checks again)" : ""), _bold);
            var signature = check.Summary;
            foreach (var finding in check.Findings) signature += "|" + finding.Code;
            if (signature != _seenCheckResult)
            {
                _seenCheckResult = signature;
                // Before a Mission the Craft Check is what the player reads; during one the log keeps the bottom.
                if (view.StartForm.CanStart) _scroll.y = 0;
            }
            if (check.Summary.Length > 0) GUILayout.Label("Claudia: " + check.Summary, ClaudiaSkin.Body);
            foreach (var finding in check.Findings)
            {
                ClaudiaSkin.BeginCard();
                GUILayout.Label((finding.Severity == CraftFindingSeverity.Blocker ? "Blocker: " : "Warning: ") + finding.Title, _bold);
                GUILayout.Label(finding.Explanation, ClaudiaSkin.Body);
                if (finding.Fix.Length > 0) GUILayout.Label("Fix: " + finding.Fix, ClaudiaSkin.Body);
                if (finding.Severity == CraftFindingSeverity.Warning)
                {
                    var acknowledged = GUILayout.Toggle(finding.Acknowledged, "I accept this risk");
                    if (acknowledged && !finding.Acknowledged) _send(new AcknowledgeWarning(finding.Code));
                }
                ClaudiaSkin.EndCard();
            }
        }
    }
}
