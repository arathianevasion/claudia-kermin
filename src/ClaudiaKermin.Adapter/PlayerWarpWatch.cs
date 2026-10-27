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
        /// <summary>Stock's warp arrows, <c>UITimeWarpController.ClickTimewarp0</c> to <c>7</c>: one per rails rate.</summary>
        private const int WarpArrows = 8;

        private static bool patched;

        /// <summary>Where the rate change now happening is (all of KSP's warp runs on the main thread).</summary>
        private static WarpChangeSource currentSource = WarpChangeSource.Other;

        /// <summary>The latest <c>setRate</c> asked for a higher rate than the current one (KSP's limits may clamp it lower).</summary>
        private static bool askedHigher;

        /// <summary>
        /// Every warp rate change: the rate before, the rate after, whether it was the player's, and whether it asked for a
        /// higher rate that KSP clamped (<see cref="WarpCancel.RateChanged"/>'s arguments).
        /// </summary>
        public static event Action<double, double, bool, bool> RateChanged;

        private void Start()
        {
            if (patched) return;
            patched = true;
            var harmony = new Harmony("claudiakermin.playerwarp");
            // The change itself; without it nothing else here matters.
            if (!Patch(harmony, typeof(TimeWarp), "assumeWarpRate", nameof(CaptureRate), nameof(ReportChange), null)) return;
            Patch(harmony, typeof(TimeWarp), "setRate", nameof(CaptureAsk), null, null);
            Patch(harmony, typeof(TimeWarp), "Update", nameof(MarkUpdate), null, nameof(RestoreSource));
            Patch(harmony, typeof(TimeWarp), "FixedUpdate", nameof(MarkClamp), null, nameof(RestoreSource));
            for (var i = 0; i < WarpArrows; i++) MarkPlayer(harmony, typeof(UITimeWarpController), "ClickTimewarp" + i);
            MarkPlayer(harmony, typeof(AutoWarpToUT), "OnSelect");
            MarkPlayer(harmony, typeof(NavBallBurnVector), "OnWarpToButton");
            MarkPlayer(harmony, typeof(NavBallBurnVector), "OnDismissButton");
            MarkPlayer(harmony, typeof(ManeuverNodeEditorManager), "WarpToNextManNode");
            MarkPlayer(harmony, typeof(AlarmClockUIFrame), "OnWarpTo");
        }

        private static void MarkPlayer(Harmony harmony, Type type, string method)
        {
            Patch(harmony, type, method, nameof(MarkPlayerControl), null, nameof(RestoreSource));
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
        private static void MarkUpdate(TimeWarp __instance, out WarpChangeSource __state)
        {
            __state = currentSource;
            currentSource = __instance.setAutoWarp ? WarpChangeSource.WarpToStep : WarpChangeSource.PlayerControl;
        }

        private static void MarkClamp(out WarpChangeSource __state)
        {
            __state = currentSource;
            currentSource = WarpChangeSource.KspClamp;
        }

        private static void MarkPlayerControl(out WarpChangeSource __state)
        {
            __state = currentSource;
            currentSource = WarpChangeSource.PlayerControl;
        }

        private static void RestoreSource(WarpChangeSource __state)
        {
            currentSource = __state;
        }

        private static void CaptureAsk(TimeWarp __instance, int rateIdx)
        {
            askedHigher = rateIdx > __instance.current_rate_index;
        }

        private static void CaptureRate(TimeWarp __instance, out float __state)
        {
            __state = __instance.tgt_rate;
        }

        private static void ReportChange(float rate, float __state)
        {
            try
            {
                var handler = RateChanged;
                if (handler == null || rate == __state) return;
                var source = currentSource != WarpChangeSource.Other ? currentSource
                    : InputLockManager.IsLocked(ControlTypes.WARPTO_LOCK) ? WarpChangeSource.WarpToStep : WarpChangeSource.Other;
                var keyDown = GameSettings.TIME_WARP_DECREASE.GetKeyDown() || GameSettings.TIME_WARP_INCREASE.GetKeyDown()
                    || GameSettings.TIME_WARP_STOP.GetKeyDown();
                handler(__state, rate, PlayerWarp.IsPlayers(source, keyDown), askedHigher);
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " player warp watch: " + e);
            }
        }
    }
}
