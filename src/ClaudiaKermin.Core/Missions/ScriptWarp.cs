// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// When the mod holds a running script's in-script warp at 1× (ADR 0004). The Core Library warps on rails through
    /// a sequence's waits; while the Mission needs the player, warp drops to 1× and the script doesn't warp again until
    /// the hold ends. A Pause, the Spending Cap's waiting call and Manual Control hold for as long as they last; link
    /// loss and a stock alarm are moments, so they hold for <see cref="MomentHoldSeconds"/> of game time and the wait
    /// then warps on (a link that drops behind the Mun every orbit mustn't leave a long hold at 1×).
    /// </summary>
    public static class ScriptWarp
    {
        /// <summary>Game seconds a moment (link loss, a stock alarm) holds warp at 1× before the script may warp again.</summary>
        public const double MomentHoldSeconds = 10;

        public const string LinkLost = "the link to Kerbin dropped";
        public const string StockAlarm = "a stock alarm went off";

        /// <summary>
        /// Why warp is held at 1× now, or "" when it isn't. <paramref name="running"/>: a script runs (a Pause during a
        /// run counts); <paramref name="capWait"/>: Claudia's next request waits at the Spending Cap;
        /// <paramref name="abortRecommended"/>: Claudia recommends Abort and the player hasn't decided (never in a Blackout);
        /// <paramref name="moment"/>: a moment still holding, or "". Manual Control holds even with no script, since
        /// taking the craft ends the run.
        /// </summary>
        public static string HoldReason(bool running, MissionStage stage, bool capWait, bool abortRecommended, string moment)
        {
            if (stage == MissionStage.ManualControl) return "you have Manual Control";
            if (!running) return "";
            if (stage == MissionStage.Paused) return "the Mission is Paused";
            if (abortRecommended) return "Claudia recommends Abort";
            if (capWait) return "Claudia's next request waits at the Spending Cap";
            return moment ?? "";
        }
    }

    /// <summary>A stock Alarm Clock alarm went off (any alarm, the player's own included).</summary>
    public sealed class StockAlarmFired : MissionInput
    {
    }

    /// <summary>
    /// Hold a running script's in-script warp at 1× (<see cref="Reason"/> says why), or let it warp again ("").
    /// The Core Library reads it as <c>ADDONS:CLAUDIA:WARPHOLD</c>. Issued whenever the reason changes; a new hold
    /// comes with a <see cref="DropWarp"/>.
    /// </summary>
    public sealed class HoldScriptWarp : AdapterCommand
    {
        public readonly string Reason;
        public HoldScriptWarp(string reason) { Reason = reason ?? ""; }
    }
}
