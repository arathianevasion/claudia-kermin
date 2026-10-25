// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
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

        public const int Padding = 12;
        public const float Gap = 8;
        public const float ButtonHeight = 36;
        /// <summary>Nothing clickable is smaller than this.</summary>
        public const float MinClickable = 28;
        /// <summary>The height of the title bar <see cref="Window"/> leaves for <see cref="DrawTitleBar"/>.</summary>
        public const int TitleBarHeight = 36;

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
            Check,
        }

        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<IconShape, Texture2D> Icons = new Dictionary<IconShape, Texture2D>();
        private static GUISkin _builtFrom;
        private static Font _mono;
        private static GUIStyle _window, _body, _secondary, _muted, _section, _heading, _mono14, _textField;
        private static GUIStyle _primary, _secondaryButton, _danger, _segment, _segmentOn, _card, _box;
        private static GUIStyle _chipInfo, _chipSuccess, _chipAttention, _chipDanger;
        private static GUIStyle _successText, _dangerText, _switchOn, _switchOff, _knobOn, _knobOff;

        // ---- Styles ----

        /// <summary>The window: body background with a bar-coloured title strip; draw the title with <see cref="DrawTitleBar"/>.</summary>
        public static GUIStyle Window { get { Build(); return _window; } }
        /// <summary>Body text, 14 px, wrapping.</summary>
        public static GUIStyle Body { get { Build(); return _body; } }
        /// <summary>Secondary text, 12 px, wrapping.</summary>
        public static GUIStyle Secondary { get { Build(); return _secondary; } }
        /// <summary>Muted text, 12 px, wrapping.</summary>
        public static GUIStyle Muted { get { Build(); return _muted; } }
        /// <summary>16 px bold, for names and headers.</summary>
        public static GUIStyle Heading { get { Build(); return _heading; } }
        /// <summary>Monospace body text (an OS font; the skin's font when none is found).</summary>
        public static GUIStyle Mono { get { Build(); return _mono14; } }
        public static GUIStyle TextField { get { Build(); return _textField; } }
        /// <summary>The card box (plans, Debriefs, Craft Check, settings groups).</summary>
        public static GUIStyle Card { get { Build(); return _card; } }

        /// <summary>Secondary-size text in success green ("Key is valid.").</summary>
        public static GUIStyle SuccessText { get { Build(); return _successText; } }
        /// <summary>Secondary-size text in danger red (a value that can't be applied).</summary>
        public static GUIStyle DangerText { get { Build(); return _dangerText; } }

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
            var style = kind == ChipKind.Success ? _chipSuccess : kind == ChipKind.Attention ? _chipAttention : kind == ChipKind.Danger ? _chipDanger : _chipInfo;
            GUILayout.Label(text, style, GUILayout.ExpandWidth(false));
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
            var all = new GUILayoutOption[options.Length + 1];
            all[0] = GUILayout.Height(ButtonHeight);
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

            _body = TextStyle(skin, 14, Text, FontStyle.Normal);
            _secondary = TextStyle(skin, 12, TextSecondary, FontStyle.Normal);
            _muted = TextStyle(skin, 12, TextMuted, FontStyle.Normal);
            _section = TextStyle(skin, 11, Accent, FontStyle.Bold);
            _section.wordWrap = false;
            _heading = TextStyle(skin, 16, Text, FontStyle.Bold);
            _heading.alignment = TextAnchor.MiddleLeft;
            _heading.wordWrap = false;
            _mono14 = TextStyle(skin, 14, Text, FontStyle.Normal);
            _mono14.wordWrap = false;
            var mono = MonoFont();
            if (mono != null) _mono14.font = mono;

            _textField = new GUIStyle(skin.textField) { richText = false, fontSize = 14, padding = new RectOffset(8, 8, 8, 8), border = new RectOffset(4, 4, 4, 4) };
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

            _successText = TextStyle(skin, 12, Success, FontStyle.Normal);
            _dangerText = TextStyle(skin, 12, Danger, FontStyle.Normal);
            _switchOn = Box(Accent, Accent, 10);
            _switchOff = Box(InputBackground, BorderStrong, 10);
            _knobOn = Box(OnAccent, OnAccent, 7);
            _knobOff = Box(TextSecondary, TextSecondary, 7);
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
            var style = new GUIStyle(skin.button) { richText = false, fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = false, border = new RectOffset(5, 5, 5, 5), padding = new RectOffset(10, 10, 6, 6) };
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
            var style = new GUIStyle(skin.label) { richText = false, fontSize = 12, fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(8, 8, 3, 3), border = new RectOffset(5, 5, 5, 5) };
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
                _mono = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Liberation Mono", "Courier New" }, 14);
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
                case IconShape.ChevronDown:
                    return Segment(p, new Vector2(0.2f, 0.35f), new Vector2(0.5f, 0.65f)) <= 0.08f || Segment(p, new Vector2(0.5f, 0.65f), new Vector2(0.8f, 0.35f)) <= 0.08f;
                case IconShape.ChevronUp:
                    return Segment(p, new Vector2(0.2f, 0.65f), new Vector2(0.5f, 0.35f)) <= 0.08f || Segment(p, new Vector2(0.5f, 0.35f), new Vector2(0.8f, 0.65f)) <= 0.08f;
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
