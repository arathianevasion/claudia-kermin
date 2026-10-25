// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Mission window's body, drawn by the floating <see cref="MissionWindow"/> and by the <see cref="CapcomStrip"/>
    /// when it is docked and expanded, so both show the same thing. Top to bottom: the pinned Mission Goal, the transcript
    /// (the Craft Check card, the Mission log, the approval checkbox, the Busy line), the prompt (which grows from 1 to 7
    /// lines and takes that height from the transcript), the action row of quick replies, and the control bar. Before a
    /// Mission the prompt writes the Mission Goal; type, model, Spending Cap and Mission Settings come from the
    /// <see cref="MissionSetup"/>, which the Settings window edits. It only draws <see cref="MissionView"/> and turns
    /// clicks into <see cref="MissionInput"/>s. One panel holds one prompt and transcript, whichever host draws it.
    /// </summary>
    public class MissionPanel
    {
        private const string InputControl = "ClaudiaMissionInput";
        private const string RaiseControl = "ClaudiaMissionRaise";
        private const int MaxPromptLines = 7;
        private static readonly float SendSize = ClaudiaSkin.Scaled(32);
        private static readonly float ControlBarHeight = ClaudiaSkin.Scaled(44);

        private readonly Action<MissionInput> _send;
        private readonly bool _debugTools;
        private readonly LogDrawer _log;
        private GUIStyle _controlBar, _summary;
        private GUIStyle _builtFrom;
        private int _builtRoom = -1;

        // The Mission being set up: its type, model, Spending Cap and Mission Settings (the Settings window edits them).
        private readonly MissionSetup _setup;
        // The Mission Goal pinned before the start: "" until the prompt sends one.
        private string _goal = "";
        // Edit loaded the pinned goal back into the prompt.
        private bool _editingGoal;
        private bool _showAllGoal;
        // The goal clamped to 2 lines, kept until the goal or the width changes (measuring it takes a dozen passes).
        private string _clampedFrom = "";
        private float _clampedAt;
        private string _clamped = "";
        // End Mission asked "End this Mission?" and awaits Yes / No.
        private bool _confirmingEnd;
        // Raise cap asked for a new Spending Cap and awaits the amount.
        private bool _raising;
        private string _raiseTo = "";

        // The prompt.
        private string _typed = "";
        private PromptFor _promptFor = PromptFor.Reply;
        private bool _focusPrompt;
        // Enter was taken as Send; the newline character that follows it in its own event is swallowed too.
        private bool _swallowNewline;

        // Set at the start of each Draw.
        private MissionView _view;
        private float _width;

        /// <summary>Opens the Settings window; set by the owner.</summary>
        public Action OpenSettings;

        /// <summary>Docks (true) or undocks (false) the panel in Flight; set by the owner.</summary>
        public Action<bool> SetDocked;

        /// <summary>The floating window offers Dock: in Flight, undocked, with a Mission the strip shows; set by the owner.</summary>
        public bool OfferDock;

        public MissionPanel(Action<MissionInput> send, MissionSetup setup, bool debugTools)
        {
            _send = send;
            _setup = setup;
            _debugTools = debugTools;
            _log = new LogDrawer(send);
        }

        /// <summary>The prompt or the Raise cap field has the keyboard; the host locks the keyboard away from the craft while it does.</summary>
        public static bool Typing
        {
            get
            {
                var focused = GUI.GetNameOfFocusedControl();
                return focused == InputControl || focused == RaiseControl;
            }
        }

        /// <summary>Flight is loading: the Craft Check starts collapsed again.</summary>
        public void FlightLoading()
        {
            _log.FlightLoading();
        }

        /// <summary>
        /// Draws the panel in the current layout area, <paramref name="width"/> wide. <paramref name="docked"/> draws the
        /// docked panel's control bar (Settings, Undock, End Mission; the spend meter is in the strip); the floating
        /// window's has the spend meter and, in Flight, Dock. <paramref name="rightRoom"/> pads the control bar's right
        /// edge.
        /// </summary>
        public void Draw(MissionView view, float width, bool docked, int rightRoom)
        {
            _view = view;
            _width = width;
            Styles(rightRoom);
            // The start form's goal is spent once a Mission is under way.
            if (_view.CanEndMission)
            {
                _goal = "";
                _editingGoal = false;
            }
            var settingUp = _view.StartForm.CanStart;
            var prompt = MissionWindowRules.Prompt(_view, _promptFor, _goal.Length > 0 && !_editingGoal);

            DrawGoal(settingUp);
            SetCraftCheckContext(settingUp);
            GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(ClaudiaSkin.Padding);
            _log.Draw(_view, _width - 2 * ClaudiaSkin.Padding - 20, GUILayout.ExpandHeight(true));
            GUILayout.Space(ClaudiaSkin.Padding / 2f);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            DrawPrompt(prompt);
            DrawActions(settingUp, prompt.For);
            DrawControlBar(settingUp, docked);
        }

        /// <summary>A quick reply from the action row. Change and Ask pick what the prompt sends; pressed again, they let go.</summary>
        private void QuickReplied(QuickReply reply)
        {
            switch (reply)
            {
                case QuickReply.Approve: _send(new Approve()); break;
                case QuickReply.Retry: _send(new Retry()); break;
                case QuickReply.RaiseCap: _raising = true; break;
                case QuickReply.EndMission: _confirmingEnd = true; break;
                case QuickReply.Resume: _send(new Resume()); break;
                case QuickReply.Replan: _send(new Replan()); break;
                case QuickReply.WarpToDueTime: _send(new WarpToDueTime()); break;
                case QuickReply.GoToVessel: _send(new GoToVessel()); break;
                case QuickReply.ManualControl: _send(new TakeManualControl()); break;
                case QuickReply.HandBack:
                    // Whatever is in the prompt goes with it as the optional message about what the player did.
                    _send(new HandBack(_typed));
                    _typed = "";
                    _promptFor = PromptFor.Reply;
                    break;
                case QuickReply.RetryWithNote: PickPrompt(PromptFor.Note, false); break;
                case QuickReply.ChangeSomething: PickPrompt(PromptFor.Change, true); break;
                case QuickReply.AskQuestion: PickPrompt(PromptFor.Question, true); break;
            }
        }

        private void PickPrompt(PromptFor mode, bool toggles)
        {
            _promptFor = toggles && _promptFor == mode ? PromptFor.Reply : mode;
            _focusPrompt = _promptFor == mode;
        }

        // What the Craft Check card in the transcript needs from the setup: the pinned goal, the cost estimate and Check again.
        private void SetCraftCheckContext(bool settingUp)
        {
            var form = _view.StartForm;
            var model = _setup.ModelFor(form);
            decimal estimate;
            _log.GoalSet = settingUp && _goal.Length > 0;
            _log.SetupModel = model;
            _log.SetupRemote = _setup.Remote;
            _log.Estimate = settingUp && form.Estimates.TryGetValue(model, out estimate) ? estimate : (decimal?)null;
            _log.CheckAgain = () => _goal.Length > 0 && form.Models.Count > 0 && _view.CraftCheck.State != CraftCheckState.Running
                ? new RunCraftCheck(_goal, model, _setup.Remote) : null;
        }

        private void Styles(int rightRoom)
        {
            if (_controlBar != null && ReferenceEquals(_builtFrom, ClaudiaSkin.Bar) && _builtRoom == rightRoom) return;
            _builtFrom = ClaudiaSkin.Bar;
            _builtRoom = rightRoom;
            _controlBar = new GUIStyle(ClaudiaSkin.Bar) { padding = new RectOffset(ClaudiaSkin.Padding, rightRoom, 6, 6) };
            _summary = new GUIStyle(ClaudiaSkin.Secondary) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
        }

        // ---- Pinned Mission Goal ----

        private void DrawGoal(bool settingUp)
        {
            var goal = settingUp ? _goal : _view.Goal;
            var textWidth = _width - 2 * ClaudiaSkin.Padding;
            if (goal != _clampedFrom || textWidth != _clampedAt)
            {
                _clampedFrom = goal;
                _clampedAt = textWidth;
                _clamped = goal.Length > 0 ? ClaudiaSkin.ClampLines(goal, ClaudiaSkin.Body, textWidth, 2) : "";
            }
            var clamped = _clamped;
            var longGoal = clamped != goal;

            GUILayout.BeginVertical(ClaudiaSkin.Raised);
            GUILayout.BeginHorizontal();
            ClaudiaSkin.SectionLabel("Mission Goal", GUILayout.ExpandWidth(false));
            var step = HighLogic.LoadedSceneIsFlight && !settingUp ? MissionWindowRules.Step(_view) : "";
            if (step.Length > 0) GUILayout.Label("· " + step, ClaudiaSkin.Muted, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            if (settingUp && goal.Length > 0 && !_editingGoal && ClaudiaSkin.IconButton(ClaudiaSkin.IconShape.Pencil, "Edit", GUILayout.ExpandWidth(false)))
            {
                _typed = _goal;
                _editingGoal = true;
                _focusPrompt = true;
            }
            if ((longGoal || _showAllGoal) && ClaudiaSkin.TextButton(_showAllGoal ? "Show less" : "Show all", GUILayout.ExpandWidth(false)))
                _showAllGoal = !_showAllGoal;
            GUILayout.EndHorizontal();

            if (goal.Length == 0) GUILayout.Label(settingUp ? "Describe the Mission Goal below." : "No Mission yet.", ClaudiaSkin.Secondary);
            else GUILayout.Label(_showAllGoal ? goal : clamped, ClaudiaSkin.Body);
            GUILayout.EndVertical();
            ClaudiaSkin.Divider();
        }

        // ---- Prompt ----

        private void DrawPrompt(PromptState prompt)
        {
            ClaudiaSkin.Divider();
            GUILayout.BeginVertical(ClaudiaSkin.Raised);
            GUILayout.Label(prompt.Caption, ClaudiaSkin.Secondary);

            var style = ClaudiaSkin.Prompt;
            var width = _width - 2 * ClaudiaSkin.Padding;
            var rect = GUILayoutUtility.GetRect(width, PromptHeight(style, width), style, GUILayout.ExpandWidth(true));
            var send = new Rect(rect.xMax - SendSize - 4, rect.yMax - SendSize - 4, SendSize, SendSize);
            var e = Event.current;
            var focused = GUI.GetNameOfFocusedControl() == InputControl;
            var canSend = prompt.Enabled && _typed.Trim().Length > 0;

            // Enter sends and Shift+Enter adds a line: Enter is taken before the text area sees it, both as the key and as its newline character.
            var pressed = false;
            if (focused && prompt.Enabled && e.type == EventType.KeyDown && !e.shift)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    pressed = true;
                    _swallowNewline = true;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.None && (e.character == '\n' || e.character == '\r'))
                {
                    // A newline without its key event first still sends.
                    pressed = !_swallowNewline;
                    _swallowNewline = false;
                    e.Use();
                }
                else if (e.keyCode != KeyCode.None)
                {
                    // Any other key: an Enter whose newline came in the same event leaves nothing to swallow.
                    _swallowNewline = false;
                }
            }
            // The send button sits over the text area's right edge, so its click is taken before the text area would.
            if (canSend && e.type == EventType.MouseDown && e.button == 0 && send.Contains(e.mousePosition))
            {
                pressed = true;
                e.Use();
            }

            if (!prompt.Enabled)
            {
                // A disabled prompt can't hold the keyboard (and the keyboard lock with it).
                if (focused) GUIUtility.keyboardControl = 0;
                var old = GUI.enabled;
                GUI.enabled = false;
                GUI.TextArea(rect, "", style);
                GUI.enabled = old;
            }
            else
            {
                GUI.SetNextControlName(InputControl);
                _typed = GUI.TextArea(rect, _typed, 4000, style);
                if (_focusPrompt)
                {
                    GUI.FocusControl(InputControl);
                    _focusPrompt = false;
                }
            }
            if ((_typed.Length == 0 || !prompt.Enabled) && prompt.Placeholder.Length > 0 && e.type == EventType.Repaint)
                ClaudiaSkin.Muted.Draw(new Rect(rect.x + style.padding.left, rect.y + style.padding.top - 2, rect.width - style.padding.horizontal, rect.height - style.padding.vertical + 4),
                    new GUIContent(prompt.Placeholder), false, false, false, false);
            ClaudiaSkin.DrawSendButton(send, canSend);
            GUILayout.Label(prompt.Hint, ClaudiaSkin.Muted);
            GUILayout.EndVertical();

            if (pressed && canSend) Send(prompt);
        }

        // From 1 line up to 7 at the current width; beyond that the text area scrolls to the cursor.
        private float PromptHeight(GUIStyle style, float width)
        {
            var one = Mathf.Max(style.CalcHeight(new GUIContent(ClaudiaSkin.Lines(1)), width), SendSize + 8);
            var most = style.CalcHeight(new GUIContent(ClaudiaSkin.Lines(MaxPromptLines)), width);
            // A trailing newline is a line of its own, which the measure would otherwise leave out.
            var text = _typed.EndsWith("\n", StringComparison.Ordinal) ? _typed + "A" : _typed;
            var needed = text.Length == 0 ? one : style.CalcHeight(new GUIContent(text), width);
            return Mathf.Clamp(needed, one, most);
        }

        private void Send(PromptState prompt)
        {
            var text = _typed.Trim();
            if (prompt.For == PromptFor.Goal)
            {
                // Pinned as the Mission Goal; nothing is sent until Check again or Start Mission.
                _goal = text;
                _editingGoal = false;
                _showAllGoal = false;
            }
            else
            {
                _send(MissionWindowRules.Send(prompt.For, text));
                _promptFor = PromptFor.Reply;
            }
            _typed = "";
        }

        // ---- Action row ----

        /// <param name="writing">What the prompt settled on; Change and Ask show pressed while it is theirs.</param>
        private void DrawActions(bool settingUp, PromptFor writing)
        {
            if (settingUp)
            {
                DrawStartRow();
                return;
            }
            // Read before the row: Raise cap clicked in it mustn't add the field to an event that was laid out without it.
            var raising = _raising;
            var any = false;
            foreach (var reply in _view.QuickReplies)
                if (Shown(reply)) any = true;
            if (any)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(ClaudiaSkin.Padding);
                foreach (var reply in _view.QuickReplies)
                {
                    if (!Shown(reply)) continue;
                    var label = MissionWindowRules.Label(reply, _view);
                    bool clicked;
                    if (MissionWindowRules.IsPrimary(reply)) clicked = ClaudiaSkin.PrimaryButton(label, GUILayout.ExpandWidth(true), GUILayout.MinWidth(0));
                    else if (reply == QuickReply.ChangeSomething) clicked = ClaudiaSkin.ToggleButton(writing == PromptFor.Change, label, GUILayout.ExpandWidth(false));
                    else if (reply == QuickReply.AskQuestion) clicked = ClaudiaSkin.ToggleButton(writing == PromptFor.Question, label, GUILayout.ExpandWidth(false));
                    else clicked = ClaudiaSkin.SecondaryButton(label, GUILayout.ExpandWidth(false));
                    if (clicked) QuickReplied(reply);
                }
                GUILayout.Space(ClaudiaSkin.Padding);
                GUILayout.EndHorizontal();
            }
            DrawRaiseCap(raising);
        }

        // End Mission is always in the control bar during a Mission, so the row doesn't repeat it; confirming an Approval is a checkbox in the transcript.
        private bool Shown(QuickReply reply)
        {
            return MissionWindowRules.InActionRow(reply) && !(reply == QuickReply.EndMission && _view.CanEndMission);
        }

        private void DrawStartRow()
        {
            var form = _view.StartForm;
            var check = _view.CraftCheck;
            GUILayout.BeginHorizontal();
            GUILayout.Space(ClaudiaSkin.Padding);
            // Start Mission runs the Craft Check first; it is closed on a Blocker and until every Warning is acknowledged.
            var canStart = _setup.SpendingCap > 0 && _goal.Length > 0 && form.Models.Count > 0 && check.StartAllowed;
            if (canStart)
            {
                // The goal stays until the Mission has actually started: the Craft Check may still hold it back.
                if (ClaudiaSkin.PrimaryButton("Start Mission", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
                    _send(new StartMission(_goal, _setup.SpendingCap, _setup.ModelFor(form), _setup.Settings.Copy(), _setup.Remote));
            }
            else
            {
                ClaudiaSkin.DisabledButton(MissionWindowRules.StartLabel(_view), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0));
            }
            GUI.enabled = _goal.Length > 0 && form.Models.Count > 0 && check.State != CraftCheckState.Running;
            if (ClaudiaSkin.SecondaryButton(check.State == CraftCheckState.None ? "Check craft" : "Check again", GUILayout.ExpandWidth(false)))
                _send(new RunCraftCheck(_goal, _setup.ModelFor(form), _setup.Remote));
            GUI.enabled = true;
            GUILayout.Space(ClaudiaSkin.Padding);
            GUILayout.EndHorizontal();
        }

        // Raise cap: the Mission is Paused at its Spending Cap; the player picks a higher cap and it resumes.
        private void DrawRaiseCap(bool raising)
        {
            if (!_view.QuickReplies.Contains(QuickReply.RaiseCap))
            {
                _raising = false;
                _raiseTo = "";
                return;
            }
            if (!raising) return;
            if (_raiseTo.Length == 0) _raiseTo = Dollars(_view.SpendingCap * 2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(ClaudiaSkin.Padding);
            GUILayout.Label("Raise Spending Cap to $", ClaudiaSkin.Body, GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(RaiseControl);
            _raiseTo = GUILayout.TextField(_raiseTo, 10, ClaudiaSkin.TextField, GUILayout.Width(ClaudiaSkin.Scaled(90)));
            decimal cap;
            GUI.enabled = decimal.TryParse(_raiseTo, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > _view.SpendingCap;
            if (ClaudiaSkin.PrimaryButton("Raise", GUILayout.ExpandWidth(false)))
            {
                _send(new RaiseCap(cap));
                _raising = false;
                _raiseTo = "";
            }
            GUI.enabled = true;
            if (ClaudiaSkin.SecondaryButton("Cancel", GUILayout.ExpandWidth(false))) _raising = false;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // ---- Control bar ----

        private void DrawControlBar(bool settingUp, bool docked)
        {
            DrawEndConfirmation();
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            ClaudiaSkin.Divider();
            GUILayout.BeginHorizontal(_controlBar, GUILayout.Height(ControlBarHeight));
            if (OpenSettings != null && ClaudiaSkin.IconButton(ClaudiaSkin.IconShape.Gear, "", GUILayout.Width(ClaudiaSkin.ButtonHeight))) OpenSettings();
            GUILayout.Space(ClaudiaSkin.Gap);

            if (docked)
            {
                // The Capcom strip above already carries the spend meter.
                if (SetDocked != null && ClaudiaSkin.IconButton(ClaudiaSkin.IconShape.ExternalLink, "Undock", GUILayout.ExpandWidth(false))) SetDocked(false);
                GUILayout.FlexibleSpace();
            }
            else
            {
                if (_view.CanEndMission) DrawSpendMeter();
                else if (settingUp) DrawSummary(MissionWindowRules.SetupSummary(_view, _setup.Remote, _setup.ModelFor(_view.StartForm)));
                else GUILayout.FlexibleSpace();
                if (OfferDock && SetDocked != null && ClaudiaSkin.IconButton(ClaudiaSkin.IconShape.Dock, "Dock", GUILayout.ExpandWidth(false))) SetDocked(true);
            }

            if (_debugTools)
            {
                GUI.enabled = _view.CanCaptureSnapshot;
                if (ClaudiaSkin.SmallButton("Capture Game Snapshot (debug)", GUILayout.ExpandWidth(false))) _send(new CaptureSnapshot());
                GUI.enabled = true;
            }
            if (!_view.CanEndMission) _confirmingEnd = false;
            else if (ClaudiaSkin.DangerButton("End Mission", GUILayout.ExpandWidth(false), GUILayout.Height(ClaudiaSkin.Scaled(32)))) _confirmingEnd = true;
            GUILayout.EndHorizontal();
        }

        // One line of the pending setup, ellipsized to the room it gets.
        private void DrawSummary(string text)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(text), _summary, GUILayout.ExpandWidth(true), GUILayout.MinWidth(0), GUILayout.Height(ControlBarHeight - 12));
            if (Event.current.type != EventType.Repaint) return;
            _summary.Draw(rect, new GUIContent(ClaudiaSkin.Ellipsize(text, _summary, rect.width), text), false, false, false, false);
        }

        // "$1.42 / $10.00" over a bar 84 px wide (times the font scale) and 4 px high: accent, amber from 80% of the Spending Cap.
        private void DrawSpendMeter()
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.Label(MissionWindowRules.SpendMeter(_view), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            ClaudiaSkin.SpendBar(_view, ClaudiaSkin.Scaled(84));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
        }

        // End Mission needs a confirmation, shown just above the control bar.
        private void DrawEndConfirmation()
        {
            if (!_confirmingEnd || !_view.CanEndMission) return;
            GUILayout.BeginVertical(ClaudiaSkin.Raised);
            GUILayout.Label("End this Mission? Its folder and transcript are kept.", ClaudiaSkin.Body);
            GUILayout.BeginHorizontal();
            if (ClaudiaSkin.DangerButton("Yes, end it", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
            {
                _confirmingEnd = false;
                _send(new EndMission());
            }
            if (ClaudiaSkin.SecondaryButton("No", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0))) _confirmingEnd = false;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private static string Dollars(decimal amount)
        {
            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
