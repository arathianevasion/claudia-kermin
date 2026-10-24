// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// What every Claudia Kermin window shares: the IMGUI window itself, an optional bottom-right resize
    /// grip, and the KSP input locks. While the pointer is over the window (or a resize is under way) the
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

        private const float GripSize = 18f;

        private readonly int _windowId;
        private readonly string _title;
        private readonly string _keyboardLockId;
        private readonly string _mouseLockId;
        private readonly Vector2 _min;
        private readonly bool _resizable;
        private bool _keyboardLocked;
        private bool _mouseLocked;
        private bool _resizing;
        private Vector2 _resizeFromMouse;
        private Vector2 _resizeFromSize;

        private string _memoryName;
        private string _appliedScene;
        private Rect _default;
        private WindowBox _lastStored;

        public Rect Rect;

        /// <summary>Where the window opens in the VAB/SPH when nothing is remembered; unset means the same as elsewhere.</summary>
        public Rect? EditorDefault;

        /// <param name="lockId">Unique per window; the keyboard and mouse locks get their own suffixes.</param>
        /// <param name="min">Smallest size the grip allows; ignored when the window isn't resizable.</param>
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
            if (_resizing) Resize();
            Rect = _resizable
                ? GUILayout.Window(_windowId, Rect, draw, _title, GUILayout.Width(Rect.width), GUILayout.Height(Rect.height))
                : GUILayout.Window(_windowId, Rect, draw, _title, options);
            Rect = KeepOnScreen(Rect);
            StoreWhenSettled();
            // A held button keeps the lock, so a fast window drag can't outrun the pointer and drop it.
            SetMouseLock(_resizing || Rect.Contains(GuiMouse()) || (_mouseLocked && Input.GetMouseButton(0)));
        }

        /// <summary>Call at the end of the draw function, before <c>GUI.DragWindow</c>: handles the grip.</summary>
        public void DrawGrip()
        {
            if (!_resizable) return;
            var grip = new Rect(Rect.width - GripSize - 2, Rect.height - GripSize - 2, GripSize, GripSize);
            GUI.Label(grip, "//");
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition))
            {
                _resizing = true;
                _resizeFromMouse = Input.mousePosition;
                _resizeFromSize = new Vector2(Rect.width, Rect.height);
                e.Use();
            }
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
                Rect = (scene == "vab" || scene == "sph") && EditorDefault.HasValue ? EditorDefault.Value : _default;
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

        private void Resize()
        {
            if (!Input.GetMouseButton(0))
            {
                _resizing = false;
                return;
            }
            // Screen y runs up, the window's height runs down.
            var delta = (Vector2)Input.mousePosition - _resizeFromMouse;
            var size = new Vector2(_resizeFromSize.x + delta.x, _resizeFromSize.y - delta.y);
            Rect.width = Mathf.Clamp(size.x, _min.x, Mathf.Max(_min.x, Screen.width - Rect.x));
            Rect.height = Mathf.Clamp(size.y, _min.y, Mathf.Max(_min.y, Screen.height - Rect.y));
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
