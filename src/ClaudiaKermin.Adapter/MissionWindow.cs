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
    /// The Mission window: one compact column in every scene. Top to bottom: the title bar with the status chip, the
    /// pinned Mission Goal, the transcript (the Craft Check card, the Mission log, the approval checkbox, the Busy line), the prompt (which grows
    /// from 1 to 7 lines and takes that height from the transcript), the action row of quick replies, and the control
    /// bar (Settings, the setup summary or the spend meter, End Mission). Before a Mission the prompt writes the Mission
    /// Goal; type, model, Spending Cap and Mission Settings come from the <see cref="MissionSetup"/>, which the Settings
    /// window edits. It only draws <see cref="MissionView"/> and turns clicks into <see cref="MissionInput"/>s.
    /// </summary>
    public class MissionWindow
    {
        private const string InputControl = "ClaudiaMissionInput";
        private const string RaiseControl = "ClaudiaMissionRaise";
        private const string LockId = "ClaudiaKerminMission";
        private const int MaxPromptLines = 7;
        private const float SendSize = 32;
        private const float ControlBarHeight = 44;
        // The resize grip sits in the bottom-right corner, over the control bar's right edge.
        private const int GripRoom = 24;

        private readonly Action<MissionInput> _send;
        private readonly bool _debugTools;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B03, "Claudia: Mission", LockId, new Rect(260, 80, 420, 760), new Vector2(360, 420), true);
        private readonly LogDrawer _log;
        private GUIStyle _controlBar, _summary;
        private GUIStyle _builtFrom;

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

        public bool Visible;
        public bool UiHidden;

        /// <summary>Set before each OnGUI.</summary>
        public MissionView View;

        /// <summary>Opens the Settings window; set by the owner.</summary>
        public Action OpenSettings;

        public MissionWindow(Action<MissionInput> send, MissionSetup setup, bool debugTools)
        {
            _send = send;
            _setup = setup;
            _debugTools = debugTools;
            _log = new LogDrawer(send);
            _frame.Remember("mission");
        }

        public void OnGUI()
        {
            if (!Visible || UiHidden || View == null)
            {
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            _frame.Style = ClaudiaSkin.BareWindow;
            _frame.Show(Draw);

            // Keys typed into the window must not stage, throttle or rotate the craft.
            var focused = GUI.GetNameOfFocusedControl();
            _frame.SetTyping(focused == InputControl || focused == RaiseControl);
        }

        /// <summary>Releases the input locks; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        /// <summary>A quick reply from here or the Capcom strip. Change and Ask pick what the prompt sends; pressed again, they let go.</summary>
        public void QuickReplied(QuickReply reply)
        {
            switch (reply)
            {
                case QuickReply.Approve: _send(new Approve()); break;
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
            Visible = true;
            _focusPrompt = _promptFor == mode;
        }

        /// <summary>Flight is loading: the Craft Check starts collapsed again.</summary>
        public void FlightLoading()
        {
            _log.FlightLoading();
        }

        /// <summary>The label with the view's names in it: Go to <i>vessel</i>.</summary>
        public static string Label(QuickReply reply, MissionView view)
        {
            return MissionWindowRules.Label(reply, view);
        }

        private void Draw(int id)
        {
            Styles();
            // The start form's goal is spent once a Mission is under way.
            if (View.CanEndMission)
            {
                _goal = "";
                _editingGoal = false;
            }
            var settingUp = View.StartForm.CanStart;
            var prompt = MissionWindowRules.Prompt(View, _promptFor, _goal.Length > 0 && !_editingGoal);

            DrawTitleBar();
            DrawGoal(settingUp);
            SetCraftCheckContext(settingUp);
            GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(ClaudiaSkin.Padding);
            _log.Draw(View, _frame.Rect.width - 2 * ClaudiaSkin.Padding - 20, GUILayout.ExpandHeight(true));
            GUILayout.Space(ClaudiaSkin.Padding / 2f);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            DrawPrompt(prompt);
            DrawActions(settingUp, prompt.For);
            DrawControlBar(settingUp);

            _frame.DrawGrip();
            WindowFrame.DrawTooltip();
            GUI.DragWindow();
        }

        // What the Craft Check card in the transcript needs from the setup: the pinned goal, the cost estimate and Check again.
        private void SetCraftCheckContext(bool settingUp)
        {
            var form = View.StartForm;
            var model = _setup.ModelFor(form);
            decimal estimate;
            _log.GoalSet = settingUp && _goal.Length > 0;
            _log.Estimate = settingUp && form.Estimates.TryGetValue(model, out estimate) ? estimate : (decimal?)null;
            _log.CheckAgain = () => _goal.Length > 0 && form.Models.Count > 0 && View.CraftCheck.State != CraftCheckState.Running
                ? new RunCraftCheck(_goal, model, _setup.Remote) : null;
        }

        private void Styles()
        {
            if (_controlBar != null && ReferenceEquals(_builtFrom, ClaudiaSkin.Bar)) return;
            _builtFrom = ClaudiaSkin.Bar;
            _controlBar = new GUIStyle(ClaudiaSkin.Bar) { padding = new RectOffset(ClaudiaSkin.Padding, GripRoom, 6, 6) };
            _summary = new GUIStyle(ClaudiaSkin.Secondary) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
        }

        // ---- Title bar ----

        // A Claudia-blue dot, the title in accent, the status chip on the right, then close.
        private void DrawTitleBar()
        {
            var bar = ClaudiaSkin.TitleBarHeight;
            var width = _frame.Rect.width;
            ClaudiaSkin.DrawIcon(new Rect(ClaudiaSkin.Padding, (bar - 8) / 2f, 8, 8), ClaudiaSkin.IconShape.Dot, ClaudiaSkin.Claudia);
            var titleStyle = ClaudiaSkin.TitleAccent;
            var title = new GUIContent("Claudia: Mission");
            var titleWidth = titleStyle.CalcSize(title).x;
            var titleX = ClaudiaSkin.Padding + 8 + ClaudiaSkin.Gap;
            GUI.Label(new Rect(titleX, 0, titleWidth, bar), title, titleStyle);

            var close = new Rect(width - ClaudiaSkin.Padding - ClaudiaSkin.MinClickable, (bar - ClaudiaSkin.MinClickable) / 2f, ClaudiaSkin.MinClickable, ClaudiaSkin.MinClickable);
            if (GUI.Button(close, new GUIContent("", "Close"), GUIStyle.none)) Visible = false;
            ClaudiaSkin.DrawIcon(new Rect(close.center.x - 6, close.center.y - 6, 12, 12), ClaudiaSkin.IconShape.Close,
                close.Contains(Event.current.mousePosition) ? ClaudiaSkin.Text : ClaudiaSkin.TextSecondary);

            var chip = MissionWindowRules.Status(View);
            if (chip == null) return;
            var style = ClaudiaSkin.ChipStyle(ClaudiaSkin.KindOf(chip.Tone));
            var room = close.x - ClaudiaSkin.Gap - (titleX + titleWidth + ClaudiaSkin.Gap);
            if (room < 40) return;
            var text = ClaudiaSkin.Ellipsize(chip.Text, style, room - style.padding.horizontal);
            var size = style.CalcSize(new GUIContent(text));
            var chipWidth = Mathf.Min(size.x, room);
            GUI.Label(new Rect(close.x - ClaudiaSkin.Gap - chipWidth, (bar - size.y) / 2f, chipWidth, size.y), text, style);
        }

        // ---- Pinned Mission Goal ----

        private void DrawGoal(bool settingUp)
        {
            var goal = settingUp ? _goal : View.Goal;
            var textWidth = _frame.Rect.width - 2 * ClaudiaSkin.Padding;
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
            var step = HighLogic.LoadedSceneIsFlight && !settingUp ? MissionWindowRules.Step(View) : "";
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
            var width = _frame.Rect.width - 2 * ClaudiaSkin.Padding;
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
            foreach (var reply in View.QuickReplies)
                if (Shown(reply)) any = true;
            if (any)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(ClaudiaSkin.Padding);
                foreach (var reply in View.QuickReplies)
                {
                    if (!Shown(reply)) continue;
                    var label = Label(reply, View);
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
            return MissionWindowRules.InActionRow(reply) && !(reply == QuickReply.EndMission && View.CanEndMission);
        }

        private void DrawStartRow()
        {
            var form = View.StartForm;
            var check = View.CraftCheck;
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
                ClaudiaSkin.DisabledButton(MissionWindowRules.StartLabel(View), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0));
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
            if (!View.QuickReplies.Contains(QuickReply.RaiseCap))
            {
                _raising = false;
                _raiseTo = "";
                return;
            }
            if (!raising) return;
            if (_raiseTo.Length == 0) _raiseTo = Dollars(View.SpendingCap * 2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(ClaudiaSkin.Padding);
            GUILayout.Label("Raise Spending Cap to $", ClaudiaSkin.Body, GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(RaiseControl);
            _raiseTo = GUILayout.TextField(_raiseTo, 10, ClaudiaSkin.TextField, GUILayout.Width(90));
            decimal cap;
            GUI.enabled = decimal.TryParse(_raiseTo, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > View.SpendingCap;
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

        private void DrawControlBar(bool settingUp)
        {
            DrawEndConfirmation();
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            ClaudiaSkin.Divider();
            GUILayout.BeginHorizontal(_controlBar, GUILayout.Height(ControlBarHeight));
            if (OpenSettings != null && ClaudiaSkin.IconButton(ClaudiaSkin.IconShape.Gear, "", GUILayout.Width(36))) OpenSettings();
            GUILayout.Space(ClaudiaSkin.Gap);

            if (View.CanEndMission) DrawSpendMeter();
            else if (settingUp) DrawSummary(MissionWindowRules.SetupSummary(View, _setup.Remote, _setup.ModelFor(View.StartForm)));
            else GUILayout.FlexibleSpace();

            if (_debugTools)
            {
                GUI.enabled = View.CanCaptureSnapshot;
                if (ClaudiaSkin.SmallButton("Capture Game Snapshot (debug)", GUILayout.ExpandWidth(false))) _send(new CaptureSnapshot());
                GUI.enabled = true;
            }
            if (!View.CanEndMission) _confirmingEnd = false;
            else if (ClaudiaSkin.DangerButton("End Mission", GUILayout.ExpandWidth(false), GUILayout.Height(32))) _confirmingEnd = true;
            GUILayout.EndHorizontal();
        }

        // One line of the pending setup, ellipsized to the room it gets.
        private void DrawSummary(string text)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(text), _summary, GUILayout.ExpandWidth(true), GUILayout.MinWidth(0), GUILayout.Height(ControlBarHeight - 12));
            if (Event.current.type != EventType.Repaint) return;
            _summary.Draw(rect, new GUIContent(ClaudiaSkin.Ellipsize(text, _summary, rect.width), text), false, false, false, false);
        }

        // "$1.42 / $10.00" over an 84×4 bar: accent, amber from 80% of the Spending Cap.
        private void DrawSpendMeter()
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.Label(MissionWindowRules.SpendMeter(View), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            var bar = GUILayoutUtility.GetRect(84, 4, GUILayout.Width(84), GUILayout.Height(4));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Border));
                var fill = new Rect(bar.x, bar.y, bar.width * MissionWindowRules.SpendFraction(View), bar.height);
                GUI.DrawTexture(fill, ClaudiaSkin.Solid(View.CapWarning ? ClaudiaSkin.Attention : ClaudiaSkin.Accent));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
        }

        // End Mission needs a confirmation, shown just above the control bar.
        private void DrawEndConfirmation()
        {
            if (!_confirmingEnd || !View.CanEndMission) return;
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

        /// <summary>The cost so far against the Spending Cap, e.g. "$1.08 of $10.00", with the 80% warning.</summary>
        public static string SpendText(MissionView view)
        {
            var text = "$" + Dollars(view.Spent + view.HeldCharges) + " of $" + Dollars(view.SpendingCap);
            return view.CapWarning ? text + " (80% of the cap or more)" : text;
        }

        public static string StageText(MissionView view)
        {
            switch (view.Stage)
            {
                case MissionStage.WaitingForFlight: return "Waiting for Flight";
                case MissionStage.Planning: return "Planning";
                case MissionStage.AwaitingPlanApproval: return "Mission Plan awaiting Approval";
                case MissionStage.Sequencing: return "Planning the next sequence";
                case MissionStage.AwaitingSequenceApproval:
                    if (!view.DueTime.HasDueTime) return "Sequence Plan awaiting Approval";
                    return "Sequence Plan awaiting Approval; Due Time " + view.DueTime.Text
                        + (view.DueTime.Reached ? " (reached)" : " (in " + MissionClock.Span(view.DueTime.SecondsLeft) + ")");
                case MissionStage.Running: return "Running";
                case MissionStage.Debriefing: return "Debriefing";
                case MissionStage.Succeeded: return "Mission Goal met";
                case MissionStage.Ended: return "Mission ended (Failed Debrief)";
                case MissionStage.Lost: return "Mission Lost (Claudia was killed aboard)";
                case MissionStage.Paused: return "Paused: " + view.PauseReason;
                case MissionStage.UnsafeScript: return "Couldn't produce a safe script";
                case MissionStage.ManualControl: return "Manual Control: you have the controls";
                default: return "";
            }
        }
    }
}
