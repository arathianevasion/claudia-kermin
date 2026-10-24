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

        // Start form.
        private string _goal = "";
        private string _cap;
        private int _modelIndex;

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
            }
            if (View.StartForm.CanStart) DrawStartForm();
            if (View.Stage != MissionStage.None) DrawMission();
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
            GUI.SetNextControlName(InputControl);
            _goal = GUILayout.TextArea(_goal, 1000, GUILayout.MinHeight(54));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Spending Cap $", GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(InputControl);
            _cap = GUILayout.TextField(_cap, 10, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            GUILayout.Label("Model (locked for the Mission):");
            var names = new string[form.Models.Count];
            for (var i = 0; i < names.Length; i++) names[i] = form.Models[i] + (form.Models[i] == form.DefaultModel ? " (default)" : "");
            _modelIndex = Mathf.Clamp(_modelIndex, 0, Math.Max(0, names.Length - 1));
            _modelIndex = GUILayout.SelectionGrid(_modelIndex, names, 1);

            decimal cap;
            var capOk = decimal.TryParse(_cap, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > 0;
            GUI.enabled = capOk && _goal.Trim().Length > 0 && names.Length > 0;
            if (GUILayout.Button("Start Mission"))
            {
                _send(new StartMission(_goal, cap, form.Models[_modelIndex]));
                _goal = "";
            }
            GUI.enabled = true;
            if (!capOk) GUILayout.Label("The Spending Cap must be a positive number of dollars.", _wrap);
        }

        private void DrawMission()
        {
            GUILayout.Label("Mission Goal: " + View.Goal, _wrap);
            GUILayout.Label("Model: " + View.Model + "   Spending Cap: $" + View.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture)
                + "   " + StageText(View), _wrap);

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
            DrawReplyBox();
        }

        private void DrawReplyBox()
        {
            if (!View.CanType) return;
            var awaiting = View.Stage == MissionStage.AwaitingPlanApproval || View.Stage == MissionStage.AwaitingSequenceApproval;
            if (!awaiting && _typingFor != TypingFor.Question) _typingFor = TypingFor.Reply;
            GUILayout.Label(_typingFor == TypingFor.Change ? "What should change?"
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
            GUILayout.Label(entry.Kind == LogKind.SequencePlan ? "Sequence Plan: " + entry.Title : entry.Title, _bold);
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
                default: return "";
            }
        }
    }
}
