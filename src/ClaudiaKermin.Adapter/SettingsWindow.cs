// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using ClaudiaKermin.Adapter.Roster;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Settings window: the Mission section on top and, when no Mission is active, the collapsible Claudia Kermin
    /// section (the API key, Prepare to uninstall). Drawn in one of three contexts: during a Mission (raise the Spending
    /// Cap, edit the Mission Settings while a plan awaits Approval), setting up a Mission (the <see cref="MissionSetup"/>
    /// and the kOS processor), and the Space Center (the Mission defaults, saved to <c>config.cfg</c>). The Mission
    /// section is a draft until Apply; Cancel throws it away. The key buttons act right away.
    /// </summary>
    public class SettingsWindow
    {
        private const string KeyControl = "ClaudiaSettingsKey";
        private const string CapControl = "ClaudiaSettingsCap";
        private const string PortraitUrl = "ClaudiaKermin/Icons/ClaudiaPortrait";

        private static readonly string[] TimeChoices = { "Fastest", "Balanced", "Most efficient" };
        private static readonly string[] TypeChoices = { "Crewed", "Remote" };

        private enum Context
        {
            /// <summary>A Mission exists that isn't over.</summary>
            DuringMission,
            /// <summary>No Mission, and one can be started here (the VAB/SPH, or a craft in Flight).</summary>
            SettingUp,
            /// <summary>No Mission in context (the Space Center): the Mission defaults.</summary>
            Defaults,
        }

        private readonly KeySettings _keys;
        private readonly MissionDriver _missions;
        private readonly PlayerConfig _config;
        private readonly string _configDirectory;
        private readonly ChatWindow _chat;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B01, "Settings", "ClaudiaKerminSettings", new Rect(200, 90, 380, 640), new Vector2(380, 360), true);

        private MissionView _view;
        private Context _context;

        // The Mission section's draft, taken when the window opens or its context changes.
        private bool _drafted;
        private Context _draftFor;
        private bool _remote;
        private string _model = "";
        private string _cap = "";
        // During a Mission: the cap the field was filled from, so a raise made elsewhere (the Mission window) refreshes an untouched field.
        private decimal _capDraftedFrom;
        private MissionSettingsValues _settings = new MissionSettingsValues();
        // Picked here and not yet applied; null in _processor is "nearest the root".
        private bool _processorPicked;
        private uint? _processor;
        private string _applyError = "";

        // The Claudia Kermin section, remembered per context for the session.
        private bool _expandedSettingUp;
        private bool _expandedDefaults = true;
        private string _typed = "";
        private string _uninstallStatus = "";

        private Vector2 _scroll;
        private GUIStyle _label, _dangerLabel, _note, _row, _rowOn, _tile, _header, _headerOpen, _uninstallBox, _radioOn, _radioOff;
        private Texture2D _portrait;

        public bool Visible;

        /// <summary>Set while the stock UI is hidden (F2).</summary>
        public bool UiHidden;

        /// <param name="chat">The basic chat window, opened from here when <c>debug_tools</c> is on.</param>
        public SettingsWindow(KeySettings keys, MissionDriver missions, PlayerConfig config, string configDirectory, ChatWindow chat)
        {
            _keys = keys;
            _missions = missions;
            _config = config;
            _configDirectory = configDirectory;
            _chat = chat;
            _frame.Remember("settings");
        }

        public void OnGUI()
        {
            if (!Visible || UiHidden)
            {
                // Closed from outside (the toolbar button, a scene change): the draft goes with it.
                if (!Visible) Discard();
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            _view = _missions.View;
            _context = ContextFor(_view);
            if (!_drafted || _context != _draftFor) Draft();
            _frame.Style = ClaudiaSkin.Window;
            _frame.Show(Draw);
            var focused = GUI.GetNameOfFocusedControl();
            _frame.SetTyping(focused == KeyControl || focused == CapControl);
        }

        /// <summary>Releases the input locks; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private static Context ContextFor(MissionView view)
        {
            if (view.CanEndMission) return Context.DuringMission;
            var scene = HighLogic.LoadedScene;
            if (view.StartForm.CanStart && (scene == GameScenes.EDITOR || scene == GameScenes.FLIGHT)) return Context.SettingUp;
            return Context.Defaults;
        }

        private void Draft()
        {
            _drafted = true;
            _draftFor = _context;
            _processorPicked = false;
            _processor = null;
            _applyError = "";
            switch (_context)
            {
                case Context.DuringMission:
                    _capDraftedFrom = _view.SpendingCap;
                    _cap = Dollars(_view.SpendingCap);
                    _settings = _view.Settings.Copy();
                    break;
                case Context.SettingUp:
                    var setup = _missions.Setup;
                    setup.SeedFrom(_view.StartForm);
                    _remote = setup.Remote;
                    _model = setup.ModelFor(_view.StartForm);
                    _cap = Dollars(setup.SpendingCap);
                    _settings = setup.Settings.Copy();
                    break;
                default:
                    _remote = _config.DefaultRemote;
                    _model = _config.DefaultModel;
                    _cap = Dollars(_config.DefaultSpendingCap);
                    _settings = MissionSettingsValues.Defaults(_config);
                    break;
            }
        }

        private void Discard()
        {
            _drafted = false;
            _typed = "";
        }

        private void Close()
        {
            Visible = false;
            Discard();
        }

        private void Draw(int id)
        {
            BuildStyles();
            ClaudiaSkin.DrawTitleBar(_frame.Rect, "Settings", ClaudiaSkin.IconShape.Gear);

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.ExpandHeight(true));
            DrawMissionSection();
            GUILayout.Space(ClaudiaSkin.Gap * 2);
            if (_context == Context.DuringMission) DrawDashedNote("The API key and Prepare to uninstall are under Claudia Kermin, shown here when no Mission is active.");
            else DrawClaudiaKermin();
            GUILayout.EndScrollView();

            DrawFooter();
            _frame.DrawGrip();
            GUI.DragWindow();
        }

        // ---- Mission ----

        private void DrawMissionSection()
        {
            var during = _context == Context.DuringMission;
            var defaults = _context == Context.Defaults;
            ClaudiaSkin.SectionLabel(defaults ? "Mission defaults" : "Mission");
            GUILayout.Label(during ? "This Mission. Changes while a plan awaits Approval ask Claudia for a revised plan."
                : defaults ? "Every new Mission starts with these. You can change them per Mission before launch."
                : "For the Mission you're setting up", ClaudiaSkin.Secondary);
            GUILayout.Space(ClaudiaSkin.Gap);

            if (during)
            {
                GUILayout.BeginHorizontal();
                DrawLockedTile("Mission type", _view.Remote ? "Remote" : "Crewed", false);
                GUILayout.Space(ClaudiaSkin.Gap);
                DrawLockedTile("Model", _view.Model, true);
                GUILayout.EndHorizontal();
            }
            else
            {
                DrawMissionType(!defaults);
                DrawModels(!defaults);
            }
            GUILayout.Space(ClaudiaSkin.Gap);
            DrawSpendingCap(during);
            GUILayout.Space(ClaudiaSkin.Gap);
            DrawMissionSettings(during);

            if (during)
            {
                var chosen = ChosenProcessorTitle();
                if (chosen.Length > 0)
                {
                    GUILayout.Space(ClaudiaSkin.Gap);
                    DrawLockedTile("kOS processor", chosen, false);
                }
            }
            else if (_context == Context.SettingUp)
            {
                DrawProcessorChoice();
            }
        }

        private void DrawLockedTile(string label, string value, bool mono)
        {
            GUILayout.BeginVertical(_tile, GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, ClaudiaSkin.Secondary);
            GUILayout.FlexibleSpace();
            ClaudiaSkin.IconLabel(ClaudiaSkin.IconShape.Lock, ClaudiaSkin.TextMuted, 12);
            GUILayout.EndHorizontal();
            GUILayout.Label(value, mono ? ClaudiaSkin.Mono : ClaudiaSkin.Body);
            GUILayout.Label("Locked for this Mission", ClaudiaSkin.Muted);
            GUILayout.EndVertical();
        }

        /// <summary>A field label, with "Locked once started" and a lock on the right when the choice is locked at Start Mission.</summary>
        private void DrawFieldLabel(string label, bool lockedOnceStarted)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label);
            GUILayout.FlexibleSpace();
            if (lockedOnceStarted)
            {
                GUILayout.Label("Locked once started", ClaudiaSkin.Muted, GUILayout.ExpandWidth(false));
                ClaudiaSkin.IconLabel(ClaudiaSkin.IconShape.Lock, ClaudiaSkin.TextMuted, 12);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawMissionType(bool lockedOnceStarted)
        {
            DrawFieldLabel("Mission type", lockedOnceStarted);
            _remote = ClaudiaSkin.Segmented(_remote ? 1 : 0, TypeChoices) == 1;
            GUILayout.Label(_remote ? "Claudia flies from Mission Control; needs a link." : "Claudia flies aboard as crew.", ClaudiaSkin.Secondary);
            GUILayout.Space(ClaudiaSkin.Gap);
        }

        private void DrawModels(bool lockedOnceStarted)
        {
            var form = _view.StartForm;
            DrawFieldLabel("Model", lockedOnceStarted);
            decimal defaultEstimate;
            var hasDefaultEstimate = form.Estimates.TryGetValue(form.DefaultModel, out defaultEstimate);
            foreach (var model in form.Models)
            {
                decimal estimate;
                var hasEstimate = form.Estimates.TryGetValue(model, out estimate);
                var note = model == form.DefaultModel ? "Default"
                    : hasEstimate && hasDefaultEstimate && estimate > defaultEstimate ? "Costs more per Mission"
                    : hasEstimate && hasDefaultEstimate && estimate < defaultEstimate ? "Costs less per Mission" : "";
                if (DrawRadioRow(model, ClaudiaSkin.Mono, note, hasEstimate ? "~$" + Dollars(estimate) : "", model == _model)) _model = model;
            }
        }

        private void DrawSpendingCap(bool during)
        {
            if (during && _view.SpendingCap != _capDraftedFrom)
            {
                if (_cap == Dollars(_capDraftedFrom)) _cap = Dollars(_view.SpendingCap);
                _capDraftedFrom = _view.SpendingCap;
            }
            GUILayout.Label("Spending Cap", _label);
            if (during) GUILayout.Label("$" + Dollars(_view.Spent + _view.HeldCharges) + " spent so far", ClaudiaSkin.Secondary);
            // Judged before the field: typing changes _cap mid-event, and the label below mustn't appear or vanish within one frame's layout.
            var error = CapError();
            GUILayout.BeginHorizontal();
            GUILayout.Label("$", ClaudiaSkin.Body, GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(CapControl);
            _cap = GUILayout.TextField(_cap, 10, ClaudiaSkin.TextField, GUILayout.Width(120));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (error.Length > 0) GUILayout.Label(error, ClaudiaSkin.DangerText);
            else if (!during) GUILayout.Label("Pauses the Mission when reached", ClaudiaSkin.Secondary);
        }

        /// <summary>Why the typed Spending Cap can't be applied; "" when it can.</summary>
        private string CapError()
        {
            decimal cap;
            if (!TryCap(out cap)) return "The Spending Cap must be a positive number of dollars.";
            if (_context == Context.DuringMission && cap < _view.SpendingCap) return "The Spending Cap can only be raised during a Mission.";
            return "";
        }

        private bool TryCap(out decimal cap)
        {
            return decimal.TryParse(_cap, NumberStyles.Number, CultureInfo.InvariantCulture, out cap) && cap > 0;
        }

        private void DrawMissionSettings(bool during)
        {
            var editable = !during || _view.CanEditSettings;
            // While they can't be edited, the Mission's own settings are shown, so a later edit starts from them.
            if (!editable) _settings = _view.Settings.Copy();
            GUI.enabled = editable;
            _settings.DaylightLanding = ClaudiaSkin.Switch(_settings.DaylightLanding, "Daylight landing");
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            GUILayout.Label("Time versus delta-v", _label);
            _settings.TimeVersusDeltaV = (TimeVersusDeltaV)ClaudiaSkin.Segmented((int)_settings.TimeVersusDeltaV, TimeChoices);
            GUILayout.Label(TimeCaption(_settings.TimeVersusDeltaV), ClaudiaSkin.Secondary);
            GUI.enabled = true;
            if (!editable) GUILayout.Label("You can change these while a plan awaits Approval.", ClaudiaSkin.Muted);
        }

        private static string TimeCaption(TimeVersusDeltaV time)
        {
            switch (time)
            {
                case TimeVersusDeltaV.Fastest: return "Burns as soon as possible; spends more delta-v.";
                case TimeVersusDeltaV.MostEfficient: return "Waits as long as it takes for the cheapest burn.";
                default: return "Waits up to 6 h for a cheaper window.";
            }
        }

        private void DrawProcessorChoice()
        {
            var processors = _view.CraftCheck.Processors;
            if (processors.Count == 0) return;
            GUILayout.Space(ClaudiaSkin.Gap);
            GUILayout.Label("Claudia's kOS processor", _label);
            if (DrawRadioRow("Nearest the root (default)", ClaudiaSkin.Body, "", "", _processorPicked && _processor == null))
            {
                _processorPicked = true;
                _processor = null;
            }
            foreach (var processor in processors)
            {
                var selected = _processorPicked ? _processor == processor.PartId : processor.Chosen;
                if (DrawRadioRow(processor.Title, ClaudiaSkin.Body, processor.BootFile.Length > 0 ? "[boot file: " + processor.BootFile + "]" : "", "", selected))
                {
                    _processorPicked = true;
                    _processor = processor.PartId;
                }
            }
        }

        private string ChosenProcessorTitle()
        {
            foreach (var processor in _view.CraftCheck.Processors)
                if (processor.Chosen) return processor.Title;
            return "";
        }

        /// <summary>One radio row: the dot, a title with an optional note under it, and text on the right. True when clicked.</summary>
        private bool DrawRadioRow(string title, GUIStyle titleStyle, string note, string right, bool selected)
        {
            GUILayout.BeginHorizontal(selected ? _rowOn : _row, GUILayout.MinHeight(ClaudiaSkin.ButtonHeight + 4));
            GUILayout.BeginVertical(GUILayout.Width(16));
            GUILayout.FlexibleSpace();
            var dot = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            if (Event.current.type == EventType.Repaint) (selected ? _radioOn : _radioOff).Draw(dot, false, false, false, false);
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.Space(ClaudiaSkin.Gap);
            GUILayout.BeginVertical();
            GUILayout.Label(title, titleStyle);
            if (note.Length > 0) GUILayout.Label(note, ClaudiaSkin.Muted);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (right.Length > 0) GUILayout.Label(right, ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            return Clicked();
        }

        /// <summary>The layout group just ended was clicked.</summary>
        private static bool Clicked()
        {
            var e = Event.current;
            if (!GUI.enabled || e.type != EventType.MouseDown || e.button != 0 || !GUILayoutUtility.GetLastRect().Contains(e.mousePosition)) return false;
            e.Use();
            return true;
        }

        private void DrawDashedNote(string text)
        {
            GUILayout.Label(text, _note);
            if (Event.current.type != EventType.Repaint) return;
            var rect = GUILayoutUtility.GetLastRect();
            var dash = ClaudiaSkin.Solid(ClaudiaSkin.BorderStrong);
            for (var x = rect.x; x < rect.xMax; x += 8)
            {
                var w = Mathf.Min(4, rect.xMax - x);
                GUI.DrawTexture(new Rect(x, rect.y, w, 1), dash);
                GUI.DrawTexture(new Rect(x, rect.yMax - 1, w, 1), dash);
            }
            for (var y = rect.y; y < rect.yMax; y += 8)
            {
                var h = Mathf.Min(4, rect.yMax - y);
                GUI.DrawTexture(new Rect(rect.x, y, 1, h), dash);
                GUI.DrawTexture(new Rect(rect.xMax - 1, y, 1, h), dash);
            }
        }

        // ---- Claudia Kermin ----

        private void DrawClaudiaKermin()
        {
            var expanded = _context == Context.SettingUp ? _expandedSettingUp : _expandedDefaults;
            GUILayout.BeginHorizontal(expanded ? _headerOpen : _header, GUILayout.MinHeight(52));
            var portrait = GUILayoutUtility.GetRect(30, 30, GUILayout.Width(30), GUILayout.Height(30));
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(portrait, Portrait(), ScaleMode.ScaleToFit);
            GUILayout.Space(ClaudiaSkin.Gap);
            GUILayout.BeginVertical();
            GUILayout.Label("Claudia Kermin", ClaudiaSkin.Heading);
            GUILayout.Label("API key · Prepare to uninstall", ClaudiaSkin.Secondary);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            ClaudiaSkin.IconLabel(expanded ? ClaudiaSkin.IconShape.ChevronUp : ClaudiaSkin.IconShape.ChevronDown, ClaudiaSkin.TextSecondary, 16);
            GUILayout.EndHorizontal();
            if (Clicked())
            {
                expanded = !expanded;
                if (_context == Context.SettingUp) _expandedSettingUp = expanded;
                else _expandedDefaults = expanded;
            }
            if (!expanded) return;

            GUILayout.Space(ClaudiaSkin.Gap);
            DrawApiKey();
            GUILayout.Space(ClaudiaSkin.Gap);
            DrawUninstall();
            if (_config.DebugTools)
            {
                GUILayout.Space(ClaudiaSkin.Gap);
                if (ClaudiaSkin.SecondaryButton("Basic chat (debug)", GUILayout.ExpandWidth(true))) _chat.Visible = true;
            }
        }

        private void DrawApiKey()
        {
            ClaudiaSkin.BeginCard();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Anthropic API key", _label);
            GUILayout.FlexibleSpace();
            var saved = _keys.MaskedKey;
            if (saved.Length > 0) GUILayout.Label("Saved " + saved, ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            if (_keys.UsingEnvironmentKey) GUILayout.Label("ANTHROPIC_API_KEY is set and takes priority.", ClaudiaSkin.Secondary);

            GUI.SetNextControlName(KeyControl);
            _typed = GUILayout.PasswordField(_typed, '*', 200, ClaudiaSkin.TextField);
            if (_typed.Length == 0 && GUI.GetNameOfFocusedControl() != KeyControl && Event.current.type == EventType.Repaint)
            {
                var field = GUILayoutUtility.GetLastRect();
                ClaudiaSkin.Muted.Draw(new Rect(field.x + 9, field.y + 8, field.width - 18, field.height - 12), "Paste a new key to replace it", false, false, false, false);
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = !_keys.Checking && _typed.Trim().Length > 0;
            if (ClaudiaSkin.PrimaryButton("Save and check", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0)))
            {
                _keys.Save(_typed);
                _typed = "";
            }
            GUILayout.Space(ClaudiaSkin.Gap);
            GUI.enabled = !_keys.Checking;
            if (ClaudiaSkin.SecondaryButton("Check saved key", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0))) _keys.Check();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            var status = _keys.Status;
            if (status.StartsWith("Key is valid.", StringComparison.Ordinal))
            {
                GUILayout.BeginHorizontal();
                ClaudiaSkin.IconLabel(ClaudiaSkin.IconShape.Check, ClaudiaSkin.Success, 14);
                GUILayout.Label(status, ClaudiaSkin.SuccessText);
                GUILayout.EndHorizontal();
            }
            else if (status.Length > 0)
            {
                GUILayout.Label(status, ClaudiaSkin.Secondary);
            }
            ClaudiaSkin.EndCard();
        }

        private void DrawUninstall()
        {
            GUILayout.BeginVertical(_uninstallBox);
            GUILayout.Label("BEFORE REMOVING THE MOD", _dangerLabel);
            GUILayout.Label("Turns every Kerbal with the Super Intelligence role in this save into a Pilot. Save the game afterwards, then remove the mod.", ClaudiaSkin.Secondary);
            GUI.enabled = UninstallPrep.SaveIsLoaded;
            if (ClaudiaSkin.DangerButton("Prepare to uninstall", GUILayout.ExpandWidth(true))) _uninstallStatus = UninstallPrep.Run();
            GUI.enabled = true;
            if (!UninstallPrep.SaveIsLoaded) GUILayout.Label("Load a save to use this.", ClaudiaSkin.Muted);
            if (_uninstallStatus.Length > 0) GUILayout.Label(_uninstallStatus, ClaudiaSkin.Secondary);
            GUILayout.EndVertical();
        }

        // ---- Footer ----

        private void DrawFooter()
        {
            GUILayout.Space(ClaudiaSkin.Gap);
            var caption = _context == Context.SettingUp ? "Apply sets these for this Mission. The key buttons act right away."
                : _context == Context.Defaults ? "Apply saves the Mission defaults. The key buttons act right away." : "";
            if (caption.Length > 0) GUILayout.Label(caption, ClaudiaSkin.Muted);
            if (_applyError.Length > 0) GUILayout.Label(_applyError, ClaudiaSkin.DangerText);
            GUILayout.BeginHorizontal();
            if (ClaudiaSkin.SecondaryButton("Cancel", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0))) Close();
            GUILayout.Space(ClaudiaSkin.Gap);
            GUI.enabled = CapError().Length == 0;
            if (ClaudiaSkin.PrimaryButton("Apply", GUILayout.ExpandWidth(true), GUILayout.MinWidth(0))) Apply();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void Apply()
        {
            decimal cap;
            if (!TryCap(out cap)) return;
            switch (_context)
            {
                case Context.DuringMission:
                    // The core ignores a cap that isn't higher; a lower one never gets here (CapError).
                    if (cap > _view.SpendingCap) _missions.Send(new RaiseCap(cap));
                    if (_view.CanEditSettings && !_settings.SameAs(_view.Settings)) _missions.Send(new EditSettings(_settings.Copy()));
                    break;
                case Context.SettingUp:
                    ApplyToSetup(cap);
                    if (_processorPicked) _missions.Send(new ChooseProcessor(_processor));
                    break;
                default:
                    _config.DefaultRemote = _remote;
                    _config.SetDefaultModel(_model);
                    _config.DefaultSpendingCap = cap;
                    _config.DefaultDaylightLanding = _settings.DaylightLanding;
                    _config.DefaultTimeVersusDeltaV = _settings.TimeVersusDeltaV;
                    try
                    {
                        _config.SaveDefaults(_configDirectory);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        _applyError = "Couldn't save config.cfg: " + e.Message;
                        return;
                    }
                    // The next Mission set up starts from the new defaults.
                    ApplyToSetup(cap);
                    break;
            }
            Close();
        }

        private void ApplyToSetup(decimal cap)
        {
            var setup = _missions.Setup;
            setup.Remote = _remote;
            setup.Model = _model;
            setup.SpendingCap = cap;
            setup.Settings = _settings.Copy();
        }

        // ---- Look ----

        private void BuildStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(ClaudiaSkin.Body) { fontStyle = FontStyle.Bold };
            _dangerLabel = new GUIStyle(ClaudiaSkin.DangerText) { fontSize = 11, fontStyle = FontStyle.Bold };
            _note = new GUIStyle(ClaudiaSkin.Muted) { padding = new RectOffset(ClaudiaSkin.Padding, ClaudiaSkin.Padding, 10, 10) };
            _row = ClaudiaSkin.Box(ClaudiaSkin.InputBackground, ClaudiaSkin.Border);
            _rowOn = ClaudiaSkin.Box(Color.Lerp(ClaudiaSkin.InputBackground, ClaudiaSkin.Accent, 0.08f), ClaudiaSkin.Accent);
            _tile = ClaudiaSkin.Box(ClaudiaSkin.RaisedBackground, ClaudiaSkin.Border);
            _header = ClaudiaSkin.Box(ClaudiaSkin.RaisedBackground, ClaudiaSkin.BorderStrong);
            _headerOpen = ClaudiaSkin.Box(ClaudiaSkin.RaisedBackground, ClaudiaSkin.Accent);
            _uninstallBox = ClaudiaSkin.Box(Color.Lerp(ClaudiaSkin.WindowBackground, ClaudiaSkin.Danger, 0.06f), ClaudiaSkin.DangerBorder);
            _radioOn = ClaudiaSkin.Box(ClaudiaSkin.Accent, ClaudiaSkin.Accent, 7);
            _radioOff = ClaudiaSkin.Box(ClaudiaSkin.InputBackground, ClaudiaSkin.BorderStrong, 7);
        }

        // The Winged glare portrait; a plain Claudia-blue tile if it didn't load.
        private Texture2D Portrait()
        {
            if (_portrait != null) return _portrait;
            var icon = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(PortraitUrl, false) : null;
            return _portrait = icon != null ? icon : ClaudiaSkin.Solid(ClaudiaSkin.Claudia);
        }

        private static string Dollars(decimal amount)
        {
            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
