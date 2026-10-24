// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Detects the player holding attitude, translation or throttle input (keys or a stick) and tells the core for
    /// how long; the core decides whether that is long enough to take Manual Control (spec: over 1 s). It reads the
    /// player's own bindings, not the flight control state, because kOS writes steering and throttle into that
    /// state while a script runs. Nothing is reported while a text field has focus.
    /// </summary>
    public sealed class ManualInputWatcher
    {
        // Held input is reported from this long on, and then this often, so the core sees the hold grow without a call per frame.
        private const float FirstReport = 0.5f;
        private const float ReportEvery = 0.25f;
        private const float StickDeadZone = 0.15f;

        private float _since = -1f;
        private float _lastReport;

        /// <summary>Call each frame. <paramref name="watching"/> is true while Claudia has the craft in Flight; <paramref name="held"/> gets the seconds held so far.</summary>
        public void Update(bool watching, Action<double> held)
        {
            if (!watching || InputLockManager.IsLocked(ControlTypes.KEYBOARDINPUT) || !Pressed())
            {
                _since = -1f;
                return;
            }
            var now = Time.unscaledTime;
            if (_since < 0f)
            {
                _since = now;
                _lastReport = now - ReportEvery;
            }
            var seconds = now - _since;
            if (seconds < FirstReport || now - _lastReport < ReportEvery) return;
            _lastReport = now;
            held(seconds);
        }

        private static bool Pressed()
        {
            return Key(GameSettings.PITCH_UP) || Key(GameSettings.PITCH_DOWN)
                || Key(GameSettings.YAW_LEFT) || Key(GameSettings.YAW_RIGHT)
                || Key(GameSettings.ROLL_LEFT) || Key(GameSettings.ROLL_RIGHT)
                || Key(GameSettings.TRANSLATE_UP) || Key(GameSettings.TRANSLATE_DOWN)
                || Key(GameSettings.TRANSLATE_LEFT) || Key(GameSettings.TRANSLATE_RIGHT)
                || Key(GameSettings.TRANSLATE_FWD) || Key(GameSettings.TRANSLATE_BACK)
                || Key(GameSettings.THROTTLE_UP) || Key(GameSettings.THROTTLE_DOWN)
                || Stick(GameSettings.AXIS_PITCH) || Stick(GameSettings.AXIS_YAW) || Stick(GameSettings.AXIS_ROLL)
                || Stick(GameSettings.AXIS_THROTTLE);
        }

        private static bool Key(KeyBinding binding)
        {
            return binding != null && binding.GetKey();
        }

        private static bool Stick(AxisBinding binding)
        {
            return binding != null && Mathf.Abs(binding.GetAxis()) > StickDeadZone;
        }
    }
}
