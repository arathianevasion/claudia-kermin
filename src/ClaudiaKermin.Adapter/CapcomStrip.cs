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
    /// The Capcom strip: a bar in Flight, under the altimeter until the player drags it by its status row (double-click
    /// that row to put it back); where it sits is kept in windows.cfg as <c>flight.capcom</c>. Collapsed, it shows
    /// Claudia's portrait, her status chip and latest line, the spend meter, the Mission Plan's sequences as a segment
    /// track (scrolling sideways when they don't fit) and the lock line, with a tab that expands the docked
    /// <see cref="MissionPanel"/> (and turns amber with what Claudia needs from the player). The tab and the panel are
    /// below the strip, or above it when the panel has no room below (<see cref="CapcomStripLayout"/>). Undocked, the tab
    /// opens the floating Mission window instead. One <see cref="WindowFrame"/> covers the strip, the panel and the tab,
    /// so the input locks hold over all of them. The docked panel resizes from its left, right and far edge and the
    /// corners there; its size (the width is the strip's too) is kept as <c>flight.panel</c>.
    /// </summary>
    public class CapcomStrip
    {
        private const float DefaultWidth = 740;
        private static readonly float MinWidth = ClaudiaSkin.Scaled(520);
        private const float Top = 70;
        private const string MemoryName = "capcom";
        private static readonly float PortraitSize = ClaudiaSkin.Scaled(44);
        private const float PortraitRadius = 8;
        private const float SegmentGap = 6;
        private const float SegmentBarHeight = 6;
        private static readonly float ArrowWidth = ClaudiaSkin.Scaled(20);
        // A drag shorter than this was a click (the first of a double-click), not a move.
        private const float DragThreshold = 3;
        private static readonly float MeterWidth = ClaudiaSkin.Scaled(72);
        private const float DefaultPanelHeight = 640;
        private const string PanelMemoryName = "panel";
        // Below this the prompt and the control bar leave the transcript no room; only a very short screen gets there.
        private const float MinPanelHeight = 200;
        private const int TabRadius = 6;
        private const string PortraitUrl = "ClaudiaKermin/Icons/ClaudiaPortrait";

        private readonly MissionPanel _panel;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B04, "", "ClaudiaKerminCapcom", new Rect(0, Top, DefaultWidth, 0), new Vector2(MinWidth, MinPanelHeight), false);
        private Texture2D _portrait;

        // Built from ClaudiaSkin's styles, so they follow its font sizes; rebuilt when it rebuilds.
        private GUIStyle _builtFrom;
        private GUIStyle _window, _strip, _stripDocked, _panelBox, _tab, _tabAttention;
        private GUIStyle _line, _segment, _segmentCurrent, _you, _tabText, _tabTextAttention;

        // What the layout looked like last frame: a change lets the window shrink to fit again.
        private string _shape = "";
        // The strip's own height last Repaint, for clamping the panel to the screen.
        private float _stripHeight = 120;

        // Where the player put the strip (its top left); unmoved, it sits under the altimeter.
        private bool _moved;
        private Vector2 _position;
        private bool _memoryApplied;
        // A press on the status row; it becomes a move once the pointer has gone past the threshold.
        private bool _dragging, _dragMoved;
        private Vector2 _dragFromMouse, _dragFromStrip;
        // This frame's placement: the strip kept on screen, the side its tab and panel are on.
        private StripPlacement _place;

        // The size the player gave the docked panel: its width is the whole window's (strip, panel and tab), and its height
        // is what it asks for, which the screen may cut short. _width is this frame's width, kept within the screen.
        private float _wantWidth = DefaultWidth, _wantPanelHeight = DefaultPanelHeight, _width = DefaultWidth;
        // The panel's rect in the window last Repaint (empty when it isn't open), where a press starts a resize.
        private Rect _panelArea;

        // How far the segment track is scrolled, and the current segment it last scrolled to show.
        private float _scroll;
        private string _followedKey, _followedMission;
        private float _trackDragFromX, _trackDragFromScroll;
        // The track's control while it is being dragged, 0 otherwise.
        private int _trackDragId;

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
            ApplyRemembered();
            if (_dragging) Drag();
            var expanded = PanelShowing;
            if (!expanded) EndResize();
            else StepResize();
            _width = Mathf.Min(_wantWidth, Screen.width);
            // Placed every frame from where the player left it, so a smaller screen re-clamps it and a larger one gives it back.
            var at = _moved ? _position : new Vector2((Screen.width - _width) / 2f, Top);
            _place = CapcomStripLayout.Place(at.x, at.y, new StripSize(_width, _stripHeight, TabHeight(), _wantPanelHeight, MinPanelHeight), Screen.width, Screen.height);
            var above = _place.Side == PanelSide.Above;

            // The size is in the shape, so the window re-fits to it every frame of a resize (it only grows on its own).
            var shape = expanded + "|" + _place.Side + "|" + (View.PlanSequences.Count > 0) + "|" + MissionWindowRules.LockLine(View) + "|" + Screen.height
                + "|" + Mathf.RoundToInt(_width) + "|" + Mathf.RoundToInt(_place.PanelHeight);
            // It grows to fit; with the tab (and panel) above, the window starts above the strip.
            var height = shape == _shape ? _frame.Rect.height : 0;
            _shape = shape;
            var y = above ? _place.Y - TabHeight() - (expanded ? _place.PanelHeight : 0) : _place.Y;
            _frame.Rect = new Rect(_place.X, y, _width, height);
            _frame.Style = _window;
            _frame.Show(Draw, GUILayout.Width(_width));
            // Keys typed into the docked prompt must not stage, throttle or rotate the craft.
            _frame.SetTyping(expanded && MissionPanel.Typing);
        }

        /// <summary>Releases the input locks; call when the strip goes away.</summary>
        public void Unlock()
        {
            _dragging = false;
            _panelArea = default(Rect);
            EndTrackDrag();
            _frame.Unlock();
        }

        // A track drag whose track went away (or stopped scrolling) mustn't keep the pointer captured.
        private void EndTrackDrag()
        {
            if (_trackDragId != 0 && GUIUtility.hotControl == _trackDragId) GUIUtility.hotControl = 0;
            _trackDragId = 0;
        }

        private void Draw(int id)
        {
            var expanded = PanelShowing;
            var above = _place.Side == PanelSide.Above;
            // Away from the strip the panel's edges resize it; first, so a control at the edge can't take the press.
            var resizeEdges = ResizeZone.Left | ResizeZone.Right | (above ? ResizeZone.Top : ResizeZone.Bottom);
            if (expanded) _frame.HandleResize(_panelArea, resizeEdges);
            var tabRow = above ? TabRow() : default(Rect);
            if (above && expanded) DrawPanel();

            GUILayout.BeginVertical(expanded ? _stripDocked : _strip);
            DrawStatusRow();
            GUILayout.Space(ClaudiaSkin.Gap);
            DrawTrack();
            DrawProgress();
            DrawLockLine();
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _stripHeight = GUILayoutUtility.GetLastRect().height;

            if (!above && expanded) DrawPanel();
            if (!above) tabRow = TabRow();
            if (expanded) _frame.DrawResizeHint(_panelArea, resizeEdges);
            // Drawn last, over the edge of what it joins.
            DrawTab(tabRow, expanded, above);
            WindowFrame.DrawTooltip();
        }

        // About 640 px, less where the strip leaves less room: the strip, the panel and the tab all stay on screen.
        private void DrawPanel()
        {
            GUILayout.BeginVertical(_panelBox, GUILayout.Height(_place.PanelHeight));
            _panel.Draw(View, _width - 2, true, ClaudiaSkin.Padding);
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _panelArea = GUILayoutUtility.GetLastRect();
        }

        // ---- Resizing the docked panel ----

        // While a resize is under way the panel follows the pointer: its width is the window's, so the left edge moves the
        // strip too; its height is what it asks for. Let go, it is kept.
        private void StepResize()
        {
            var was = _frame.Resizing;
            Rect area;
            if (_frame.TryResize(out area))
            {
                _wantWidth = area.width;
                _wantPanelHeight = area.height;
                _moved = true;
                _position = new Vector2(area.x, _place.Y);
            }
            else if (was) StoreResize();
        }

        // The panel is closing (or the strip going) mid-resize.
        private void EndResize()
        {
            _panelArea = default(Rect);
            if (!_frame.Resizing) return;
            _frame.Unlock();
            StoreResize();
        }

        // Kept as it shows on this screen, so a pull past the room doesn't come back when the strip is moved. The box's
        // position is the strip's; only its size is read back.
        private void StoreResize()
        {
            _wantWidth = _width;
            _wantPanelHeight = _place.PanelHeight;
            _position = new Vector2(_place.X, _place.Y);
            WindowMemory.Store(PanelMemoryName, new WindowBox(_position.x, _position.y, _wantWidth, _wantPanelHeight));
            WindowMemory.Store(MemoryName, new WindowBox(_position.x, _position.y, _wantWidth, _stripHeight));
        }

        // ---- Moving the strip ----

        // On the first draw, where the player last left it.
        private void ApplyRemembered()
        {
            if (_memoryApplied) return;
            _memoryApplied = true;
            WindowBox box;
            _moved = WindowMemory.TryGet(MemoryName, out box);
            if (_moved) _position = new Vector2(box.X, box.Y);
            if (WindowMemory.TryGet(PanelMemoryName, out box))
            {
                _wantWidth = Mathf.Max(box.Width, MinWidth);
                _wantPanelHeight = Mathf.Max(box.Height, MinPanelHeight);
            }
        }

        // Follows the pointer while the button is held; saved once it is let go, if it moved.
        private void Drag()
        {
            var delta = GuiMouse() - _dragFromMouse;
            if (!Input.GetMouseButton(0))
            {
                _dragging = false;
                if (!_dragMoved) return;
                // Kept as placed on this screen, so a later, smaller screen doesn't remember it squeezed.
                _position = new Vector2(_place.X, _place.Y);
                WindowMemory.Store(MemoryName, new WindowBox(_position.x, _position.y, _width, _stripHeight));
                return;
            }
            if (!_dragMoved && delta.magnitude < DragThreshold) return;
            _dragMoved = _moved = true;
            _position = _dragFromStrip + delta;
        }

        // Back under the altimeter, and forgotten, so it follows the screen's centre again.
        private void ResetPosition()
        {
            _dragging = false;
            _moved = false;
            WindowMemory.Forget(MemoryName);
        }

        // A press in the status row starts a drag; a double-click resets.
        private void HandleStatusRowInput(Rect row)
        {
            GUI.Label(row, new GUIContent("", "Drag to move the strip; double-click to put it back under the altimeter"), GUIStyle.none);
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !row.Contains(e.mousePosition)) return;
            if (e.clickCount == 2) ResetPosition();
            else
            {
                _dragging = true;
                _dragMoved = false;
                _dragFromMouse = GuiMouse();
                _dragFromStrip = new Vector2(_place.X, _place.Y);
            }
            e.Use();
        }

        private static Vector2 GuiMouse()
        {
            return new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        }

        // ---- Status row ----

        // The portrait; "Claudia", the status chip and her latest line; the spend text over its meter on the right. The row is the strip's handle.
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
            HandleStatusRowInput(GUILayoutUtility.GetLastRect());
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

        // One segment per sequence of the Mission Plan, a bar over "N · name", sharing the track equally down to
        // ClaudiaSkin's minimum width; past that the track scrolls by drag, wheel or the arrows at its ends.
        private void DrawTrack()
        {
            var segments = MissionWindowRules.Segments(View);
            // Taken on every event, so the drag's id stays the same from one event to the next.
            var id = GUIUtility.GetControlID(FocusType.Passive);
            if (segments.Count == 0)
            {
                EndTrackDrag();
                GUILayout.Label(MissionWindowRules.NoPlanYet, ClaudiaSkin.Secondary);
                return;
            }
            var labelHeight = _segmentCurrent.CalcHeight(new GUIContent("A"), 100);
            var row = GUILayoutUtility.GetRect(1, SegmentBarHeight + 4 + labelHeight, GUILayout.ExpandWidth(true));
            // The layout pass has no width yet.
            if (Event.current.type == EventType.Layout) return;
            var track = new SegmentTrack(segments.Count, row.width, ClaudiaSkin.MinSegmentWidth, SegmentGap);
            FollowCurrent(track, segments);
            HandleTrackInput(id, row, track);
            if (Event.current.type != EventType.Repaint) return;

            GUI.BeginClip(row);
            var visible = track.Visible(_scroll);
            for (var i = visible.First; i <= visible.Last; i++)
            {
                var x = track.X(i) - _scroll;
                DrawBar(new Rect(x, 0, track.SegmentWidth, SegmentBarHeight), segments[i].Bar);
                DrawSegmentLabel(new Rect(x, SegmentBarHeight + 4, track.SegmentWidth, labelHeight), segments[i]);
            }
            GUI.EndClip();
            if (_scroll > 0) DrawArrow(LeftArrow(row), ClaudiaSkin.IconShape.ChevronLeft);
            if (_scroll < track.MaxScroll) DrawArrow(RightArrow(row), ClaudiaSkin.IconShape.ChevronRight);
        }

        // When the current sequence changes (and on first sight of the plan), it scrolls into view with the next one;
        // in between, the player's scroll is kept, only clamped to the track.
        private void FollowCurrent(SegmentTrack track, IList<PlanSegment> segments)
        {
            var current = -1;
            for (var i = 0; i < segments.Count; i++)
                if (segments[i].State == SegmentState.Current) current = i;
            var key = View.MissionId + "|" + segments.Count + "|" + current;
            if (key == _followedKey)
            {
                _scroll = track.Clamp(_scroll);
                return;
            }
            // A new Mission starts its track from the left.
            if (View.MissionId != _followedMission) _scroll = 0;
            _scroll = track.ScrollToShow(_scroll, current);
            _followedKey = key;
            _followedMission = View.MissionId;
        }

        private void HandleTrackInput(int id, Rect row, SegmentTrack track)
        {
            var e = Event.current;
            var step = track.SegmentWidth + track.Gap;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (!track.Scrolls || e.button != 0 || !row.Contains(e.mousePosition)) return;
                    if (_scroll > 0 && LeftArrow(row).Contains(e.mousePosition)) _scroll = track.Clamp(_scroll - step);
                    else if (_scroll < track.MaxScroll && RightArrow(row).Contains(e.mousePosition)) _scroll = track.Clamp(_scroll + step);
                    else
                    {
                        GUIUtility.hotControl = _trackDragId = id;
                        _trackDragFromX = e.mousePosition.x;
                        _trackDragFromScroll = _scroll;
                    }
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) return;
                    _scroll = track.Clamp(_trackDragFromScroll - (e.mousePosition.x - _trackDragFromX));
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) return;
                    EndTrackDrag();
                    e.Use();
                    break;
                case EventType.ScrollWheel:
                    if (!track.Scrolls || !row.Contains(e.mousePosition)) return;
                    // A notch (about 3) moves about a segment; a sideways wheel or trackpad works too.
                    var delta = Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y) ? e.delta.x : e.delta.y;
                    _scroll = track.Clamp(_scroll + delta * step / 3f);
                    e.Use();
                    break;
            }
        }

        private static Rect LeftArrow(Rect row)
        {
            return new Rect(row.x, row.y, ArrowWidth, row.height);
        }

        private static Rect RightArrow(Rect row)
        {
            return new Rect(row.xMax - ArrowWidth, row.y, ArrowWidth, row.height);
        }

        // A small chevron on the strip's background over the track's end: there is more that way.
        private static void DrawArrow(Rect rect, ClaudiaSkin.IconShape shape)
        {
            GUI.DrawTexture(rect, ClaudiaSkin.Solid(ClaudiaSkin.BarBackground));
            var hover = rect.Contains(Event.current.mousePosition);
            ClaudiaSkin.DrawIcon(new Rect(rect.center.x - 7, rect.center.y - 7, 14, 14), shape, hover ? ClaudiaSkin.Text : ClaudiaSkin.TextSecondary);
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

        // ---- Live progress ----

        // While Running: the block, time and key numbers over a thin bar of time elapsed against the plan. No Claude call is behind it.
        private void DrawProgress()
        {
            var text = MissionWindowRules.ProgressLine(View);
            if (text.Length == 0) return;
            GUILayout.Space(ClaudiaSkin.Gap / 2);
            GUILayout.Label(text, ClaudiaSkin.Secondary, GUILayout.MinWidth(0));
            var bar = GUILayoutUtility.GetRect(0, 3, GUILayout.ExpandWidth(true), GUILayout.Height(3));
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(bar, ClaudiaSkin.Solid(ClaudiaSkin.Border));
            var fraction = Mathf.Clamp01((float)View.RunProgress.Fraction);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * fraction, bar.height), ClaudiaSkin.Solid(ClaudiaSkin.Claudia));
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

        // The tab's row across the window, taken in layout order: above the strip (and panel) or below.
        private Rect TabRow()
        {
            var height = TabHeight();
            return GUILayoutUtility.GetRect(_width, height, GUILayout.ExpandWidth(true), GUILayout.Height(height));
        }

        // A tab centred on the strip's (or the panel's) edge, joined to it: a chevron pointing the way the panel opens and
        // "Chat", amber with what Claudia needs from the player, or the chevron back and "Collapse" when the docked panel is open.
        private void DrawTab(Rect row, bool expanded, bool above)
        {
            var needs = !expanded && MissionWindowRules.NeedsYou(View).Length > 0;
            var text = MissionWindowRules.ExpandTab(View, expanded);
            var textStyle = needs ? _tabTextAttention : _tabText;
            var textWidth = Mathf.Min(textStyle.CalcSize(new GUIContent(text)).x, _width - 120);
            var tabWidth = textWidth + 14 + 6 + 2 * ClaudiaSkin.Padding;

            var tab = new Rect(row.x + (row.width - tabWidth) / 2f, row.y, tabWidth, row.height);
            // The Due Time in the tab reads from now; the date it lands on is the hover.
            var hover = needs && View.DueTime.HasDueTime ? View.DueTime.Date : "";
            if (GUI.Button(tab, new GUIContent("", hover), GUIStyle.none) && TabClicked != null) TabClicked();
            if (Event.current.type != EventType.Repaint) return;

            // The box runs under what it joins so only its outer corners show; that edge's border is cleared where they join.
            var style = needs ? _tabAttention : _tab;
            var join = TabRadius + 2;
            style.Draw(new Rect(tab.x, above ? tab.y : tab.y - join, tab.width, tab.height + join), false, false, false, false);
            GUI.DrawTexture(new Rect(tab.x + 1, above ? tab.yMax : tab.y - join, tab.width - 2, join), ClaudiaSkin.Solid(needs ? ClaudiaSkin.AttentionFill : ClaudiaSkin.BarBackground));

            var color = needs ? ClaudiaSkin.Attention : ClaudiaSkin.TextSecondary;
            var x = tab.x + ClaudiaSkin.Padding;
            var pointsUp = expanded != above;
            ClaudiaSkin.DrawIcon(new Rect(x, tab.center.y - 7, 14, 14), pointsUp ? ClaudiaSkin.IconShape.ChevronUp : ClaudiaSkin.IconShape.ChevronDown, color);
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
