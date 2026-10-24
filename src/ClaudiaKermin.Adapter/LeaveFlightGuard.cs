// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core;
using HarmonyLib;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Leaving Flight while a Maneuver Sequence is Running cuts its script off, so the player is asked first.
    /// A prefix on <c>HighLogic.LoadScene</c> holds the change and shows a dialog; Leave marks the sequence
    /// interrupted (the core does that when the scene changes) and repeats the change. Other scene loads,
    /// and leaving Flight when nothing is running, pass straight through.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class LeaveFlightGuard : MonoBehaviour
    {
        private static bool patched;
        private static bool leaving;

        private void Start()
        {
            if (patched) return;
            patched = true;
            try
            {
                var harmony = new Harmony("claudiakermin.leaveflight");
                harmony.Patch(AccessTools.Method(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) }),
                    prefix: new HarmonyMethod(typeof(LeaveFlightGuard), nameof(AskFirst)));
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " could not patch scene loading; leaving Flight while Running won't ask first: " + e);
            }
        }

        // Returning false holds the scene change until the player answers.
        private static bool AskFirst(GameScenes scene)
        {
            var driver = MissionDriver.Current;
            if (leaving || driver == null || HighLogic.LoadedScene != GameScenes.FLIGHT || scene == GameScenes.FLIGHT) return true;
            if (!driver.LeavingFlightNeedsConfirmation) return true;

            var dialog = new MultiOptionDialog(
                "ClaudiaKerminLeaveFlight",
                "A Maneuver Sequence is Running. If you leave Flight now its script is cut off and the sequence is marked "
                + "interrupted; Claudia will Debrief it when you come back.",
                "Claudia Kermin",
                HighLogic.UISkin,
                new DialogGUIBase[]
                {
                    new DialogGUIHorizontalLayout(
                        new DialogGUIButton("Leave Flight", () =>
                        {
                            leaving = true;
                            try
                            {
                                HighLogic.LoadScene(scene);
                            }
                            finally
                            {
                                leaving = false;
                            }
                        }, true),
                        new DialogGUIButton("Stay", () => { }, true)),
                });
            PopupDialog.SpawnPopupDialog(dialog, false, HighLogic.UISkin);
            return false;
        }
    }
}
