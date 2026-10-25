// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The Capcom strip: a bar under the altimeter in Flight. Collapsed, it shows Claudia's portrait, her status chip and
    /// latest line, the spend meter, the Mission Plan's sequences as a segment track and the lock line, with a tab under
    /// it that expands the docked <see cref="MissionPanel"/> (and turns amber with what Claudia needs from the player).
    /// Docked and expanded, the panel hangs under the strip with a Collapse tab under it. Undocked, the tab opens the
    /// floating Mission window instead. One <see cref="WindowFrame"/> covers the strip, the panel and the tab, so the
    /// input locks hold over all of them.
    /// </summary>
    public class CapcomStrip
    {
        private const float Width = 620;
        private const float Top = 70;
        private const float PortraitSize = 44;
        private const float PortraitRadius = 8;
        private const float SegmentGap = 6;
        private const float SegmentBarHeight = 6;
        private const float MeterWidth = 72;
        private const float PanelHeight = 640;
        private const float MinPanelHeight = 320;
        private const int TabRadius = 6;
        private const string PortraitUrl = "ClaudiaKermin/Icons/ClaudiaPortrait";

        private readonly MissionPanel _panel;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B04, "", "ClaudiaKerminCapcom", new Rect(0, Top, Width, 0), Vector2.zero, false);
        private Texture2D _portrait;

        // Built from ClaudiaSkin's styles, so they follow its font sizes; rebuilt when it rebuilds.
        private GUIStyle _builtFrom;
        private GUIStyle _window, _strip, _stripDocked, _panelBox, _tab, _tabAttention;
        private GUIStyle _line, _segment, _segmentCurrent, _you, _tabText, _tabTextAttention;

        // What the layout looked like last frame: a change lets the window shrink to fit again.
        private string _shape = "";
        // The strip's own height last Repaint, for clamping the panel to the screen.
        private float _stripHeight = 120;

        public bool UiHidden;

        /// <summary>In Flight the Mission panel is docked under the strip (not undocked to the floating window); set by the owner.</summary>
        public bool Docked = true;

        /// <summary>The docked panel is expanded under the strip.</summary>
        public bool Expanded;

        /// <summary>The expand tab was clicked; set by the owner (it expands the docked panel or toggles the floating window).</summary>
        public Action TabClicked;

        /// <summary>Set before each OnGUI.</summary>
        public MissionView View;

        public CapcomStrip(MissionPanel panel)
        {
            _panel = panel;
        }

        /// <summary>The strip is on screen, so the player can see Claudia's line without a window.</summary>
        public bool Showing
        {
            get { return !UiHidden && View != null && View.Stage != MissionStage.None; }
        }

        /// <summary>The docked panel is on screen.</summary>
        public bool PanelShowing
        {
            get { return Showing && Docked && Expanded; }
        }

        public void OnGUI()
        {
            if (!Showing)
            {
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            Styles();
            var expanded = PanelShowing;
            var shape = expanded + "|" + (View.PlanSequences.Count > 0) + "|" + MissionWindowRules.LockLine(View) + "|" + Screen.height;
            // Under the altimeter at the top centre; it grows to fit and can't be dragged off.
            var height = shape == _shape ? _frame.Rect.height : 0;
            _shape = shape;
            _frame.Rect = new Rect((Screen.width - Width) / 2f, Top, Width, height);
            _frame.Style = _window;
            _frame.Show(Draw, GUILayout.Width(Width));
            // Keys typed into the docked prompt must not stage, throttle or rotate the craft.
            _frame.SetTyping(expanded && MissionPanel.Typing);
        }

        /// <summary>Releases the input locks; call when the strip goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private void Draw(int id)
        {
            var expanded = PanelShowing;
            GUILayout.BeginVertical(expanded ? _stripDocked : _strip);
            DrawStatusRow();
            GUILayout.Space(ClaudiaSkin.Gap);
            DrawTrack();
            DrawLockLine();
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _stripHeight = GUILayoutUtility.GetLastRect().height;

            if (expanded)
            {
                // About 640 px, less on a short screen: the strip, the panel and the tab all stay on it.
                var room = Screen.height - Top - _stripHeight - TabHeight() - ClaudiaSkin.Gap;
                GUILayout.BeginVertical(_panelBox, GUILayout.Height(Mathf.Clamp(room, MinPanelHeight, PanelHeight)));
                _panel.Draw(View, Width - 2, true, ClaudiaSkin.Padding);
                GUILayout.EndVertical();
            }
            DrawTab(expanded);
            WindowFrame.DrawTooltip();
        }

        // ---- Status row ----

        // The portrait; "Claudia", the status chip and her latest line; the spend text over its meter on the right.
        private void DrawStatusRow()
        {
            GUILayout.BeginHorizontal();
            var portrait = GUILayoutUtility.GetRect(PortraitSize, PortraitSize, GUILayout.Width(PortraitSize), GUILayout.Height(PortraitSize));
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(portrait, Portrait(), ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, PortraitRadius);
            GUILayout.Space(ClaudiaSkin.Gap + 2);

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.MinWidth(0));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Claudia", ClaudiaSkin.ClaudiaName, GUILayout.ExpandWidth(false));
            GUILayout.Space(ClaudiaSkin.Gap);
            var chip = MissionWindowRules.StripStatus(View);
            if (chip != null) DrawChip(chip);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            OneLine(MissionWindowRules.StripLine(View), _line);
            GUILayout.EndVertical();

            GUILayout.Space(ClaudiaSkin.Gap);
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.Label(MissionWindowRules.SpendMeter(View), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            ClaudiaSkin.SpendBar(View, MeterWidth);
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        // The chip's dot and text: the chip style with a dot drawn in its left padding.
        private static void DrawChip(StatusChip chip)
        {
            var style = ClaudiaSkin.ChipStyle(ClaudiaSkin.KindOf(chip.Tone));
            var content = new GUIContent("    " + chip.Text);
            var rect = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(false));
            if (Event.current.type != EventType.Repaint) return;
            style.Draw(rect, content, false, false, false, false);
            var color = chip.Tone == ChipTone.Attention ? ClaudiaSkin.Attention : ClaudiaSkin.Claudia;
            ClaudiaSkin.DrawIcon(new Rect(rect.x + style.padding.left, rect.center.y - 4, 8, 8), ClaudiaSkin.IconShape.Dot, color);
        }

        // One line of text, ellipsized to the room the layout gives it.
        private static void OneLine(string text, GUIStyle style)
        {
            var content = new GUIContent(text);
            var rect = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(true), GUILayout.MinWidth(0));
            if (Event.current.type != EventType.Repaint) return;
            style.Draw(rect, new GUIContent(ClaudiaSkin.Ellipsize(text, style, rect.width), text), false, false, false, false);
        }

        // ---- Segment track ----

        // One segment per sequence of the Mission Plan in an equal-width grid: a bar over "N · name".
        private void DrawTrack()
        {
            var segments = MissionWindowRules.Segments(View);
            if (segments.Count == 0)
            {
                GUILayout.Label(MissionWindowRules.NoPlanYet, ClaudiaSkin.Secondary);
                return;
            }
            var labelHeight = _segmentCurrent.CalcHeight(new GUIContent("A"), 100);
            var row = GUILayoutUtility.GetRect(1, SegmentBarHeight + 4 + labelHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            var width = (row.width - SegmentGap * (segments.Count - 1)) / segments.Count;
            for (var i = 0; i < segments.Count; i++)
            {
                var x = row.x + i * (width + SegmentGap);
                DrawBar(new Rect(x, row.y, width, SegmentBarHeight), segments[i].Bar);
                DrawSegmentLabel(new Rect(x, row.y + SegmentBarHeight + 4, width, labelHeight), segments[i]);
            }
        }

        private static void DrawBar(Rect bar, SegmentBar kind)
        {
            switch (kind)
            {
                case SegmentBar.Accent: GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Accent)); break;
                // No progress fraction is known while Running, so the bar is filled.
                case SegmentBar.Claudia: GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Claudia)); break;
                case SegmentBar.AttentionFill: GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Attention)); break;
                case SegmentBar.ClaudiaOutline:
                    GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Border));
                    ClaudiaSkin.Outline(bar, ClaudiaSkin.Claudia);
                    break;
                case SegmentBar.AttentionOutline:
                    GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Border));
                    ClaudiaSkin.Outline(bar, ClaudiaSkin.Attention);
                    break;
                case SegmentBar.ManualDashed: ClaudiaSkin.DashedOutline(bar, ClaudiaSkin.Attention); break;
                default: GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Border)); break;
            }
        }

        // A check before a done one; bold white for the current one; "(you)" in amber after a manual stretch's name.
        private void DrawSegmentLabel(Rect rect, PlanSegment segment)
        {
            var x = rect.x;
            if (segment.State == SegmentState.Done)
            {
                ClaudiaSkin.DrawIcon(new Rect(x, rect.center.y - 6, 12, 12), ClaudiaSkin.IconShape.Check, ClaudiaSkin.Accent);
                x += 12 + 4;
            }
            var style = segment.State == SegmentState.Current ? _segmentCurrent : _segment;
            var you = segment.You ? new GUIContent(" (you)") : GUIContent.none;
            var youWidth = segment.You ? _you.CalcSize(you).x : 0;
            var label = ClaudiaSkin.Ellipsize(segment.Label, style, Mathf.Max(0, rect.xMax - x - youWidth));
            var labelWidth = Mathf.Min(style.CalcSize(new GUIContent(label)).x, rect.xMax - x - youWidth);
            style.Draw(new Rect(x, rect.y, labelWidth, rect.height), new GUIContent(label), false, false, false, false);
            if (segment.You) _you.Draw(new Rect(x + labelWidth, rect.y, youWidth, rect.height), you, false, false, false, false);
        }

        // ---- Lock line ----

        private void DrawLockLine()
        {
            var text = MissionWindowRules.LockLine(View);
            if (text.Length == 0) return;
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            GUILayout.BeginHorizontal();
            var icon = GUILayoutUtility.GetRect(12, _line.CalcHeight(new GUIContent("A"), 100), GUILayout.Width(12));
            ClaudiaSkin.DrawIcon(new Rect(icon.x, icon.center.y - 6, 12, 12), ClaudiaSkin.IconShape.Lock, ClaudiaSkin.TextSecondary);
            GUILayout.Space(6);
            GUILayout.Label(text, ClaudiaSkin.Secondary, GUILayout.MinWidth(0));
            GUILayout.EndHorizontal();
        }

        // ---- Expand tab ----

        // At least a click target high, and as high as its text needs at ClaudiaSkin's size.
        private float TabHeight()
        {
            return Mathf.Max(ClaudiaSkin.MinClickable + 4, _tabText.CalcHeight(new GUIContent("A"), 200) + 12);
        }

        // A tab centred under the strip (or the panel), joined to it: a down chevron and "Chat", amber with what Claudia
        // needs from the player, or an up chevron and "Collapse" when the docked panel is open.
        private void DrawTab(bool expanded)
        {
            var needs = !expanded && MissionWindowRules.NeedsYou(View).Length > 0;
            var text = MissionWindowRules.ExpandTab(View, expanded);
            var textStyle = needs ? _tabTextAttention : _tabText;
            var height = TabHeight();
            var textWidth = Mathf.Min(textStyle.CalcSize(new GUIContent(text)).x, Width - 120);
            var tabWidth = textWidth + 14 + 6 + 2 * ClaudiaSkin.Padding;

            var row = GUILayoutUtility.GetRect(Width, height, GUILayout.ExpandWidth(true), GUILayout.Height(height));
            var tab = new Rect(row.x + (row.width - tabWidth) / 2f, row.y, tabWidth, height);
            if (GUI.Button(tab, GUIContent.none, GUIStyle.none) && TabClicked != null) TabClicked();
            if (Event.current.type != EventType.Repaint) return;

            // The box runs up under the strip so only its bottom corners show; the strip's bottom border is cleared where they join.
            var style = needs ? _tabAttention : _tab;
            style.Draw(new Rect(tab.x, tab.y - TabRadius - 2, tab.width, tab.height + TabRadius + 2), false, false, false, false);
            GUI.DrawTexture(new Rect(tab.x + 1, tab.y - TabRadius - 2, tab.width - 2, TabRadius + 2), ClaudiaSkin.Solid(needs ? ClaudiaSkin.AttentionFill : ClaudiaSkin.BarBackground));

            var color = needs ? ClaudiaSkin.Attention : ClaudiaSkin.TextSecondary;
            var x = tab.x + ClaudiaSkin.Padding;
            ClaudiaSkin.DrawIcon(new Rect(x, tab.center.y - 7, 14, 14), expanded ? ClaudiaSkin.IconShape.ChevronUp : ClaudiaSkin.IconShape.ChevronDown, color);
            var shown = ClaudiaSkin.Ellipsize(text, textStyle, textWidth);
            textStyle.Draw(new Rect(x + 14 + 6, tab.y, textWidth, tab.height), new GUIContent(shown), tab.Contains(Event.current.mousePosition), false, false, false);
        }

        private void Styles()
        {
            if (_window != null && ReferenceEquals(_builtFrom, ClaudiaSkin.Secondary)) return;
            _builtFrom = ClaudiaSkin.Secondary;

            // The window itself is invisible: the strip, the panel and the tab draw their own backgrounds.
            _window = new GUIStyle { padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            _strip = ClaudiaSkin.Box(ClaudiaSkin.BarBackground, ClaudiaSkin.BorderStrong, 8);
            _strip.padding = new RectOffset(ClaudiaSkin.Padding, ClaudiaSkin.Padding, 10, 10);
            _strip.margin = new RectOffset(0, 0, 0, 0);
            // Docked, the strip's bottom corners are squared against the panel.
            _stripDocked = ClaudiaSkin.Box(ClaudiaSkin.BarBackground, ClaudiaSkin.BorderStrong, 2);
            _stripDocked.padding = _strip.padding;
            _stripDocked.margin = _strip.margin;
            _panelBox = ClaudiaSkin.Box(ClaudiaSkin.WindowBackground, ClaudiaSkin.BorderStrong, 2);
            _panelBox.padding = new RectOffset(1, 1, 0, 1);
            _panelBox.margin = new RectOffset(0, 0, 0, 0);
            _tab = ClaudiaSkin.Box(ClaudiaSkin.BarBackground, ClaudiaSkin.BorderStrong, TabRadius);
            _tabAttention = ClaudiaSkin.Box(ClaudiaSkin.AttentionFill, ClaudiaSkin.AttentionBorder, TabRadius);

            _line = new GUIStyle(ClaudiaSkin.Body) { wordWrap = false };
            _segment = new GUIStyle(ClaudiaSkin.Secondary) { wordWrap = false, clipping = TextClipping.Clip };
            _segmentCurrent = new GUIStyle(_segment) { fontStyle = FontStyle.Bold };
            _segmentCurrent.normal.textColor = ClaudiaSkin.Text;
            _you = new GUIStyle(_segment);
            _you.normal.textColor = ClaudiaSkin.Attention;
            _tabText = new GUIStyle(ClaudiaSkin.Secondary) { wordWrap = false, alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            _tabText.hover.textColor = ClaudiaSkin.Text;
            _tabTextAttention = new GUIStyle(_tabText);
            _tabTextAttention.normal.textColor = _tabTextAttention.hover.textColor = ClaudiaSkin.Attention;
        }

        // The Winged glare portrait from GameData when it loaded; otherwise a plain Claudia-blue tile.
        private Texture2D Portrait()
        {
            if (_portrait != null) return _portrait;
            var icon = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(PortraitUrl, false) : null;
            return _portrait = icon != null ? icon : ClaudiaSkin.Solid(ClaudiaSkin.Claudia);
        }
    }
}
