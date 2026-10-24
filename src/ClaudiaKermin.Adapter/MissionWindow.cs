// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Mission window: the start form when no Mission is active, otherwise the log drawer (chat lines,
    /// with the Mission Plan, Sequence Plans and Debriefs as cards), the quick replies and the reply box.
    /// It only draws <see cref="MissionView"/> and turns clicks into <see cref="MissionInput"/>s.
    /// </summary>
    public class MissionWindow
    {
        private const string InputControl = "ClaudiaMissionInput";
        private const string LockId = "ClaudiaKerminMission";

        private enum TypingFor
        {
            /// <summary>A reply to a pending plan (classified by Claude), or a question when nothing is pending.</summary>
            Reply,
            Change,
            Question,
            /// <summary>The note for Retry with a note, after "Couldn't produce a safe script".</summary>
            Note,
        }

        private readonly Action<MissionInput> _send;
        private readonly bool _debugTools;
        // Large enough by default to read a Sequence Plan card and its script without resizing.
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B03, "Claudia: Mission", LockId, new Rect(260, 80, 700, 700), new Vector2(420, 360), true);
        private Vector2 _scroll;
        private int _seenLogCount = -1;
        private GUIStyle _wrap;
        private GUIStyle _bold;
        private GUIStyle _script;
        private GUIStyle _goalStyle;

        // Start form.
        private string _goal = "";
        private Vector2 _goalScroll;
        // The Craft Check findings scroll; a new result (a different set of findings) jumps back to the top.
        private Vector2 _checkScroll;
        private string _seenCheckResult = "";
        // End Mission asked "End this Mission?" and awaits Yes / No.
        private bool _confirmingEnd;
        private string _cap;
        private int _modelIndex;
        // Raise cap asked for a new Spending Cap and awaits the amount.
        private bool _raising;
        private string _raiseTo = "";

        // Reply box.
        private string _typed = "";
        private TypingFor _typingFor = TypingFor.Reply;

        // Expanded "why" and "Show script" per log entry.
        private readonly HashSet<LogEntry> _showWhy = new HashSet<LogEntry>();
        private readonly HashSet<LogEntry> _showScript = new HashSet<LogEntry>();

        public bool Visible;
        public bool UiHidden;

        /// <summary>Set before each OnGUI.</summary>
        public MissionView View;

        public MissionWindow(Action<MissionInput> send, bool debugTools)
        {
            _send = send;
            _debugTools = debugTools;
        }

        public void OnGUI()
        {
            if (!Visible || UiHidden || View == null)
            {
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            _frame.Show(Draw);

            // Keys typed into the window must not stage, throttle or rotate the craft.
            _frame.SetTyping(GUI.GetNameOfFocusedControl() == InputControl);
        }

        /// <summary>Releases the input locks; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        /// <summary>A quick reply from here or the Capcom strip. Change and Ask pick what the reply box sends.</summary>
        public void QuickReplied(QuickReply reply)
        {
            switch (reply)
            {
                case QuickReply.Approve: _send(new Approve()); break;
                case QuickReply.ConfirmApproval: _send(new ConfirmApproval(true)); break;
                case QuickReply.NotYet: _send(new ConfirmApproval(false)); break;
                case QuickReply.Retry: _send(new Retry()); break;
                case QuickReply.RaiseCap:
                    _raising = true;
                    Visible = true;
                    break;
                case QuickReply.EndMission:
                    _confirmingEnd = true;
                    Visible = true;
                    break;
                case QuickReply.Resume: _send(new Resume()); break;
                case QuickReply.Replan: _send(new Replan()); break;
                case QuickReply.RetryWithNote:
                    _typingFor = TypingFor.Note;
                    Visible = true;
                    break;
                case QuickReply.ChangeSomething:
                    _typingFor = TypingFor.Change;
                    Visible = true;
                    break;
                case QuickReply.AskQuestion:
                    _typingFor = TypingFor.Question;
                    Visible = true;
                    break;
            }
        }

        public static string Label(QuickReply reply)
        {
            switch (reply)
            {
                case QuickReply.Approve: return "Approve";
                case QuickReply.ChangeSomething: return "Change something";
                case QuickReply.AskQuestion: return "Ask a question";
                case QuickReply.ConfirmApproval: return "Yes, approve";
                case QuickReply.NotYet: return "Not yet";
                case QuickReply.RaiseCap: return "Raise cap";
                case QuickReply.EndMission: return "End Mission";
                case QuickReply.Resume: return "Resume";
                case QuickReply.RetryWithNote: return "Retry with a note";
                case QuickReply.Replan: return "Re-plan";
                default: return "Retry";
            }
        }

        private void Draw(int id)
        {
            if (_wrap == null)
            {
                _wrap = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };
                _bold = new GUIStyle(_wrap) { fontStyle = FontStyle.Bold };
                // Scripts wrap to the card instead of running off the right edge; the log scrolls them.
                _script = new GUIStyle(GUI.skin.textArea) { wordWrap = true, richText = false };
                _goalStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true, richText = false };
            }
            if (View.StartForm.CanStart) DrawStartForm();
            // The start form's goal is spent once a Mission is under way.
            if (View.CanEndMission) _goal = "";
            if (View.Stage != MissionStage.None) DrawMission();
            if (View.StartForm.CanStart || View.CraftCheck.State != CraftCheckState.None) DrawCraftCheck();
            if (_debugTools)
            {
                GUI.enabled = View.CanCaptureSnapshot;
                if (GUILayout.Button("Capture Game Snapshot (debug)")) _send(new CaptureSnapshot());
                GUI.enabled = true;
            }
            _frame.DrawGrip();
            GUI.DragWindow();
        }

        private void DrawStartForm()
        {
            var form = View.StartForm;
            if (_cap == null) _cap = form.DefaultSpendingCap.ToString("0.00", CultureInfo.InvariantCulture);
            GUILayout.Label("Start a Mission", _bold);
            GUILayout.Label("Mission Goal:");
            // About 9 lines by default; taller as the player resizes the window.
            var goalHeight = Mathf.Max(9f * _goalStyle.lineHeight + _goalStyle.padding.vertical, _frame.Rect.height * 0.4f);
            _goalScroll = GUILayout.BeginScrollView(_goalScroll, GUILayout.Height(goalHeight));
            GUI.SetNextControlName(InputControl);
            _goal = GUILayout.TextArea(_goal, 4000, _goalStyle);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Clear", GUILayout.ExpandWidth(false))) _goal = "";
            GUILayout.BeginHorizontal();
            GUILayout.Label("Spending Cap $", GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(InputControl);
            _cap = GUILayout.TextField(_cap, 10, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            GUILayout.Label("Model (locked for the Mission):");
            var names = new string[form.Models.Count];
            for (var i = 0; i < names.Length; i++)
            {
                decimal estimate;
                names[i] = form.Models[i] + (form.Models[i] == form.DefaultModel ? " (default)" : "")
                    + (form.Estimates.TryGetValue(form.Models[i], out estimate)
                        ? "  est. Mission cost ~$" + estimate.ToString("0.00", CultureInfo.InvariantCulture) : "");
            }
            _modelIndex = Mathf.Clamp(_modelIndex, 0, Math.Max(0, names.Length - 1));
            _modelIndex = GUILayout.SelectionGrid(_modelIndex, names, 1);

            decimal cap;
            var capOk = decimal.TryParse(_cap, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > 0;
            // Start Mission runs the Craft Check first; it is disabled on a Blocker and until every Warning is acknowledged.
            GUI.enabled = capOk && _goal.Trim().Length > 0 && names.Length > 0 && View.CraftCheck.StartAllowed;
            if (GUILayout.Button("Start Mission"))
            {
                // The goal stays until the Mission has actually started: the Craft Check may still hold it back.
                _send(new StartMission(_goal, cap, form.Models[_modelIndex]));
            }
            GUI.enabled = true;
            if (!capOk) GUILayout.Label("The Spending Cap must be a positive number of dollars.", _wrap);
        }

        /// <summary>The Craft Check, pinned at the bottom of the Mission board.</summary>
        private void DrawCraftCheck()
        {
            var check = View.CraftCheck;
            var form = View.StartForm;
            GUILayout.Box("", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            GUILayout.Label("Craft Check", _bold);

            if (form.CanStart)
            {
                GUI.enabled = _goal.Trim().Length > 0 && form.Models.Count > 0 && check.State != CraftCheckState.Running;
                if (GUILayout.Button("Check craft", GUILayout.ExpandWidth(false)))
                    _send(new RunCraftCheck(_goal, form.Models[Mathf.Clamp(_modelIndex, 0, form.Models.Count - 1)]));
                GUI.enabled = true;
            }
            DrawProcessorChoice(check);

            switch (check.State)
            {
                case CraftCheckState.None:
                    GUILayout.Label("Not checked yet. Start Mission checks the craft first, or press Check craft.", _wrap);
                    break;
                case CraftCheckState.Running:
                    GUILayout.Label("Claudia is checking the craft...", _wrap);
                    break;
                case CraftCheckState.NeedsSimulator:
                case CraftCheckState.Error:
                    GUILayout.Label(check.Message, _wrap);
                    break;
                case CraftCheckState.Done:
                    DrawCraftCheckResult(check);
                    break;
            }
            if (form.CanStart && !check.StartAllowed && check.State == CraftCheckState.Done && !check.Stale)
                GUILayout.Label(check.Verdict == CraftVerdict.Fail
                    ? "Start Mission is closed: fix every Blocker in the craft and check again."
                    : "Start Mission opens once you have acknowledged every Warning.", _wrap);
        }

        private void DrawProcessorChoice(CraftCheckView check)
        {
            if (check.Processors.Count == 0) return;
            GUILayout.Label("Claudia's kOS processor (default: the one nearest the root):", _wrap);
            foreach (var processor in check.Processors)
            {
                var label = (processor.Chosen ? "(x) " : "( ) ") + processor.Title
                    + (processor.BootFile.Length > 0 ? " [boot file: " + processor.BootFile + "]" : "");
                if (GUILayout.Button(label, GUILayout.ExpandWidth(false)) && !processor.Chosen) _send(new ChooseProcessor(processor.PartId));
            }
            if (GUILayout.Button("Use the one nearest the root", GUILayout.ExpandWidth(false))) _send(new ChooseProcessor(null));
        }

        private void DrawCraftCheckResult(CraftCheckView check)
        {
            var verdict = check.Verdict == CraftVerdict.Pass ? "Pass"
                : check.Verdict == CraftVerdict.PassWithWarnings ? "Pass with Warnings" : "Fail";
            GUILayout.Label(verdict + (check.Stale ? " (the craft changed since; Start Mission checks again)" : ""), _bold);
            var signature = check.Summary;
            foreach (var finding in check.Findings) signature += "|" + finding.Code;
            if (signature != _seenCheckResult)
            {
                _seenCheckResult = signature;
                _checkScroll = Vector2.zero;
            }
            // Takes the space left over, but no more than 45% of the window, so a Mission log below keeps room.
            _checkScroll = GUILayout.BeginScrollView(_checkScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(_frame.Rect.height * 0.45f), GUILayout.ExpandHeight(true));
            if (check.Summary.Length > 0) GUILayout.Label("Claudia: " + check.Summary, _wrap);
            foreach (var finding in check.Findings)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label((finding.Severity == CraftFindingSeverity.Blocker ? "Blocker: " : "Warning: ") + finding.Title, _bold);
                GUILayout.Label(finding.Explanation, _wrap);
                if (finding.Fix.Length > 0) GUILayout.Label("Fix: " + finding.Fix, _wrap);
                if (finding.Severity == CraftFindingSeverity.Warning)
                {
                    var acknowledged = GUILayout.Toggle(finding.Acknowledged, "I accept this risk");
                    if (acknowledged && !finding.Acknowledged) _send(new AcknowledgeWarning(finding.Code));
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }

        private void DrawMission()
        {
            GUILayout.Label("Mission Goal: " + View.Goal, _wrap);
            GUILayout.Label("Model: " + View.Model + "   Spent: " + SpendText(View) + "   " + StageText(View), _wrap);

            if (View.Log.Count != _seenLogCount)
            {
                _seenLogCount = View.Log.Count;
                _scroll.y = float.MaxValue;
            }
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (var entry in View.Log) DrawEntry(entry);
            if (View.Busy) GUILayout.Label("Claudia: (" + (View.Activity.Length > 0 ? View.Activity : "thinking") + "...)", _wrap);
            GUILayout.EndScrollView();

            if (View.ConfirmPrompt != null) GUILayout.Label(View.ConfirmPrompt, _bold);
            if (View.QuickReplies.Count > 0)
            {
                GUILayout.BeginHorizontal();
                foreach (var reply in View.QuickReplies)
                    if (GUILayout.Button(Label(reply))) QuickReplied(reply);
                GUILayout.EndHorizontal();
            }
            DrawRaiseCap();
            DrawReplyBox();
            DrawEndMission();
        }

        // Raise cap: the Mission is Paused at its Spending Cap; the player picks a higher cap and it resumes.
        private void DrawRaiseCap()
        {
            if (!View.QuickReplies.Contains(QuickReply.RaiseCap))
            {
                _raising = false;
                _raiseTo = "";
                return;
            }
            if (!_raising) return;
            if (_raiseTo.Length == 0) _raiseTo = (View.SpendingCap * 2).ToString("0.00", CultureInfo.InvariantCulture);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Raise Spending Cap to $", GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(InputControl);
            _raiseTo = GUILayout.TextField(_raiseTo, 10, GUILayout.Width(80));
            decimal cap;
            GUI.enabled = decimal.TryParse(_raiseTo, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > View.SpendingCap;
            if (GUILayout.Button("Raise", GUILayout.ExpandWidth(false)))
            {
                _send(new RaiseCap(cap));
                _raising = false;
                _raiseTo = "";
            }
            GUI.enabled = true;
            if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false))) _raising = false;
            GUILayout.EndHorizontal();
        }

        /// <summary>The cost so far against the Spending Cap, e.g. "$1.08 of $10.00", with the 80% warning.</summary>
        public static string SpendText(MissionView view)
        {
            var text = "$" + (view.Spent + view.HeldCharges).ToString("0.00", CultureInfo.InvariantCulture)
                + " of $" + view.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture);
            return view.CapWarning ? text + " (80% of the cap or more)" : text;
        }

        // The Mission Goal of a started Mission is shown read-only above; End Mission needs a confirmation.
        private void DrawEndMission()
        {
            if (!View.CanEndMission)
            {
                _confirmingEnd = false;
                return;
            }
            if (!_confirmingEnd)
            {
                if (GUILayout.Button("End Mission", GUILayout.ExpandWidth(false))) _confirmingEnd = true;
                return;
            }
            GUILayout.Label("End this Mission? Its folder and transcript are kept.", _bold);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Yes, end it"))
            {
                _confirmingEnd = false;
                _send(new EndMission());
            }
            if (GUILayout.Button("No")) _confirmingEnd = false;
            GUILayout.EndHorizontal();
        }

        private void DrawReplyBox()
        {
            if (!View.CanType) return;
            var awaiting = View.Stage == MissionStage.AwaitingPlanApproval || View.Stage == MissionStage.AwaitingSequenceApproval;
            var unsafeScript = View.Stage == MissionStage.UnsafeScript;
            if (!awaiting && _typingFor != TypingFor.Question && !(unsafeScript && _typingFor == TypingFor.Note)) _typingFor = TypingFor.Reply;
            GUILayout.Label(_typingFor == TypingFor.Change ? "What should change?"
                : _typingFor == TypingFor.Note ? "Note for Claudia's retry:"
                : _typingFor == TypingFor.Question || !awaiting ? "Ask Claudia:" : "Reply to Claudia:");

            var enter = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == InputControl;
            GUI.SetNextControlName(InputControl);
            _typed = GUILayout.TextField(_typed, 2000);

            GUI.enabled = _typed.Trim().Length > 0;
            if (GUILayout.Button("Send") || (enter && GUI.enabled))
            {
                if (_typingFor == TypingFor.Change) _send(new ChangeSomething(_typed));
                else if (_typingFor == TypingFor.Note) _send(new RetryWithNote(_typed));
                else if (_typingFor == TypingFor.Question || !awaiting) _send(new AskQuestion(_typed));
                else _send(new Reply(_typed));
                _typed = "";
                _typingFor = TypingFor.Reply;
                if (enter) Event.current.Use();
            }
            GUI.enabled = true;
        }

        // Plain text only: Claude's replies may contain '<', which rich text would misread.
        private void DrawEntry(LogEntry entry)
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

        private static void Toggle(HashSet<LogEntry> set, LogEntry entry)
        {
            if (!set.Remove(entry)) set.Add(entry);
        }

        public static string StageText(MissionView view)
        {
            switch (view.Stage)
            {
                case MissionStage.WaitingForFlight: return "Waiting for Flight";
                case MissionStage.Planning: return "Planning";
                case MissionStage.AwaitingPlanApproval: return "Mission Plan awaiting Approval";
                case MissionStage.Sequencing: return "Planning the next sequence";
                case MissionStage.AwaitingSequenceApproval: return "Sequence Plan awaiting Approval";
                case MissionStage.Running: return "Running";
                case MissionStage.Debriefing: return "Debriefing";
                case MissionStage.Succeeded: return "Mission Goal met";
                case MissionStage.Ended: return "Mission ended (Failed Debrief)";
                case MissionStage.Paused: return "Paused: " + view.PauseReason;
                case MissionStage.UnsafeScript: return "Couldn't produce a safe script";
                default: return "";
            }
        }
    }
}
