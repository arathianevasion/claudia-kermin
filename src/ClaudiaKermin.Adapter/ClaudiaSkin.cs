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
    /// The look every Claudia Kermin window shares (the UI redesign's visual spec): the colour tokens, cached
    /// GUIStyles built on <c>HighLogic.Skin</c>'s fonts, and the few controls the windows draw with. Textures are
    /// made once, in code: solid fills, rounded boxes (nine-sliced, so one texture fits any size) and small icons.
    /// Every style keeps <c>richText = false</c>, since Claude's replies can contain <c>&lt;</c>. Call from OnGUI only.
    /// </summary>
    public static class ClaudiaSkin
    {
        // ---- Tokens ----

        public static readonly Color WindowBackground = Hex(0x1b2024);
        public static readonly Color RaisedBackground = Hex(0x20262b);
        public static readonly Color BarBackground = Hex(0x252b30);
        public static readonly Color CardBackground = Hex(0x232a2f);
        public static readonly Color CardHeader = Hex(0x2a3238);
        public static readonly Color InputBackground = Hex(0x15191c);
        public static readonly Color Border = Hex(0x3d464e);
        public static readonly Color BorderStrong = Hex(0x4a545c);
        public static readonly Color Text = Hex(0xdfe4e8);
        public static readonly Color TextSecondary = Hex(0x98a3ad);
        public static readonly Color TextMuted = Hex(0x8a96a0);
        public static readonly Color Accent = Hex(0xb8d94a);
        public static readonly Color OnAccent = Hex(0x1a1f10);
        public static readonly Color Claudia = Hex(0x6cbcf0);
        public static readonly Color InfoFill = Hex(0x1f3342);
        public static readonly Color InfoText = Hex(0x8fcdf4);
        public static readonly Color Attention = Hex(0xf2b04e);
        public static readonly Color AttentionFill = Hex(0x2a2720);
        public static readonly Color AttentionFillStrong = Hex(0x3a3020);
        public static readonly Color AttentionBorder = Hex(0x5a4a2c);
        public static readonly Color Success = Hex(0xc9e66a);
        public static readonly Color SuccessBorder = Hex(0x5e7a2a);
        public static readonly Color Danger = Hex(0xf19a8e);
        public static readonly Color DangerBorder = Hex(0x6a3b36);
        public static readonly Color PlayerFill = Hex(0x2e3a24);
        public static readonly Color PlayerBorder = Hex(0x4b5e2c);
        public static readonly Color IconGreen = Hex(0x8cc63f);

        // ---- Spacing ----

        /// <summary>
        /// The one place the text size is set: every font size and every fixed height that holds text is its design
        /// value (14 px body, 36 px button...) times this, rounded to whole pixels. 1.2 is the 20% bump from the #138 feedback.
        /// </summary>
        public const float FontScale = 1.2f;

        /// <summary><paramref name="designPx"/> times <see cref="FontScale"/>, rounded to whole pixels (14 gives 17, 12 gives 14, 11 gives 13, 16 gives 19).</summary>
        public static int Scaled(float designPx)
        {
            return Mathf.RoundToInt(designPx * FontScale);
        }

        public const int Padding = 12;
        public const float Gap = 8;
        public static readonly float ButtonHeight = Scaled(36);
        /// <summary>Nothing clickable is smaller than this (28 px before scaling, so never under 28).</summary>
        public static readonly float MinClickable = Scaled(28);
        /// <summary>The height of a checkbox row.</summary>
        public static readonly float CheckboxHeight = Scaled(32);
        /// <summary>The height of the title bar <see cref="Window"/> leaves for <see cref="DrawTitleBar"/>.</summary>
        public static readonly int TitleBarHeight = Scaled(36);
        /// <summary>The Capcom strip's segment track never squeezes a segment narrower than this; past it the track scrolls.</summary>
        public static readonly float MinSegmentWidth = Scaled(110);

        public enum ChipKind
        {
            Info,
            Success,
            Attention,
            Danger,
        }

        public enum IconShape
        {
            Gear,
            Lock,
            ChevronDown,
            ChevronUp,
            ChevronLeft,
            ChevronRight,
            Check,
            Close,
            Pencil,
            Warning,
            ArrowUp,
            Dot,
            /// <summary>A box with an arrow leaving it: Undock.</summary>
            ExternalLink,
            /// <summary>An arrow into a tray along the top: Dock.</summary>
            Dock,
        }

        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<IconShape, Texture2D> Icons = new Dictionary<IconShape, Texture2D>();
        private static GUISkin _builtFrom;
        private static Font _mono;
        private static GUIStyle _window, _body, _secondary, _muted, _section, _heading, _mono14, _textField;
        private static GUIStyle _primary, _secondaryButton, _danger, _segment, _segmentOn, _card, _box;
        private static GUIStyle _chipInfo, _chipSuccess, _chipAttention, _chipDanger;
        private static GUIStyle _successText, _dangerText, _switchOn, _switchOff, _knobOn, _knobOff;
        private static GUIStyle _bareWindow, _raised, _bar, _prompt, _bubble, _claudiaName, _notice, _cardHeader, _kindLabel, _titleAccent;
        private static GUIStyle _textButton, _toggleOn, _small, _disabledButton, _sendOn, _sendOff;
        private static GUIStyle _checkOn, _checkOff, _checkDone, _warningBox, _blockerBox, _askBox, _warningLabel, _blockerLabel;

        // ---- Styles ----

        /// <summary>The window: body background with a bar-coloured title strip; draw the title with <see cref="DrawTitleBar"/>.</summary>
        public static GUIStyle Window { get { Build(); return _window; } }
        /// <summary>Body text, 14 px (times FontScale), wrapping.</summary>
        public static GUIStyle Body { get { Build(); return _body; } }
        /// <summary>Secondary text, 12 px (times FontScale), wrapping.</summary>
        public static GUIStyle Secondary { get { Build(); return _secondary; } }
        /// <summary>Muted text, 12 px (times FontScale), wrapping.</summary>
        public static GUIStyle Muted { get { Build(); return _muted; } }
        /// <summary>16 px bold times FontScale, for names and headers.</summary>
        public static GUIStyle Heading { get { Build(); return _heading; } }
        /// <summary>Monospace body text (an OS font; the skin's font when none is found).</summary>
        public static GUIStyle Mono { get { Build(); return _mono14; } }
        public static GUIStyle TextField { get { Build(); return _textField; } }
        /// <summary>The card box (plans, Debriefs, Craft Check, settings groups).</summary>
        public static GUIStyle Card { get { Build(); return _card; } }

        /// <summary>A <see cref="Window"/> whose body has no padding, for windows whose sections run edge to edge and pad themselves.</summary>
        public static GUIStyle BareWindow { get { Build(); return _bareWindow; } }
        /// <summary>A raised-background panel (pinned goal, prompt), padded like the window.</summary>
        public static GUIStyle Raised { get { Build(); return _raised; } }
        /// <summary>A bar-background panel (control bar), padded like the window.</summary>
        public static GUIStyle Bar { get { Build(); return _bar; } }
        /// <summary>The multi-line prompt: input background, wrapping, room on the right for a 32 px send button.</summary>
        public static GUIStyle Prompt { get { Build(); return _prompt; } }
        /// <summary>The player's message bubble: player-message colours, 8 px radius.</summary>
        public static GUIStyle PlayerBubble { get { Build(); return _bubble; } }
        /// <summary>"Claudia" above her lines: 12 px bold in Claudia blue.</summary>
        public static GUIStyle ClaudiaName { get { Build(); return _claudiaName; } }
        /// <summary>A notice line: 12 px muted, centred.</summary>
        public static GUIStyle Notice { get { Build(); return _notice; } }
        /// <summary>A card's header row.</summary>
        public static GUIStyle CardHeaderRow { get { Build(); return _cardHeader; } }
        /// <summary>A card's kind ("MISSION PLAN"): 11 px bold capitals, secondary.</summary>
        public static GUIStyle KindLabel { get { Build(); return _kindLabel; } }
        /// <summary>A window title in accent, 16 px bold.</summary>
        public static GUIStyle TitleAccent { get { Build(); return _titleAccent; } }

        /// <summary>The chip colour for a tone the rules chose.</summary>
        public static ChipKind KindOf(ChipTone tone)
        {
            switch (tone)
            {
                case ChipTone.Success: return ChipKind.Success;
                case ChipTone.Attention: return ChipKind.Attention;
                case ChipTone.Danger: return ChipKind.Danger;
                default: return ChipKind.Info;
            }
        }

        public static GUIStyle ChipStyle(ChipKind kind)
        {
            Build();
            return kind == ChipKind.Success ? _chipSuccess : kind == ChipKind.Attention ? _chipAttention : kind == ChipKind.Danger ? _chipDanger : _chipInfo;
        }

        /// <summary>Secondary-size text in success green ("Key is valid.").</summary>
        public static GUIStyle SuccessText { get { Build(); return _successText; } }
        /// <summary>Secondary-size text in danger red (a value that can't be applied).</summary>
        public static GUIStyle DangerText { get { Build(); return _dangerText; } }

        /// <summary>A Craft Check Warning's box: amber-tinted.</summary>
        public static GUIStyle WarningBox { get { Build(); return _warningBox; } }
        /// <summary>A Craft Check Blocker's box: danger-tinted.</summary>
        public static GUIStyle BlockerBox { get { Build(); return _blockerBox; } }
        /// <summary>The bordered box Claudia asks the player to confirm in (an Approval).</summary>
        public static GUIStyle AskBox { get { Build(); return _askBox; } }
        /// <summary>"WARNING" over a finding: 11 px bold capitals in amber.</summary>
        public static GUIStyle WarningLabel { get { Build(); return _warningLabel; } }
        /// <summary>"BLOCKER" over a finding: 11 px bold capitals in danger red.</summary>
        public static GUIStyle BlockerLabel { get { Build(); return _blockerLabel; } }

        // ---- Controls ----

        /// <summary>A section label: 11 px bold capitals in accent.</summary>
        public static void SectionLabel(string text, params GUILayoutOption[] options)
        {
            Build();
            GUILayout.Label(text.ToUpperInvariant(), _section, options);
        }

        /// <summary>A primary button: accent fill, dark text.</summary>
        public static bool PrimaryButton(string text, params GUILayoutOption[] options)
        {
            Build();
            return GUILayout.Button(text, _primary, WithHeight(options));
        }

        public static bool SecondaryButton(string text, params GUILayoutOption[] options)
        {
            Build();
            return GUILayout.Button(text, _secondaryButton, WithHeight(options));
        }

        /// <summary>A danger-outline button (End Mission, Prepare to uninstall).</summary>
        public static bool DangerButton(string text, params GUILayoutOption[] options)
        {
            Build();
            return GUILayout.Button(text, _danger, WithHeight(options));
        }

        /// <summary>A text-only button in accent ("Show all"); still at least <see cref="MinClickable"/> high.</summary>
        public static bool TextButton(string text, params GUILayoutOption[] options)
        {
            Build();
            return GUILayout.Button(text, _textButton, Prepend(GUILayout.Height(MinClickable), options));
        }

        /// <summary>A secondary button that stays pressed: accent border and fill while <paramref name="on"/>. Returns true when clicked.</summary>
        public static bool ToggleButton(bool on, string text, params GUILayoutOption[] options)
        {
            Build();
            return GUILayout.Button(text, on ? _toggleOn : _secondaryButton, WithHeight(options));
        }

        /// <summary>A small secondary button, 12 px text, <see cref="MinClickable"/> high.</summary>
        public static bool SmallButton(string text, params GUILayoutOption[] options)
        {
            Build();
            return GUILayout.Button(text, _small, Prepend(GUILayout.Height(MinClickable), options));
        }

        /// <summary>A small secondary button with an icon before its text, or only the icon when the text is "".</summary>
        public static bool IconButton(IconShape shape, string text, params GUILayoutOption[] options)
        {
            Build();
            // Spaces leave room for the icon in front of the text.
            var content = new GUIContent(text.Length > 0 ? "      " + text : "");
            var rect = GUILayoutUtility.GetRect(content, _small, Prepend(GUILayout.Height(MinClickable), options));
            var clicked = GUI.Button(rect, content, _small);
            const float size = 14;
            var x = text.Length > 0 ? rect.x + _small.padding.left : rect.center.x - size / 2;
            DrawIcon(new Rect(x, rect.center.y - size / 2, size, size), shape, GUI.enabled ? Text : TextMuted);
            return clicked;
        }

        /// <summary>A button that can't be pressed now: secondary text in a dashed outline. It takes no clicks.</summary>
        public static void DisabledButton(string text, params GUILayoutOption[] options)
        {
            Build();
            var content = new GUIContent(text);
            var rect = GUILayoutUtility.GetRect(content, _disabledButton, WithHeight(options));
            if (Event.current.type != EventType.Repaint) return;
            _disabledButton.Draw(rect, content, false, false, false, false);
            DashedOutline(rect, BorderStrong);
        }

        /// <summary>The prompt's send button (an up arrow) drawn into <paramref name="rect"/>; the caller takes the click.</summary>
        public static void DrawSendButton(Rect rect, bool enabled)
        {
            Build();
            if (Event.current.type != EventType.Repaint) return;
            (enabled ? _sendOn : _sendOff).Draw(rect, GUIContent.none, enabled && rect.Contains(Event.current.mousePosition), false, false, false);
            DrawIcon(new Rect(rect.center.x - 8, rect.center.y - 8, 16, 16), IconShape.ArrowUp, enabled ? OnAccent : TextMuted);
        }

        /// <summary>The spend meter's 4 px bar, <paramref name="width"/> wide: accent, amber from 80% of the Spending Cap.</summary>
        public static void SpendBar(MissionView view, float width)
        {
            var bar = GUILayoutUtility.GetRect(width, 4, GUILayout.Width(width), GUILayout.Height(4));
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(bar, Solid(Border));
            var fill = new Rect(bar.x, bar.y, bar.width * MissionWindowRules.SpendFraction(view), bar.height);
            GUI.DrawTexture(fill, Solid(view.CapWarning ? Attention : Accent));
        }

        /// <summary>A 1 px outline just inside <paramref name="rect"/>; call on Repaint.</summary>
        public static void Outline(Rect rect, Color color)
        {
            var texture = Solid(color);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1), texture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1, rect.width, 1), texture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1, rect.height), texture);
            GUI.DrawTexture(new Rect(rect.xMax - 1, rect.y, 1, rect.height), texture);
        }

        /// <summary>A 1 px divider across the layout in the border colour.</summary>
        public static void Divider()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(rect, Solid(Border));
        }

        /// <summary>A dashed 1 px outline just inside <paramref name="rect"/>; call on Repaint.</summary>
        public static void DashedOutline(Rect rect, Color color)
        {
            const float dash = 5, gap = 4;
            var texture = Solid(color);
            for (var x = rect.x + 3; x < rect.xMax - 3; x += dash + gap)
            {
                var w = Mathf.Min(dash, rect.xMax - 3 - x);
                GUI.DrawTexture(new Rect(x, rect.y, w, 1), texture);
                GUI.DrawTexture(new Rect(x, rect.yMax - 1, w, 1), texture);
            }
            for (var y = rect.y + 3; y < rect.yMax - 3; y += dash + gap)
            {
                var h = Mathf.Min(dash, rect.yMax - 3 - y);
                GUI.DrawTexture(new Rect(rect.x, y, 1, h), texture);
                GUI.DrawTexture(new Rect(rect.xMax - 1, y, 1, h), texture);
            }
        }

        /// <summary>The text cut to one line of <paramref name="width"/>, ending in an ellipsis when cut.</summary>
        public static string Ellipsize(string text, GUIStyle style, float width)
        {
            if (style.CalcSize(new GUIContent(text)).x <= width) return text;
            int low = 0, high = text.Length;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (style.CalcSize(new GUIContent(Cut(text, mid))).x <= width) low = mid;
                else high = mid - 1;
            }
            return Cut(text, low);
        }

        /// <summary>The wrapped text cut to <paramref name="lines"/> lines at <paramref name="width"/>, ending in an ellipsis when cut.</summary>
        public static string ClampLines(string text, GUIStyle style, float width, int lines)
        {
            var most = style.CalcHeight(new GUIContent(Lines(lines)), width);
            if (style.CalcHeight(new GUIContent(text), width) <= most) return text;
            int low = 0, high = text.Length;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (style.CalcHeight(new GUIContent(Cut(text, mid)), width) <= most) low = mid;
                else high = mid - 1;
            }
            return Cut(text, low);
        }

        /// <summary>"A" on <paramref name="count"/> lines, for measuring the height of so many lines.</summary>
        public static string Lines(int count)
        {
            var text = "A";
            for (var i = 1; i < count; i++) text += "\nA";
            return text;
        }

        private static string Cut(string text, int length)
        {
            return text.Substring(0, length).TrimEnd() + "…";
        }

        /// <summary>A segmented control of equal segments; returns the selected index.</summary>
        public static int Segmented(int selected, string[] labels)
        {
            Build();
            GUILayout.BeginHorizontal(_box);
            for (var i = 0; i < labels.Length; i++)
                if (GUILayout.Button(labels[i], i == selected ? _segmentOn : _segment, GUILayout.Height(MinClickable + 4), GUILayout.ExpandWidth(true)))
                    selected = i;
            GUILayout.EndHorizontal();
            return selected;
        }

        /// <summary>A toggle switch with its label on the left; the whole row is the click target.</summary>
        public static bool Switch(bool on, string label)
        {
            Build();
            var row = GUILayoutUtility.GetRect(new GUIContent(label), _body, GUILayout.Height(MinClickable + 4), GUILayout.ExpandWidth(true));
            if (GUI.Button(row, GUIContent.none, GUIStyle.none)) on = !on;
            if (Event.current.type == EventType.Repaint)
            {
                const float w = 40, h = 22;
                var track = new Rect(row.xMax - w, row.y + (row.height - h) / 2, w, h);
                _body.Draw(new Rect(row.x, row.y, row.width - w - Gap, row.height), new GUIContent(label), false, false, false, false);
                (on ? _switchOn : _switchOff).Draw(track, false, false, false, false);
                var knob = new Rect(on ? track.xMax - h + 3 : track.x + 3, track.y + 3, h - 6, h - 6);
                (on ? _knobOn : _knobOff).Draw(knob, false, false, false, false);
            }
            return on;
        }

        /// <summary>
        /// A checkbox with its label to the right; the whole row, at least 32 px high, is the click target. Returns the new
        /// value. A checked box that is not <paramref name="enabled"/> (a done acknowledgement) shows checked and takes no clicks.
        /// </summary>
        public static bool Checkbox(bool on, string label, bool enabled = true)
        {
            Build();
            var row = GUILayoutUtility.GetRect(new GUIContent(label), _body, GUILayout.Height(CheckboxHeight), GUILayout.ExpandWidth(true));
            var clicked = enabled && GUI.Button(row, GUIContent.none, GUIStyle.none);
            if (Event.current.type == EventType.Repaint)
            {
                const float size = 20;
                var box = new Rect(row.x, row.y + (row.height - size) / 2, size, size);
                (on ? (enabled ? _checkOn : _checkDone) : _checkOff).Draw(box, false, false, false, false);
                if (on) DrawIcon(new Rect(box.x + 2, box.y + 2, size - 4, size - 4), IconShape.Check, enabled ? OnAccent : Text);
                _body.Draw(new Rect(box.xMax + Gap, row.y, row.width - size - Gap, row.height), new GUIContent(label), false, false, false, false);
            }
            return clicked ? !on : on;
        }

        /// <summary>Starts a card box; end it with <see cref="EndCard"/>.</summary>
        public static void BeginCard(params GUILayoutOption[] options)
        {
            Build();
            GUILayout.BeginVertical(_card, options);
        }

        public static void EndCard()
        {
            GUILayout.EndVertical();
        }

        /// <summary>A small rounded label, e.g. "Running" (info) or "Nominal" (success).</summary>
        public static void Chip(string text, ChipKind kind)
        {
            Build();
            GUILayout.Label(text, ChipStyle(kind), GUILayout.ExpandWidth(false));
        }

        /// <summary>Draws an icon in a fixed square in the layout.</summary>
        public static void IconLabel(IconShape shape, Color color, float size = 14)
        {
            var rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            DrawIcon(rect, shape, color);
        }

        /// <summary>Draws an icon (white artwork tinted with <paramref name="color"/>) into a rect.</summary>
        public static void DrawIcon(Rect rect, IconShape shape, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Icon(shape), ScaleMode.ScaleToFit);
            GUI.color = old;
        }

        /// <summary>The title strip of a <see cref="Window"/>-styled window: an optional icon and the title. Call first in the draw function.</summary>
        public static void DrawTitleBar(Rect window, string title, IconShape? icon)
        {
            Build();
            var x = (float)Padding;
            if (icon.HasValue)
            {
                DrawIcon(new Rect(x, (TitleBarHeight - 16) / 2f, 16, 16), icon.Value, TextSecondary);
                x += 16 + Gap;
            }
            GUI.Label(new Rect(x, 0, window.width - x - Padding, TitleBarHeight), title, _heading);
        }

        /// <summary>A cached 1×1 texture of one colour.</summary>
        public static Texture2D Solid(Color color)
        {
            var key = "s" + ColorUtility.ToHtmlStringRGBA(color);
            Texture2D texture;
            if (Textures.TryGetValue(key, out texture) && texture != null) return texture;
            texture = new Texture2D(1, 1, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return Textures[key] = texture;
        }

        /// <summary>
        /// A cached rounded box with a 1 px border, made to be nine-sliced with a border of <paramref name="radius"/> + 1
        /// (see <see cref="Box"/>), so it stretches to any size. <paramref name="radius"/> is at least 2.
        /// </summary>
        public static Texture2D Rounded(Color fill, Color border, int radius)
        {
            radius = Math.Max(2, radius);
            var key = "r" + ColorUtility.ToHtmlStringRGBA(fill) + ColorUtility.ToHtmlStringRGBA(border) + radius;
            Texture2D texture;
            if (Textures.TryGetValue(key, out texture) && texture != null) return texture;
            var size = 2 * radius + 3;
            texture = new Texture2D(size, size, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var clear = new Color(fill.r, fill.g, fill.b, 0);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Distance outside the rounded rectangle, sampled at the pixel centre.
                    var cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    var cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy)) - radius;
                    var edge = Mathf.Clamp01(d + 1.5f);
                    var inside = Mathf.Clamp01(-d + 0.5f);
                    var color = Color.Lerp(fill, border, edge);
                    texture.SetPixel(x, y, inside > 0 ? new Color(color.r, color.g, color.b, color.a * inside) : clear);
                }
            }
            texture.Apply();
            return Textures[key] = texture;
        }

        /// <summary>A new box style with a rounded background, for rows and panels the controls above don't cover; build it once and keep it.</summary>
        public static GUIStyle Box(Color fill, Color border, int radius = 4)
        {
            Build();
            radius = Math.Max(2, radius);
            var style = new GUIStyle(_card);
            style.normal.background = Rounded(fill, border, radius);
            style.border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1);
            return style;
        }

        // ---- Building ----

        private static GUILayoutOption[] WithHeight(GUILayoutOption[] options)
        {
            return Prepend(GUILayout.Height(ButtonHeight), options);
        }

        private static GUILayoutOption[] Prepend(GUILayoutOption first, GUILayoutOption[] options)
        {
            var all = new GUILayoutOption[options.Length + 1];
            all[0] = first;
            Array.Copy(options, 0, all, 1, options.Length);
            return all;
        }

        private static void Build()
        {
            var skin = HighLogic.Skin;
            if (_window != null && ReferenceEquals(skin, _builtFrom)) return;
            _builtFrom = skin;

            _window = new GUIStyle(skin.window) { richText = false, padding = new RectOffset(Padding, Padding, TitleBarHeight + Padding, Padding), border = new RectOffset(2, 2, TitleBarHeight + 1, 2) };
            var windowTexture = WindowTexture();
            SetBackground(_window, windowTexture);
            // The title is drawn by DrawTitleBar, beside its icon.
            _window.normal.textColor = _window.onNormal.textColor = Color.clear;

            _body = TextStyle(skin, Scaled(14), Text, FontStyle.Normal);
            _secondary = TextStyle(skin, Scaled(12), TextSecondary, FontStyle.Normal);
            _muted = TextStyle(skin, Scaled(12), TextMuted, FontStyle.Normal);
            _section = TextStyle(skin, Scaled(11), Accent, FontStyle.Bold);
            _section.wordWrap = false;
            _heading = TextStyle(skin, Scaled(16), Text, FontStyle.Bold);
            _heading.alignment = TextAnchor.MiddleLeft;
            _heading.wordWrap = false;
            _mono14 = TextStyle(skin, Scaled(14), Text, FontStyle.Normal);
            _mono14.wordWrap = false;
            var mono = MonoFont();
            if (mono != null) _mono14.font = mono;

            _textField = new GUIStyle(skin.textField) { richText = false, fontSize = Scaled(14), padding = new RectOffset(8, 8, 8, 8), border = new RectOffset(4, 4, 4, 4) };
            _textField.normal.background = _textField.hover.background = Rounded(InputBackground, BorderStrong, 3);
            _textField.focused.background = _textField.onFocused.background = Rounded(InputBackground, Accent, 3);
            _textField.normal.textColor = _textField.hover.textColor = _textField.focused.textColor = _textField.onFocused.textColor = Text;

            _primary = Button(skin, Accent, Accent, OnAccent, Hex(0xc8e65e));
            _primary.fontStyle = FontStyle.Bold;
            _secondaryButton = Button(skin, RaisedBackground, BorderStrong, Text, CardHeader);
            _danger = Button(skin, WindowBackground, DangerBorder, Danger, Hex(0x2a2020));

            _box = new GUIStyle { padding = new RectOffset(2, 2, 2, 2), border = new RectOffset(5, 5, 5, 5) };
            _box.normal.background = Rounded(InputBackground, BorderStrong, 4);
            _segment = Button(skin, InputBackground, InputBackground, TextSecondary, RaisedBackground);
            _segment.margin = new RectOffset(0, 0, 0, 0);
            _segmentOn = Button(skin, Accent, Accent, OnAccent, Accent);
            _segmentOn.margin = new RectOffset(0, 0, 0, 0);
            _segmentOn.fontStyle = FontStyle.Bold;

            _card = new GUIStyle { richText = false, padding = new RectOffset(Padding, Padding, 10, 10), margin = new RectOffset(0, 0, 4, 4), border = new RectOffset(5, 5, 5, 5) };
            _card.normal.background = Rounded(CardBackground, Border, 4);

            _chipInfo = Chip(skin, InfoFill, InfoFill, InfoText);
            _chipSuccess = Chip(skin, WindowBackground, SuccessBorder, Success);
            _chipAttention = Chip(skin, AttentionFillStrong, AttentionBorder, Attention);
            _chipDanger = Chip(skin, WindowBackground, DangerBorder, Danger);

            _successText = TextStyle(skin, Scaled(12), Success, FontStyle.Normal);
            _dangerText = TextStyle(skin, Scaled(12), Danger, FontStyle.Normal);
            _switchOn = Box(Accent, Accent, 10);
            _switchOff = Box(InputBackground, BorderStrong, 10);
            _knobOn = Box(OnAccent, OnAccent, 7);
            _knobOff = Box(TextSecondary, TextSecondary, 7);

            _bareWindow = new GUIStyle(_window) { padding = new RectOffset(0, 0, TitleBarHeight, 0) };
            _raised = new GUIStyle { padding = new RectOffset(Padding, Padding, 10, 10) };
            _raised.normal.background = Solid(RaisedBackground);
            _bar = new GUIStyle { padding = new RectOffset(Padding, Padding, 6, 6) };
            _bar.normal.background = Solid(BarBackground);

            _prompt = new GUIStyle(skin.textArea) { richText = false, wordWrap = true, fontSize = Scaled(14), padding = new RectOffset(10, Scaled(44), 10, 10), border = new RectOffset(4, 4, 4, 4), margin = new RectOffset(0, 0, 2, 2) };
            _prompt.normal.background = _prompt.hover.background = Rounded(InputBackground, BorderStrong, 3);
            _prompt.focused.background = _prompt.onFocused.background = Rounded(InputBackground, Accent, 3);
            _prompt.normal.textColor = _prompt.hover.textColor = _prompt.focused.textColor = _prompt.onFocused.textColor = Text;

            _bubble = TextStyle(skin, Scaled(14), Text, FontStyle.Normal);
            _bubble.padding = new RectOffset(10, 10, 7, 7);
            _bubble.margin = new RectOffset(0, 0, 4, 4);
            _bubble.border = new RectOffset(9, 9, 9, 9);
            _bubble.normal.background = _bubble.hover.background = _bubble.active.background = Rounded(PlayerFill, PlayerBorder, 8);
            _claudiaName = TextStyle(skin, Scaled(12), Claudia, FontStyle.Bold);
            _claudiaName.margin = new RectOffset(0, 0, 6, 0);
            _notice = TextStyle(skin, Scaled(12), TextMuted, FontStyle.Normal);
            _notice.alignment = TextAnchor.MiddleCenter;
            _notice.margin = new RectOffset(0, 0, 4, 4);
            _cardHeader = new GUIStyle { padding = new RectOffset(8, 8, 4, 4), margin = new RectOffset(0, 0, 0, 6), border = new RectOffset(4, 4, 4, 4) };
            _cardHeader.normal.background = Rounded(CardHeader, CardHeader, 3);
            _kindLabel = TextStyle(skin, Scaled(11), TextSecondary, FontStyle.Bold);
            _kindLabel.wordWrap = false;
            _kindLabel.alignment = TextAnchor.MiddleLeft;
            _titleAccent = new GUIStyle(_heading);
            _titleAccent.normal.textColor = Accent;

            _textButton = TextStyle(skin, Scaled(12), Accent, FontStyle.Bold);
            _textButton.wordWrap = false;
            _textButton.alignment = TextAnchor.MiddleRight;
            _textButton.hover.textColor = Hex(0xc8e65e);
            _toggleOn = Button(skin, PlayerFill, Accent, Accent, PlayerFill);
            _toggleOn.fontStyle = FontStyle.Bold;
            _small = Button(skin, RaisedBackground, BorderStrong, Text, CardHeader);
            _small.fontSize = Scaled(12);
            _small.padding = new RectOffset(8, 8, 4, 4);
            _disabledButton = TextStyle(skin, Scaled(14), TextSecondary, FontStyle.Normal);
            _disabledButton.alignment = TextAnchor.MiddleCenter;
            _disabledButton.wordWrap = false;
            _disabledButton.padding = new RectOffset(10, 10, 6, 6);
            _disabledButton.margin = new RectOffset(4, 4, 4, 4);
            _sendOn = Box(Accent, Accent, 4);
            _sendOn.hover.background = Rounded(Hex(0xc8e65e), Accent, 4);
            _sendOff = Box(RaisedBackground, BorderStrong, 4);

            _checkOn = Box(Accent, Accent, 4);
            _checkOff = Box(InputBackground, BorderStrong, 4);
            _checkDone = Box(SuccessBorder, SuccessBorder, 4);
            _warningBox = Box(AttentionFill, AttentionBorder, 4);
            _blockerBox = Box(Hex(0x2a2020), DangerBorder, 4);
            _askBox = Box(CardBackground, BorderStrong, 4);
            _warningLabel = TextStyle(skin, Scaled(11), Attention, FontStyle.Bold);
            _blockerLabel = TextStyle(skin, Scaled(11), Danger, FontStyle.Bold);
        }

        private static GUIStyle TextStyle(GUISkin skin, int size, Color color, FontStyle fontStyle)
        {
            var style = new GUIStyle(skin.label) { richText = false, wordWrap = true, fontSize = size, fontStyle = fontStyle, padding = new RectOffset(0, 0, 2, 2), margin = new RectOffset(0, 0, 2, 2) };
            style.normal.textColor = style.hover.textColor = style.active.textColor = color;
            style.normal.background = style.hover.background = style.active.background = null;
            return style;
        }

        private static GUIStyle Button(GUISkin skin, Color fill, Color border, Color text, Color hover)
        {
            var style = new GUIStyle(skin.button) { richText = false, fontSize = Scaled(14), alignment = TextAnchor.MiddleCenter, wordWrap = false, border = new RectOffset(5, 5, 5, 5), padding = new RectOffset(10, 10, 6, 6) };
            style.normal.background = style.onNormal.background = Rounded(fill, border, 4);
            style.hover.background = style.onHover.background = Rounded(hover, border, 4);
            style.active.background = style.onActive.background = Rounded(hover, border, 4);
            style.focused.background = style.onFocused.background = style.normal.background;
            style.normal.textColor = style.onNormal.textColor = style.hover.textColor = style.onHover.textColor = text;
            style.active.textColor = style.onActive.textColor = style.focused.textColor = style.onFocused.textColor = text;
            return style;
        }

        private static GUIStyle Chip(GUISkin skin, Color fill, Color border, Color text)
        {
            var style = new GUIStyle(skin.label) { richText = false, fontSize = Scaled(12), fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(8, 8, 3, 3), border = new RectOffset(5, 5, 5, 5) };
            style.normal.background = Rounded(fill, border, 4);
            style.normal.textColor = text;
            return style;
        }

        private static void SetBackground(GUIStyle style, Texture2D texture)
        {
            style.normal.background = style.onNormal.background = style.hover.background = style.onHover.background = texture;
            style.active.background = style.onActive.background = style.focused.background = style.onFocused.background = texture;
        }

        // Nine-sliced: the top slice is the title bar with a divider under it, the middle stretches as the body.
        private static Texture2D WindowTexture()
        {
            const int width = 6;
            var height = TitleBarHeight + 4;
            var texture = new Texture2D(width, height, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (var y = 0; y < height; y++)
            {
                // Texture rows count up from the bottom.
                var fromTop = height - 1 - y;
                for (var x = 0; x < width; x++)
                {
                    Color color;
                    if (x == 0 || x == width - 1 || fromTop == 0 || y == 0) color = BorderStrong;
                    else if (fromTop <= TitleBarHeight - 1) color = BarBackground;
                    else if (fromTop == TitleBarHeight) color = Border;
                    else color = WindowBackground;
                    texture.SetPixel(x, y, color);
                }
            }
            texture.Apply();
            return texture;
        }

        private static Font MonoFont()
        {
            if (_mono != null) return _mono;
            try
            {
                _mono = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Liberation Mono", "Courier New" }, Scaled(14));
            }
            catch (Exception e)
            {
                Debug.Log("[ClaudiaKermin] No monospace font: " + e.Message);
            }
            return _mono;
        }

        private static Texture2D Icon(IconShape shape)
        {
            Texture2D texture;
            if (Icons.TryGetValue(shape, out texture) && texture != null) return texture;
            const int size = 32;
            texture = new Texture2D(size, size, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // 4×4 samples per pixel; u and v run 0..1 from the top left.
                    var hits = 0;
                    for (var sy = 0; sy < 4; sy++)
                        for (var sx = 0; sx < 4; sx++)
                            if (Inside(shape, (x + (sx + 0.5f) / 4f) / size, 1f - (y + (sy + 0.5f) / 4f) / size)) hits++;
                    texture.SetPixel(x, y, new Color(1, 1, 1, hits / 16f));
                }
            }
            texture.Apply();
            return Icons[shape] = texture;
        }

        private static bool Inside(IconShape shape, float u, float v)
        {
            var p = new Vector2(u, v);
            switch (shape)
            {
                case IconShape.Gear:
                {
                    var d = p - new Vector2(0.5f, 0.5f);
                    var r = d.magnitude;
                    var angle = Mathf.Atan2(d.y, d.x);
                    var outer = Mathf.Cos(angle * 8) > 0.2f ? 0.48f : 0.36f;
                    return r <= outer && r >= 0.15f;
                }
                case IconShape.Lock:
                {
                    var body = u >= 0.18f && u <= 0.82f && v >= 0.45f && v <= 0.92f;
                    var shackle = Mathf.Abs(Vector2.Distance(p, new Vector2(0.5f, 0.42f)) - 0.2f) <= 0.07f && v <= 0.45f;
                    return body || shackle;
                }
                case IconShape.Close:
                    return Segment(p, new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.75f)) <= 0.07f || Segment(p, new Vector2(0.75f, 0.25f), new Vector2(0.25f, 0.75f)) <= 0.07f;
                case IconShape.Pencil:
                    return Segment(p, new Vector2(0.3f, 0.7f), new Vector2(0.8f, 0.2f)) <= 0.1f || Segment(p, new Vector2(0.15f, 0.85f), new Vector2(0.24f, 0.76f)) <= 0.05f;
                case IconShape.ArrowUp:
                    return Segment(p, new Vector2(0.5f, 0.82f), new Vector2(0.5f, 0.2f)) <= 0.08f
                        || Segment(p, new Vector2(0.24f, 0.46f), new Vector2(0.5f, 0.2f)) <= 0.08f || Segment(p, new Vector2(0.5f, 0.2f), new Vector2(0.76f, 0.46f)) <= 0.08f;
                case IconShape.Warning:
                {
                    // A triangle (apex at the top) with the exclamation mark cut out of it.
                    if (v < 0.1f || v > 0.88f || Mathf.Abs(u - 0.5f) > (v - 0.1f) / 0.78f * 0.46f) return false;
                    var stem = Mathf.Abs(u - 0.5f) <= 0.05f && v >= 0.38f && v <= 0.62f;
                    return !stem && Vector2.Distance(p, new Vector2(0.5f, 0.74f)) > 0.065f;
                }
                case IconShape.Dot:
                    return Vector2.Distance(p, new Vector2(0.5f, 0.5f)) <= 0.45f;
                case IconShape.ExternalLink:
                {
                    // The box is open at its top right, where the arrow leaves.
                    var inBox = u >= 0.15f && u <= 0.75f && v >= 0.25f && v <= 0.85f;
                    var edge = u <= 0.22f || u >= 0.68f || v <= 0.32f || v >= 0.78f;
                    var box = inBox && edge && !(u > 0.42f && v < 0.58f);
                    var arrow = Segment(p, new Vector2(0.45f, 0.55f), new Vector2(0.85f, 0.15f)) <= 0.07f
                        || Segment(p, new Vector2(0.55f, 0.15f), new Vector2(0.85f, 0.15f)) <= 0.07f
                        || Segment(p, new Vector2(0.85f, 0.15f), new Vector2(0.85f, 0.45f)) <= 0.07f;
                    return box || arrow;
                }
                case IconShape.Dock:
                    return (v <= 0.22f && v >= 0.12f && u >= 0.12f && u <= 0.88f)
                        || Segment(p, new Vector2(0.5f, 0.88f), new Vector2(0.5f, 0.36f)) <= 0.07f
                        || Segment(p, new Vector2(0.28f, 0.58f), new Vector2(0.5f, 0.36f)) <= 0.07f
                        || Segment(p, new Vector2(0.5f, 0.36f), new Vector2(0.72f, 0.58f)) <= 0.07f;
                case IconShape.ChevronDown:
                    return Segment(p, new Vector2(0.2f, 0.35f), new Vector2(0.5f, 0.65f)) <= 0.08f || Segment(p, new Vector2(0.5f, 0.65f), new Vector2(0.8f, 0.35f)) <= 0.08f;
                case IconShape.ChevronUp:
                    return Segment(p, new Vector2(0.2f, 0.65f), new Vector2(0.5f, 0.35f)) <= 0.08f || Segment(p, new Vector2(0.5f, 0.35f), new Vector2(0.8f, 0.65f)) <= 0.08f;
                case IconShape.ChevronLeft:
                    return Segment(p, new Vector2(0.65f, 0.2f), new Vector2(0.35f, 0.5f)) <= 0.08f || Segment(p, new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.8f)) <= 0.08f;
                case IconShape.ChevronRight:
                    return Segment(p, new Vector2(0.35f, 0.2f), new Vector2(0.65f, 0.5f)) <= 0.08f || Segment(p, new Vector2(0.65f, 0.5f), new Vector2(0.35f, 0.8f)) <= 0.08f;
                default:
                    return Segment(p, new Vector2(0.15f, 0.52f), new Vector2(0.4f, 0.78f)) <= 0.08f || Segment(p, new Vector2(0.4f, 0.78f), new Vector2(0.86f, 0.25f)) <= 0.08f;
            }
        }

        private static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + t * ab);
        }

        private static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
        }
    }
}
