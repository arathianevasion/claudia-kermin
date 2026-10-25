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
        private readonly LogDrawer _log = new LogDrawer();
        private GUIStyle _wrap;
        private GUIStyle _bold;
        private GUIStyle _goalStyle;

        // Start form: the Mission Goal here; type, model, Spending Cap and Mission Settings in the setup (the Settings window edits them).
        private readonly MissionSetup _setup;
        private string _goal = "";
        private Vector2 _goalScroll;
        // The Craft Check findings scroll; a new result (a different set of findings) jumps back to the top.
        private Vector2 _checkScroll;
        private string _seenCheckResult = "";
        // In Flight the Craft Check starts collapsed to one line so the Mission log gets the room; reset when Flight loads.
        private bool _checkCollapsed = true;
        // End Mission asked "End this Mission?" and awaits Yes / No.
        private bool _confirmingEnd;
        // Raise cap asked for a new Spending Cap and awaits the amount.
        private bool _raising;
        private string _raiseTo = "";

        // Reply box.
        private string _typed = "";
        private TypingFor _typingFor = TypingFor.Reply;

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
            _frame.Remember("mission");
            // The VAB/SPH Mission board has two panes, so it opens wider.
            _frame.EditorDefault = new Rect(160, 80, 980, 640);
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
                case QuickReply.WarpToDueTime: _send(new WarpToDueTime()); break;
                case QuickReply.GoToVessel: _send(new GoToVessel()); break;
                case QuickReply.ManualControl: _send(new TakeManualControl()); break;
                case QuickReply.HandBack:
                    // Whatever is in the reply box goes with it as the optional message about what the player did.
                    _send(new HandBack(_typed));
                    _typed = "";
                    _typingFor = TypingFor.Reply;
                    break;
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

        /// <summary>Flight is loading: the Craft Check starts collapsed again.</summary>
        public void FlightLoading()
        {
            _checkCollapsed = true;
        }

        /// <summary>The label with the view's names in it: Go to <i>vessel</i>.</summary>
        public static string Label(QuickReply reply, MissionView view)
        {
            if (reply == QuickReply.GoToVessel && view.DueTime.VesselName.Length > 0) return "Go to " + view.DueTime.VesselName;
            return Label(reply);
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
                case QuickReply.WarpToDueTime: return "Warp to Due Time";
                case QuickReply.GoToVessel: return "Go to vessel";
                case QuickReply.ManualControl: return "Manual Control";
                case QuickReply.HandBack: return "Hand back to Claudia";
                default: return "Retry";
            }
        }

        private void Draw(int id)
        {
            if (_wrap == null)
            {
                _wrap = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };
                _bold = new GUIStyle(_wrap) { fontStyle = FontStyle.Bold };
                _goalStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true, richText = false };
            }
            if (HighLogic.LoadedSceneIsEditor) DrawBoard();
            else DrawFlight();
            _frame.DrawGrip();
            WindowFrame.DrawTooltip();
            GUI.DragWindow();
        }

        /// <summary>Flight and the Space Center: one column, the Mission log over the start form and the Craft Check.</summary>
        private void DrawFlight()
        {
            if (View.StartForm.CanStart) DrawStartForm();
            // The start form's goal is spent once a Mission is under way.
            if (View.CanEndMission) _goal = "";
            if (View.Stage != MissionStage.None) DrawMission();
            if (View.StartForm.CanStart || View.CraftCheck.State != CraftCheckState.None) DrawCraftCheck();
            DrawDebugTools();
        }

        /// <summary>
        /// The VAB/SPH Mission board: on the left the Mission Goal, the craft, its delta-v margin and the cost,
        /// with the Craft Check pinned below; on the right the conversation with Claudia.
        /// </summary>
        private void DrawBoard()
        {
            if (View.CanEndMission) _goal = "";
            var left = Mathf.Clamp(_frame.Rect.width * 0.42f, 300f, 460f);
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(left));
            if (View.StartForm.CanStart) DrawStartForm();
            else if (View.Stage != MissionStage.None) DrawMissionHeader();
            DrawCraftFacts();
            if (View.StartForm.CanStart || View.CraftCheck.State != CraftCheckState.None) DrawCraftCheck();
            DrawDebugTools();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true));
            GUILayout.Label("Conversation", _bold);
            if (View.Stage == MissionStage.None)
                GUILayout.Label("Write the Mission Goal on the left and press Start Mission. Claudia checks the craft first, then answers here.", _wrap);
            else DrawConversation();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        /// <summary>The craft, its delta-v against the margin the Craft Check wants, and the cost.</summary>
        private void DrawCraftFacts()
        {
            var check = View.CraftCheck;
            GUILayout.Box("", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            GUILayout.Label("Craft: " + (check.CraftName.Length > 0 ? check.CraftName : "none on the floor"), _bold);
            GUILayout.Label(check.CraftDeltaV.HasValue
                ? "Delta-v: " + check.CraftDeltaV.Value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s; margin wanted over the Delta-v Map: "
                    + check.MarginPercent.ToString(CultureInfo.InvariantCulture) + "%. " + MarginVerdict(check)
                : "Delta-v: unknown (turn on the stock delta-v simulator in the editor).", _wrap);
            if (View.Stage != MissionStage.None)
                GUILayout.Label("Cost: " + SpendText(View) + "  Model: " + View.Model, _wrap);
            else if (View.StartForm.CanStart && View.StartForm.Models.Count > 0)
            {
                decimal estimate;
                var model = _setup.ModelFor(View.StartForm);
                if (View.StartForm.Estimates.TryGetValue(model, out estimate))
                    GUILayout.Label("Cost: a whole Mission is about $" + estimate.ToString("0.00", CultureInfo.InvariantCulture) + " with " + model + ".", _wrap);
            }
        }

        private static string MarginVerdict(CraftCheckView check)
        {
            if (check.State != CraftCheckState.Done || check.Stale) return "Check the craft to judge it against the Mission Goal.";
            return check.DeltaVShort ? "Short of the delta-v this Mission Goal needs." : "Enough for this Mission Goal.";
        }

        private void DrawDebugTools()
        {
            if (!_debugTools) return;
            GUI.enabled = View.CanCaptureSnapshot;
            if (GUILayout.Button("Capture Game Snapshot (debug)")) _send(new CaptureSnapshot());
            GUI.enabled = true;
        }

        private void DrawStartForm()
        {
            var form = View.StartForm;
            _setup.SeedFrom(form);
            GUILayout.Label("Start a Mission", _bold);
            GUILayout.Label("Mission Goal:");
            // About 9 lines by default; taller as the player resizes the window.
            var goalHeight = Mathf.Max(9f * _goalStyle.lineHeight + _goalStyle.padding.vertical, _frame.Rect.height * 0.4f);
            _goalScroll = GUILayout.BeginScrollView(_goalScroll, GUILayout.Height(goalHeight));
            GUI.SetNextControlName(InputControl);
            _goal = GUILayout.TextArea(_goal, 4000, _goalStyle);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Clear", GUILayout.ExpandWidth(false))) _goal = "";

            // Type, model, Spending Cap and Mission Settings are chosen in the Settings window.
            GUILayout.Label((_setup.Remote ? "Remote" : "Crewed") + "; model " + _setup.ModelFor(form) + "; Spending Cap $"
                + _setup.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture) + "; " + _setup.Settings.Summary(), _wrap);
            DrawSettingsButton();

            // Start Mission runs the Craft Check first; it is disabled on a Blocker and until every Warning is acknowledged.
            GUI.enabled = _setup.SpendingCap > 0 && _goal.Trim().Length > 0 && form.Models.Count > 0 && View.CraftCheck.StartAllowed;
            if (GUILayout.Button("Start Mission"))
            {
                // The goal stays until the Mission has actually started: the Craft Check may still hold it back.
                _send(new StartMission(_goal, _setup.SpendingCap, _setup.ModelFor(form), _setup.Settings.Copy(), _setup.Remote));
            }
            GUI.enabled = true;
            if (form.Refusal.Length > 0) GUILayout.Label(form.Refusal, _wrap);
        }

        private void DrawSettingsButton()
        {
            if (OpenSettings != null && GUILayout.Button("Settings", GUILayout.ExpandWidth(false))) OpenSettings();
        }

        /// <summary>The Mission's settings on the board; while a plan awaits Approval they can be changed in the Settings window, which asks Claudia for a revised Mission Plan.</summary>
        private void DrawSettings()
        {
            GUILayout.Label(View.Settings.Summary(), _wrap);
            DrawSettingsButton();
        }

        /// <summary>The Craft Check, pinned at the bottom of the Mission board.</summary>
        private void DrawCraftCheck()
        {
            var check = View.CraftCheck;
            var form = View.StartForm;
            GUILayout.Box("", GUILayout.Height(2), GUILayout.ExpandWidth(true));

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
                if (!needsPlayer && GUILayout.Button(collapsed ? "Expand" : "Collapse", GUILayout.ExpandWidth(false)))
                    _checkCollapsed = !_checkCollapsed;
                GUILayout.EndHorizontal();
                if (collapsed) return;
            }
            else
            {
                GUILayout.Label("Craft Check", _bold);
            }

            if (form.CanStart)
            {
                GUI.enabled = _goal.Trim().Length > 0 && form.Models.Count > 0 && check.State != CraftCheckState.Running;
                if (GUILayout.Button("Check craft", GUILayout.ExpandWidth(false)))
                    _send(new RunCraftCheck(_goal, _setup.ModelFor(form), _setup.Remote));
                GUI.enabled = true;
            }
            // Chosen in the Settings window.
            foreach (var processor in check.Processors)
                if (processor.Chosen) GUILayout.Label("Claudia's kOS processor: " + processor.Title, _wrap);

            // Any result after a pause (Running, Error, ...) is new, even if it matches the last one.
            if (check.State != CraftCheckState.Done) _seenCheckResult = "";
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

        private void DrawCraftCheckResult(CraftCheckView check)
        {
            var verdict = VerdictText(check);
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
            DrawMissionHeader();
            DrawConversation();
        }

        private void DrawMissionHeader()
        {
            GUILayout.Label("Mission Goal: " + View.Goal, _wrap);
            GUILayout.Label("Model: " + View.Model + "   Spent: " + SpendText(View) + "   " + StageText(View), _wrap);
            DrawSettings();
        }

        /// <summary>The log, the quick replies, the reply box and End Mission.</summary>
        private void DrawConversation()
        {
            _log.Draw(View, GUILayout.ExpandHeight(true));

            if (View.ConfirmPrompt != null) GUILayout.Label(View.ConfirmPrompt, _bold);
            if (View.QuickReplies.Count > 0)
            {
                GUILayout.BeginHorizontal();
                foreach (var reply in View.QuickReplies)
                    if (GUILayout.Button(Label(reply, View))) QuickReplied(reply);
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
            var manual = View.Stage == MissionStage.ManualControl;
            GUILayout.Label(manual ? "Ask Claudia, or write what you did and Hand back:"
                : _typingFor == TypingFor.Change ? "What should change?"
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
