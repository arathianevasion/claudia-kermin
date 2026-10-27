// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// Where in KSP a warp rate change happened, as the adapter's markers on the warp entry points see it (research #300:
    /// every source ends in the same private <c>TimeWarp.setRate</c>, and its only event says nothing about who).
    /// </summary>
    public enum WarpChangeSource
    {
        /// <summary>None of the markers: kOS (a script's <c>WARP</c> or <c>CANCELWARP</c>), the mod's own drop, an alarm, another mod.</summary>
        Other,
        /// <summary>The player's own controls: the warp keys (<c>TimeWarp.Update</c> with no warp-to pending), the warp arrows, the stock warp-to and alarm buttons.</summary>
        PlayerControl,
        /// <summary>KSP's own clamp (<c>TimeWarp.FixedUpdate</c>): an atmosphere, altitude, acceleration or SOI limit.</summary>
        KspClamp,
        /// <summary>A warp-to's own step: its start in <c>TimeWarp.Update</c>, or its coroutine while <c>WARPTO_LOCK</c> is held.</summary>
        WarpToStep,
    }

    /// <summary>Telling the player's warp changes from KSP's and the script's (#336).</summary>
    public static class PlayerWarp
    {
        /// <summary>
        /// Whether a rate change was the player's: their controls, or a warp-to's step while a warp key is down (its
        /// coroutine reads the keys itself and changes the rate before cancelling). A warp-to's other steps (its own
        /// arrival, a script's <c>WARP</c> under it) aren't, whoever started it.
        /// </summary>
        /// <param name="source">Where the change happened.</param>
        /// <param name="warpKeyDown">A warp key (<c>,</c> <c>.</c> <c>/</c>) is down this frame.</param>
        public static bool IsPlayers(WarpChangeSource source, bool warpKeyDown)
        {
            switch (source)
            {
                case WarpChangeSource.PlayerControl: return true;
                case WarpChangeSource.WarpToStep: return warpKeyDown;
                default: return false;
            }
        }
    }

    /// <summary>
    /// The player's warp cancel for one mod-started run (#336, ADR 0008): <c>ADDONS:CLAUDIA:WARPCANCELLED</c>. It turns
    /// true when the player lowers or stops warp, rails or physics, during the script's current wait, and resets when the
    /// next wait starts (<c>WAITSTART</c>), so a cancel holds for the rest of that wait, is still readable once the wait
    /// returns, and the next wait starts fresh. Raising warp isn't a cancel, and neither is KSP's own drop. Who made a
    /// change is <see cref="PlayerWarp.IsPlayers"/>'s call.
    /// </summary>
    public sealed class WarpCancel
    {
        private bool _waiting;

        /// <summary>The player lowered or stopped warp during the current (or the last) wait.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>A Core Library wait began (<c>WAITSTART</c>): a fresh start.</summary>
        public void WaitStarted()
        {
            _waiting = true;
            Cancelled = false;
        }

        /// <summary>The wait ended (<c>WAITEND</c>); <see cref="Cancelled"/> keeps its value until the next wait starts.</summary>
        public void WaitEnded()
        {
            _waiting = false;
        }

        /// <summary>
        /// The warp rate changed from one rate to another. askedHigher: the change asked for a higher rate and KSP's limits
        /// clamped it lower, so a player's raise isn't read as a cancel. Returns true when it cancelled the wait.
        /// </summary>
        public bool RateChanged(double from, double to, bool byPlayer, bool askedHigher = false)
        {
            if (!_waiting || !byPlayer || askedHigher || to >= from) return false;
            Cancelled = true;
            return true;
        }
    }
}
