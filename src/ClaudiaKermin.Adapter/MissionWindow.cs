// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The floating Mission window: the title bar with the status chip over the <see cref="MissionPanel"/>. It is the
    /// Mission window in the VAB/SPH, and in Flight once the player has undocked the panel from the Capcom strip.
    /// </summary>
    public class MissionWindow
    {
        private const string LockId = "ClaudiaKerminMission";

        internal static Rect DefaultRect(WindowBox box)
        {
            return new Rect(box.X, box.Y, box.Width, box.Height);
        }

        private readonly MissionPanel _panel;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B03, "", LockId, DefaultRect(DefaultSizes.Mission(ClaudiaSkin.FontScale, 260, 80)),new Vector2(430, 460), true);

        public bool Visible;
        public bool UiHidden;

        /// <summary>Set before each OnGUI.</summary>
        public MissionView View;

        public MissionWindow(MissionPanel panel)
        {
            _panel = panel;
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
            _frame.SetTyping(MissionPanel.Typing);
        }

        /// <summary>Releases the input locks; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private void Draw(int id)
        {
            DrawTitleBar();
            _panel.Draw(View, _frame.Rect.width, false, ClaudiaSkin.Padding);
            _frame.DrawResizeZones();
            WindowFrame.DrawTooltip();
            GUI.DragWindow();
        }

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
    }
}
