// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// In-script warp (ADR 0004): while a script warps through a wait, warp drops to 1× when the Mission needs the
    /// player (<see cref="ScriptWarp"/>), and the script is told to hold it there until the need has passed.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>Why the running script's warp is held at 1× now; "" when it may warp.</summary>
        private string _warpHold = "";
        /// <summary>The last moment that held warp (a stock alarm), and the UT until which it holds.</summary>
        private string _warpMoment = "";
        private double _warpMomentUntil = double.NegativeInfinity;

        private void StockAlarmWentOff()
        {
            if (_mission != null && IsRunning(_mission)) HoldFor(ScriptWarp.StockAlarm);
        }

        private void HoldFor(string moment)
        {
            _warpMoment = moment;
            _warpMomentUntil = _ut + ScriptWarp.MomentHoldSeconds;
        }

        /// <summary>After every input: a new hold drops warp and tells the script; the end of a hold lets it warp again.</summary>
        private void CheckScriptWarp()
        {
            var mission = _mission;
            var running = mission != null && !MissionOver && IsRunning(mission);

            var reason = mission == null || MissionOver ? ""
                : ScriptWarp.HoldReason(running, mission.Stage, mission.WaitingForCap, mission.AbortRecommendation.Length > 0,
                    _ut < _warpMomentUntil ? _warpMoment : "");
            if (reason == _warpHold) return;
            if (reason.Length > 0)
            {
                _commands.Add(new DropWarp());
                Note(LogKind.Notice, "Warp dropped to 1x: " + reason + ".");
            }
            _commands.Add(new HoldScriptWarp(reason));
            _warpHold = reason;
        }
    }
}
