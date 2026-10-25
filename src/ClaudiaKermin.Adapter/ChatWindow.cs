// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Chat;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The basic chat window: draws the <see cref="ChatSession"/> lines and forwards Send, Cancel and
    /// Retry. With <c>debug_tools</c> on it also offers dumping raw stream events to disk.
    /// </summary>
    public class ChatWindow
    {
        private const string InputControl = "ClaudiaChatInput";
        private const string LockId = "ClaudiaKerminChat";

        private readonly ChatSession _chat;
        private readonly IRawStreamDump _dump;
        private readonly string _dumpFolder;
        private readonly WindowFrame _frame =
            new WindowFrame(0x434B02, "Claudia", LockId, new Rect(240, 140, 520, 480), new Vector2(320, 260), true);
        private Vector2 _scroll;
        private string _typed = "";
        private int _seenVersion = -1;
        private bool _dumping;
        private GUIStyle _lineStyle;
        private readonly BannerDrawer _banner = new BannerDrawer();

        public bool Visible;

        /// <summary>Set while the stock UI is hidden (F2).</summary>
        public bool UiHidden;

        /// <param name="dumpFolder">Where raw stream events go; null unless debug_tools is on.</param>
        public ChatWindow(ChatSession chat, string dumpFolder)
        {
            _chat = chat;
            _dumpFolder = dumpFolder;
            _frame.Remember("chat");
            if (dumpFolder != null) _dump = new FileStreamDump(dumpFolder);
        }

        public void OnGUI()
        {
            if (!Visible || UiHidden)
            {
                Unlock();
                return;
            }
            GUI.skin = HighLogic.Skin;
            _frame.Show(Draw);

            // Keys typed into the chat must not stage, throttle or rotate the craft.
            _frame.SetTyping(GUI.GetNameOfFocusedControl() == InputControl);
        }

        /// <summary>Releases the input locks; call when the window goes away.</summary>
        public void Unlock()
        {
            _frame.Unlock();
        }

        private void Draw(int id)
        {
            if (_chat.Version != _seenVersion)
            {
                _seenVersion = _chat.Version;
                _scroll.y = float.MaxValue;
            }

            if (_lineStyle == null) _lineStyle = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = true };
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            // An empty session, fresh or cleared, starts with the banner.
            if (_chat.Lines.Count == 0) _banner.DrawChat(_frame.Rect.width - 2 * ClaudiaSkin.Padding, _chat.Model);
            foreach (var line in _chat.Lines)
                GUILayout.Label(Format(line), _lineStyle);
            GUILayout.EndScrollView();

            var enter = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == InputControl;

            GUI.SetNextControlName(InputControl);
            _typed = GUILayout.TextField(_typed, 2000);

            GUILayout.BeginHorizontal();
            GUI.enabled = (!_chat.Busy && _typed.Trim().Length > 0) || ClearRules.IsCommand(_typed);
            if (GUILayout.Button("Send") || (enter && GUI.enabled))
            {
                // /clear starts a new conversation and is never sent to Claude; other /... text is sent as normal.
                if (ClearRules.IsCommand(_typed))
                {
                    _chat.Clear();
                    _typed = "";
                }
                else if (_chat.Send(_typed)) _typed = "";
                if (enter) Event.current.Use();
            }
            GUI.enabled = _chat.Busy;
            if (GUILayout.Button("Cancel")) _chat.Cancel();
            GUI.enabled = _chat.CanRetry;
            if (GUILayout.Button("Retry")) _chat.Retry();
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Clear", "Start a new conversation. Claudia forgets this chat."))) _chat.Clear();
            GUILayout.EndHorizontal();

            if (_dump != null)
            {
                var dumping = GUILayout.Toggle(_dumping, "Dump raw stream events (debug)");
                if (dumping != _dumping)
                {
                    _dumping = dumping;
                    _chat.RawDump = dumping ? _dump : null;
                }
                if (_dumping) GUILayout.Label("To " + _dumpFolder);
            }

            _frame.DrawResizeZones();
            WindowFrame.DrawTooltip();
            GUI.DragWindow();
        }

        // Plain text only: Claude's replies may contain '<', which rich text would misread.
        private static string Format(ChatLine line)
        {
            switch (line.Speaker)
            {
                case ChatSpeaker.Player:
                    return "You: " + line.Text;
                case ChatSpeaker.Notice:
                    return "[" + line.Text + "]";
                default:
                    if (line.Thinking) return "Claudia: (thinking...)";
                    if (line.Streaming && line.Text.Length == 0) return "Claudia: ...";
                    return "Claudia: " + line.Text + (line.Streaming ? " ..." : "");
            }
        }
    }
}
