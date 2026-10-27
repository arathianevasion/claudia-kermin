// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens.Flight;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Tells the player's warp changes from KSP's (#336, ADR 0008), so a running script's wait never overrides a player who
    /// lowered or stopped warp (<c>ADDONS:CLAUDIA:WARPCANCELLED</c>). KSP gives no such signal: every rate change ends in
    /// the private <c>TimeWarp.setRate</c> and <c>assumeWarpRate</c>, and its event carries no arguments (research #300). So a
    /// postfix on <c>assumeWarpRate</c> sees each change, and prefixes on the player's own entry points mark where it
    /// happened: the warp keys in <c>TimeWarp.Update</c>, the warp arrows, the stock warp-to and alarm buttons, and KSP's
    /// clamp in <c>TimeWarp.FixedUpdate</c>. A change outside every marker while <c>WARPTO_LOCK</c> is held is a warp-to's
    /// coroutine. <see cref="PlayerWarp.IsPlayers"/> makes the call; <see cref="RateChanged"/> reports it.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class PlayerWarpWatch : MonoBehaviour
    {
        private static bool patched;

        /// <summary>Where the rate change now happening is (all of KSP's warp runs on the main thread).</summary>
        private static WarpChangeSource scope = WarpChangeSource.Other;

        /// <summary>The warp-to running or pending was started from one of the player's buttons, not by a script.</summary>
        private static bool warpToIsPlayers;

        /// <summary>Every warp rate change: the rate before, the rate after, and whether it was the player's.</summary>
        public static event Action<double, double, bool> RateChanged;

        private void Start()
        {
            if (patched) return;
            patched = true;
            var harmony = new Harmony("claudiakermin.playerwarp");
            // The change itself; without it nothing else here matters.
            if (!Patch(harmony, typeof(TimeWarp), "assumeWarpRate", nameof(Before), nameof(Changed), null)) return;
            Patch(harmony, typeof(TimeWarp), "Update", nameof(InUpdate), null, nameof(Leave));
            Patch(harmony, typeof(TimeWarp), "FixedUpdate", nameof(InClamp), null, nameof(Leave));
            Patch(harmony, typeof(TimeWarp), nameof(TimeWarp.WarpTo), nameof(WarpToAsked), null, null);
            for (var i = 0; i < 8; i++) Player(harmony, typeof(UITimeWarpController), "ClickTimewarp" + i);
            Player(harmony, typeof(AutoWarpToUT), "OnSelect");
            Player(harmony, typeof(NavBallBurnVector), "OnWarpToButton");
            Player(harmony, typeof(NavBallBurnVector), "OnDismissButton");
            Player(harmony, typeof(ManeuverNodeEditorManager), "WarpToNextManNode");
            Player(harmony, typeof(AlarmClockUIFrame), "OnWarpTo");
        }

        private static void Player(Harmony harmony, Type type, string method)
        {
            Patch(harmony, type, method, nameof(InPlayerControl), null, nameof(Leave));
        }

        private static bool Patch(Harmony harmony, Type type, string method, string prefix, string postfix, string finalizer)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null) throw new MissingMethodException(type.Name, method);
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(PlayerWarpWatch), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(PlayerWarpWatch), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(PlayerWarpWatch), finalizer));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch " + type.Name + "." + method + "; the player's warp changes may read as KSP's: " + e);
                return false;
            }
        }

        // With a warp-to pending, Update's only rate change is starting it; otherwise its rate changes are the warp keys.
        private static void InUpdate(TimeWarp __instance, out WarpChangeSource __state)
        {
            __state = scope;
            scope = __instance.setAutoWarp ? WarpChangeSource.WarpToStep : WarpChangeSource.PlayerControl;
        }

        private static void InClamp(out WarpChangeSource __state)
        {
            __state = scope;
            scope = WarpChangeSource.KspClamp;
        }

        private static void InPlayerControl(out WarpChangeSource __state)
        {
            __state = scope;
            scope = WarpChangeSource.PlayerControl;
        }

        private static void Leave(WarpChangeSource __state)
        {
            scope = __state;
        }

        // A warp-to already running refuses a new one, so only an accepted one changes whose it is.
        private static void WarpToAsked()
        {
            if (!InputLockManager.IsLocked(ControlTypes.WARPTO_LOCK)) warpToIsPlayers = scope == WarpChangeSource.PlayerControl;
        }

        private static void Before(TimeWarp __instance, out float __state)
        {
            __state = __instance.tgt_rate;
        }

        private static void Changed(float rate, float __state)
        {
            try
            {
                var handler = RateChanged;
                if (handler == null || rate == __state) return;
                var source = scope != WarpChangeSource.Other ? scope
                    : InputLockManager.IsLocked(ControlTypes.WARPTO_LOCK) ? WarpChangeSource.WarpToStep : WarpChangeSource.Other;
                var keyDown = GameSettings.TIME_WARP_DECREASE.GetKeyDown() || GameSettings.TIME_WARP_INCREASE.GetKeyDown()
                    || GameSettings.TIME_WARP_STOP.GetKeyDown();
                handler(__state, rate, PlayerWarp.IsPlayers(source, warpToIsPlayers, keyDown));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " player warp watch: " + e);
            }
        }
    }
}
