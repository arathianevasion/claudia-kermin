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
    /// Draws the transcript: the player's lines as bubbles, Claudia's under her name, notices as muted lines, and the
    /// Mission Plan, Sequence Plans and Debriefs as cards with a "Why" expander and, for a Sequence Plan, "Show script";
    /// then the Busy line. Given a send, it leads with the Craft Check as a card (its Warnings are checkboxes that acknowledge
    /// them) and ends with the checkbox that confirms a typed approval. Used by the <see cref="MissionPanel"/>, which the
    /// floating Mission window and the docked Capcom strip both draw. Plain text only:
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
        // When the current result first reached the view, for the card's "in -2m" time.
        private double _checkedAt;
        // The approval checkbox appeared: the transcript follows it to the bottom.
        private bool _seenConfirm;
        // The width the entries get; the facts row wraps at it.
        private float _width;

        /// <summary>The Mission Goal is pinned (the card's notice says so); set before <see cref="Draw"/>.</summary>
        public bool GoalSet;
        /// <summary>The estimated cost of the Mission for the chosen model, for the facts row; null for none.</summary>
        public decimal? Estimate;
        /// <summary>Builds the input Check again sends, or null when it can't be sent yet (no Mission Goal pinned).</summary>
        public Func<MissionInput> CheckAgain;
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
            _width = width;
            var confirming = view.ConfirmPrompt != null;
            if (view.Log.Count != _seenLogCount || (confirming && !_seenConfirm))
            {
                _seenLogCount = view.Log.Count;
                _scroll.y = float.MaxValue;
            }
            _seenConfirm = confirming;
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, options);
            if (_send != null && (view.StartForm.CanStart || view.CraftCheck.State != CraftCheckState.None)) DrawCraftCheck(view);
            if (_send != null && view.StartForm.CanStart && view.StartForm.Refusal.Length > 0) GUILayout.Label(view.StartForm.Refusal, ClaudiaSkin.DangerText);
            foreach (var entry in view.Log) DrawEntry(entry, view.Ut, width);
            if (_send != null && confirming) DrawApproval(view);
            if (view.Busy) GUILayout.Label("Claudia is " + (view.Activity.Length > 0 ? view.Activity : "thinking") + "…", ClaudiaSkin.Muted);
            GUILayout.EndScrollView();
        }

        private void Styles()
        {
            if (_bold != null && ReferenceEquals(_stylesFor, HighLogic.Skin)) return;
            _stylesFor = HighLogic.Skin;
            _bold = new GUIStyle(ClaudiaSkin.Body) { fontStyle = FontStyle.Bold };
            // Scripts wrap to the card instead of running off the right edge; the transcript scrolls them.
            _script = new GUIStyle(ClaudiaSkin.TextField) { wordWrap = true, font = ClaudiaSkin.Mono.font, fontSize = ClaudiaSkin.Scaled(12) };
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
            // Read before the buttons: a click mustn't add the expanded text to an event that was laid out without it.
            var showWhy = _showWhy.Contains(entry);
            var showScript = _showScript.Contains(entry);
            if (entry.Why.Length > 0 || entry.Script.Length > 0)
            {
                GUILayout.BeginHorizontal();
                if (entry.Why.Length > 0 && ClaudiaSkin.SmallButton(showWhy ? "Hide why" : "Why", GUILayout.ExpandWidth(false)))
                    Toggle(_showWhy, entry);
                if (entry.Script.Length > 0 && ClaudiaSkin.SmallButton(showScript ? "Hide script" : "Show script", GUILayout.ExpandWidth(false)))
                    Toggle(_showScript, entry);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (showWhy) GUILayout.Label("Why: " + entry.Why, ClaudiaSkin.Secondary);
            if (showScript) GUILayout.TextArea(entry.Script, _script);
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

        // ---- Craft Check: the first item of the transcript ----

        private void DrawCraftCheck(MissionView view)
        {
            var check = view.CraftCheck;
            var form = view.StartForm;

            // A check that needs the player is never hidden in Flight: Running, needs the simulator, errored, or Start Mission held closed.
            var needsPlayer = check.State == CraftCheckState.Running || check.State == CraftCheckState.NeedsSimulator
                || check.State == CraftCheckState.Error
                || (check.State == CraftCheckState.Done && form.CanStart && !check.StartAllowed);
            var inFlight = HighLogic.LoadedSceneIsFlight;
            // Forced open without touching the player's choice, which comes back once the check no longer needs them.
            var collapsed = inFlight && _checkCollapsed && !needsPlayer;

            // Any result after a pause (Running, Error, ...) is new, even if it matches the last one.
            if (check.State != CraftCheckState.Done) _seenCheckResult = "";

            if (collapsed)
            {
                DrawCollapsed(check);
                return;
            }

            var notice = MissionWindowRules.CraftNotice(check, !HighLogic.LoadedSceneIsEditor, GoalSet);
            if (notice.Length > 0) GUILayout.Label(notice, ClaudiaSkin.Notice);

            if (check.State != CraftCheckState.Done)
            {
                DrawStateLine(check);
                return;
            }
            DrawResult(view, inFlight && !needsPlayer);
        }

        // In Flight the card is its header line with an expand control.
        private void DrawCollapsed(CraftCheckView check)
        {
            ClaudiaSkin.BeginCard();
            GUILayout.BeginHorizontal();
            if (check.State == CraftCheckState.Done) VerdictIcon(check.Verdict);
            GUILayout.Label(MissionWindowRules.CollapsedTitle(check), _bold);
            GUILayout.FlexibleSpace();
            if (ClaudiaSkin.SmallButton("Expand", GUILayout.ExpandWidth(false))) _checkCollapsed = false;
            GUILayout.EndHorizontal();
            ClaudiaSkin.EndCard();
        }

        // None reads as a notice; Running, NeedsSimulator and Error as Claudia's line, with Check again where it helps.
        private void DrawStateLine(CraftCheckView check)
        {
            var line = MissionWindowRules.StateLine(check);
            var none = check.State == CraftCheckState.None;
            if (none) GUILayout.Label(line, ClaudiaSkin.Notice);
            else DrawClaudiaLine(line);

            // Check again needs the pinned goal, which only the Mission window knows; the strip's drawer has no button.
            var offer = MissionWindowRules.OffersCheckAgain(check.State) && CheckAgain != null;
            // An expanded "not checked yet" in Flight can be put away again.
            var collapsible = none && HighLogic.LoadedSceneIsFlight;
            if (!offer && !collapsible) return;
            GUILayout.BeginHorizontal();
            if (offer)
            {
                var input = CheckAgain();
                var wasEnabled = GUI.enabled;
                GUI.enabled = input != null;
                if (ClaudiaSkin.SecondaryButton("Check again", GUILayout.ExpandWidth(false)) && input != null) _send(input);
                GUI.enabled = wasEnabled;
            }
            if (collapsible && ClaudiaSkin.SmallButton("Collapse", GUILayout.ExpandWidth(false))) _checkCollapsed = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void DrawClaudiaLine(string text)
        {
            GUILayout.Label("Claudia", ClaudiaSkin.ClaudiaName);
            GUILayout.Label(text, ClaudiaSkin.Body);
        }

        // Amber triangle for Pass with Warnings, red for Fail, green check for Pass.
        private static void VerdictIcon(CraftVerdict verdict)
        {
            var tone = MissionWindowRules.VerdictTone(verdict);
            ClaudiaSkin.IconLabel(verdict == CraftVerdict.Pass ? ClaudiaSkin.IconShape.Check : ClaudiaSkin.IconShape.Warning,
                tone == ChipTone.Success ? ClaudiaSkin.IconGreen : tone == ChipTone.Danger ? ClaudiaSkin.Danger : ClaudiaSkin.Attention, 16);
        }

        private void DrawResult(MissionView view, bool canCollapse)
        {
            var check = view.CraftCheck;
            var signature = check.Summary;
            foreach (var finding in check.Findings) signature += "|" + finding.Code;
            if (signature != _seenCheckResult)
            {
                _seenCheckResult = signature;
                _checkedAt = view.Ut;
                // Before a Mission the Craft Check is what the player reads; during one the log keeps the bottom.
                if (view.StartForm.CanStart) _scroll.y = 0;
            }
            var summary = MissionWindowRules.ClaudiaLine(check);
            if (summary.Length > 0) DrawClaudiaLine(summary);

            ClaudiaSkin.BeginCard();
            GUILayout.BeginHorizontal();
            VerdictIcon(check.Verdict);
            GUILayout.Label(MissionWindowRules.CardTitle(check), _bold);
            GUILayout.FlexibleSpace();
            if (_checkedAt > 0 && view.Ut > 0) GUILayout.Label(TimeContent(_checkedAt, view.Ut), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            if (canCollapse && ClaudiaSkin.SmallButton("Collapse", GUILayout.ExpandWidth(false))) _checkCollapsed = true;
            GUILayout.EndHorizontal();

            DrawFacts(MissionWindowRules.Facts(check, Estimate));
            foreach (var finding in check.Findings) DrawFinding(finding);

            if (view.StartForm.CanStart)
            {
                var footer = MissionWindowRules.Footer(check);
                if (footer.Text.Length > 0) GUILayout.Label(footer.Text, footer.Tone == ChipTone.Success ? ClaudiaSkin.SuccessText : ClaudiaSkin.Secondary);
            }
            ClaudiaSkin.EndCard();
        }

        // Small chips that wrap onto further lines when the row is full.
        private void DrawFacts(IList<StatusChip> facts)
        {
            // The card's own padding comes off the entry width too.
            var room = _width - 4 * ClaudiaSkin.Padding;
            var used = 0f;
            var open = false;
            foreach (var chip in facts)
            {
                var kind = ClaudiaSkin.KindOf(chip.Tone);
                var size = ClaudiaSkin.ChipStyle(kind).CalcSize(new GUIContent(chip.Text)).x + ClaudiaSkin.Gap;
                if (!open || (used > 0 && used + size > room))
                {
                    if (open) GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    open = true;
                    used = 0;
                }
                ClaudiaSkin.Chip(chip.Text, kind);
                used += size;
            }
            if (open) GUILayout.EndHorizontal();
        }

        // A Warning gets the box and a checkbox that acknowledges it; a Blocker the box alone.
        private void DrawFinding(CraftFinding finding)
        {
            var warning = finding.Severity == CraftFindingSeverity.Warning;
            GUILayout.BeginVertical(warning ? ClaudiaSkin.WarningBox : ClaudiaSkin.BlockerBox);
            GUILayout.Label(MissionWindowRules.FindingLabel(finding.Severity), warning ? ClaudiaSkin.WarningLabel : ClaudiaSkin.BlockerLabel);
            GUILayout.Label(finding.Title, _bold);
            GUILayout.Label(finding.Explanation, ClaudiaSkin.Secondary);
            if (finding.Fix.Length > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Fix:", _bold, GUILayout.ExpandWidth(false));
                GUILayout.Label(finding.Fix, ClaudiaSkin.Body);
                GUILayout.EndHorizontal();
            }
            if (warning)
            {
                // The core can't un-acknowledge: once it has, the box stays checked and takes no clicks.
                var acknowledged = ClaudiaSkin.Checkbox(finding.Acknowledged, "I accept this risk", !finding.Acknowledged);
                if (acknowledged && !finding.Acknowledged) _send(new AcknowledgeWarning(finding.Code));
            }
            GUILayout.EndVertical();
        }

        // ---- Approval: a typed reply that reads like an approval ends the transcript with a checkbox ----

        /// <summary>Claudia's ask and the box with the checkbox and Not yet.</summary>
        private void DrawApproval(MissionView view)
        {
            Styles();
            DrawClaudiaLine(MissionWindowRules.ApprovalAsk);
            GUILayout.BeginVertical(ClaudiaSkin.AskBox);
            if (ClaudiaSkin.Checkbox(false, MissionWindowRules.ApprovalLabel(view))) _send(new ConfirmApproval(true));
            GUILayout.BeginHorizontal();
            if (ClaudiaSkin.TextButton("Not yet", GUILayout.ExpandWidth(false))) _send(new ConfirmApproval(false));
            GUILayout.Label("Or keep typing to change it.", ClaudiaSkin.Secondary);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
    }
}

