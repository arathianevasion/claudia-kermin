// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// What every Claudia Kermin window shares: the IMGUI window itself, optional resizing from any edge or
    /// corner (<see cref="WindowResize"/>), and the KSP input locks. While the pointer is over the window (or a resize is under way) the
    /// mouse is locked away from the game, so the wheel doesn't zoom the camera and clicks don't pick
    /// parts; while a text field has focus the keyboard is locked too. Every lock is released by
    /// <see cref="Unlock"/>, which owners call whenever the window can't draw (closed, F2, scene change).
    /// </summary>
    public class WindowFrame
    {
        // Mouse: no camera moves (flight, map, editor), no part picking or editor actions under the window.
        private const ControlTypes MouseTypes = ControlTypes.CAMERACONTROLS | ControlTypes.ACTIONS_EXTERNAL
            | ControlTypes.EDITOR_ICON_PICK | ControlTypes.EDITOR_PAD_PICK_PLACE | ControlTypes.EDITOR_PAD_PICK_COPY
            | ControlTypes.EDITOR_ICON_HOVER | ControlTypes.EDITOR_GIZMO_TOOLS | ControlTypes.EDITOR_SOFT_LOCK;

        // Hit zones, in design pixels (scaled with the skin): a band along each edge and a bigger square at each corner.
        private const float EdgeZone = 6f;
        private const float CornerZone = 16f;
        private const float HintThickness = 2f;

        private readonly int _windowId;
        private readonly string _title;
        private readonly string _keyboardLockId;
        private readonly string _mouseLockId;
        private readonly Vector2 _min;
        private readonly bool _resizable;
        private bool _keyboardLocked;
        private bool _mouseLocked;
        private bool _resizing;
        private ResizeZone _zone;
        private WindowBox _resizeFrom;
        private Vector2 _resizeFromMouse;

        private string _memoryName;
        private string _appliedScene;
        private Rect _default;
        private WindowBox _lastStored;

        public Rect Rect;

        /// <summary>The window's style (e.g. <see cref="ClaudiaSkin.Window"/>); unset uses the current skin's window.</summary>
        public GUIStyle Style;

        /// <param name="title">The stock window title. Empty for windows in <see cref="ClaudiaSkin.Window"/>, which draw their own title bar, so the title shows once.</param>
        /// <param name="lockId">Unique per window; the keyboard and mouse locks get their own suffixes.</param>
        /// <param name="min">Smallest size a resize allows: of the window, or of the area given to <see cref="HandleResize"/>.</param>
        public WindowFrame(int windowId, string title, string lockId, Rect initial, Vector2 min, bool resizable)
        {
            _windowId = windowId;
            _title = title;
            _keyboardLockId = lockId;
            _mouseLockId = lockId + "Mouse";
            Rect = initial;
            _min = min;
            _resizable = resizable;
            _default = initial;
        }

        /// <summary>Position and size are remembered per scene under this name (see <see cref="WindowMemory"/>).</summary>
        public void Remember(string name)
        {
            _memoryName = name;
        }

        /// <summary>Draws the window at its current size and updates the mouse lock. <paramref name="options"/> only apply to windows that aren't resizable.</summary>
        public void Show(GUI.WindowFunction draw, params GUILayoutOption[] options)
        {
            ApplyRemembered();
            Rect resized;
            if (_resizable && TryResize(out resized)) Rect = resized;
            var style = Style ?? GUI.skin.window;
            Rect = _resizable
                ? GUILayout.Window(_windowId, Rect, draw, _title, style, GUILayout.Width(Rect.width), GUILayout.Height(Rect.height))
                : GUILayout.Window(_windowId, Rect, draw, _title, style, options);
            Rect = KeepOnScreen(Rect);
            StoreWhenSettled();
            // A held button keeps the lock, so a fast window drag can't outrun the pointer and drop it.
            SetMouseLock(_resizing || Rect.Contains(GuiMouse()) || (_mouseLocked && Input.GetMouseButton(0)));
        }

        /// <summary>
        /// Call at the end of the draw function, before <c>GUI.DragWindow</c>: the whole window can be resized from any edge
        /// or corner. A press in a zone starts the resize and is used up, so it neither drags the window nor reaches the game.
        /// </summary>
        public void DrawResizeZones()
        {
            if (!_resizable) return;
            var all = new Rect(0, 0, Rect.width, Rect.height);
            HandleResize(all, ResizeZone.All);
            DrawResizeHint(all, ResizeZone.All);
        }

        /// <summary>
        /// A press in one of <paramref name="area"/>'s zones (in window coordinates; only <paramref name="allowed"/> edges)
        /// starts a resize of that area, and is used up. Call before the controls inside, so they can't take the press first.
        /// The owner reads the result with <see cref="TryResize"/>.
        /// </summary>
        public void HandleResize(Rect area, ResizeZone allowed)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || _resizing) return;
            var zone = ZoneAt(area, allowed, e.mousePosition);
            if (zone == ResizeZone.None) return;
            _resizing = true;
            _zone = zone;
            _resizeFrom = new WindowBox(Rect.x + area.x, Rect.y + area.y, area.width, area.height);
            _resizeFromMouse = GuiMouse();
            e.Use();
        }

        /// <summary>The hover hint: IMGUI can't change the OS cursor, so the edge or corner under the pointer lights up.</summary>
        public void DrawResizeHint(Rect area, ResizeZone allowed)
        {
            if (Event.current.type != EventType.Repaint) return;
            var zone = _resizing ? _zone : ZoneAt(area, allowed, Event.current.mousePosition);
            if (zone == ResizeZone.None) return;
            var thick = Mathf.Max(1f, ClaudiaSkin.Scaled(HintThickness));
            var corner = zone != ResizeZone.Left && zone != ResizeZone.Right && zone != ResizeZone.Top && zone != ResizeZone.Bottom;
            var length = ClaudiaSkin.Scaled(CornerZone);
            var alongX = corner ? length : area.width;
            var alongY = corner ? length : area.height;
            var fromBottom = (zone & ResizeZone.Bottom) != 0 ? area.height - alongY : 0;
            var fromRight = (zone & ResizeZone.Right) != 0 ? area.width - alongX : 0;
            var texture = ClaudiaSkin.Solid(ClaudiaSkin.Accent);
            if ((zone & ResizeZone.Left) != 0) GUI.DrawTexture(new Rect(area.x, area.y + fromBottom, thick, alongY), texture);
            if ((zone & ResizeZone.Right) != 0) GUI.DrawTexture(new Rect(area.xMax - thick, area.y + fromBottom, thick, alongY), texture);
            if ((zone & ResizeZone.Top) != 0) GUI.DrawTexture(new Rect(area.x + fromRight, area.y, alongX, thick), texture);
            if ((zone & ResizeZone.Bottom) != 0) GUI.DrawTexture(new Rect(area.x + fromRight, area.yMax - thick, alongX, thick), texture);
        }

        /// <summary>
        /// While a resize is under way, true with the dragged area's new rect in screen coordinates (within the minimum
        /// size and the screen); a resizable window applies it itself. Letting go of the button ends the resize.
        /// </summary>
        public bool TryResize(out Rect area)
        {
            area = default(Rect);
            if (!_resizing) return false;
            if (!Input.GetMouseButton(0))
            {
                _resizing = false;
                return false;
            }
            var delta = GuiMouse() - _resizeFromMouse;
            var box = WindowResize.Resize(_resizeFrom, _zone, delta.x, delta.y, _min.x, _min.y, Screen.width, Screen.height);
            area = new Rect(box.X, box.Y, box.Width, box.Height);
            return true;
        }

        /// <summary>A resize is under way.</summary>
        public bool Resizing
        {
            get { return _resizing; }
        }

        private static ResizeZone ZoneAt(Rect area, ResizeZone allowed, Vector2 point)
        {
            return WindowResize.Hit(area.width, area.height, point.x - area.x, point.y - area.y, ClaudiaSkin.Scaled(EdgeZone), ClaudiaSkin.Scaled(CornerZone), allowed);
        }

        /// <summary>Draws the hover text (<see cref="GUI.tooltip"/>) near the pointer; call last in the draw function.</summary>
        public static void DrawTooltip()
        {
            if (string.IsNullOrEmpty(GUI.tooltip)) return;
            var content = new GUIContent(GUI.tooltip);
            var size = GUI.skin.box.CalcSize(content);
            var mouse = Event.current.mousePosition;
            GUI.Box(new Rect(mouse.x + 12, mouse.y + 18, size.x + 6, size.y + 4), content);
        }

        /// <summary>The keyboard lock follows text-field focus.</summary>
        public void SetTyping(bool typing)
        {
            if (typing == _keyboardLocked) return;
            if (typing) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, _keyboardLockId);
            else InputLockManager.RemoveControlLock(_keyboardLockId);
            _keyboardLocked = typing;
        }

        /// <summary>Releases every lock and any resize; safe to call when nothing is locked.</summary>
        public void Unlock()
        {
            _resizing = false;
            SetTyping(false);
            SetMouseLock(false);
        }

        // On entering a scene (or the first draw), the remembered box replaces the default.
        private void ApplyRemembered()
        {
            if (_memoryName == null) return;
            var scene = WindowMemory.Scene();
            if (scene == _appliedScene) return;
            _appliedScene = scene;
            WindowBox box;
            if (WindowMemory.TryGet(_memoryName, out box))
                Rect = new Rect(box.X, box.Y, _resizable ? Mathf.Max(box.Width, _min.x) : Rect.width, _resizable ? Mathf.Max(box.Height, _min.y) : Rect.height);
            else
                Rect = _default;
            _lastStored = new WindowBox(Rect.x, Rect.y, Rect.width, Rect.height);
        }

        // Saved once a drag or resize is let go, not on every frame of it.
        private void StoreWhenSettled()
        {
            if (_memoryName == null || _resizing || Input.GetMouseButton(0)) return;
            var box = new WindowBox(Rect.x, Rect.y, Rect.width, Rect.height);
            if (box.Equals(_lastStored)) return;
            _lastStored = box;
            WindowMemory.Store(_memoryName, box);
        }

        private void SetMouseLock(bool locked)
        {
            if (locked == _mouseLocked) return;
            if (locked) InputLockManager.SetControlLock(MouseTypes, _mouseLockId);
            else InputLockManager.RemoveControlLock(_mouseLockId);
            _mouseLocked = locked;
        }

        // A shrinking screen or a drag mustn't strand the window out of reach.
        private Rect KeepOnScreen(Rect rect)
        {
            rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, Screen.width - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, Screen.height - rect.height));
            if (_resizable)
            {
                rect.width = Mathf.Min(rect.width, Screen.width);
                rect.height = Mathf.Min(rect.height, Screen.height);
            }
            return rect;
        }

        private static Vector2 GuiMouse()
        {
            return new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        }
    }
}
