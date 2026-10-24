// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The game side of a Due Time: the stock Alarm Clock alarm, Warp to Due Time, the warp drop and Go to
    /// <i>vessel</i>. The mod warps only when the player presses Warp to Due Time, never between sequences on its own.
    /// </summary>
    internal static class DueTimeActions
    {
        private const string AlarmPrefix = "Claudia: ";

        private static string Tag(string missionId)
        {
            return "[Claudia Kermin " + missionId + "]";
        }

        /// <summary>A raw alarm at the Due Time that stops warp; replaces the Mission's earlier one.</summary>
        public static void SetAlarm(SetDueTimeAlarm command)
        {
            if (AlarmClockScenario.Instance == null) return;
            ClearAlarm(command.MissionId);
            var title = command.Title.StartsWith(AlarmPrefix) ? command.Title : AlarmPrefix + command.Title;
            var alarm = new AlarmTypeRaw
            {
                title = title,
                description = "Due Time for Claudia's next Maneuver Sequence. " + Tag(command.MissionId),
                ut = command.Ut,
            };
            alarm.actions.warp = AlarmActions.WarpEnum.KillWarp;
            alarm.actions.message = AlarmActions.MessageEnum.Yes;
            AlarmClockScenario.AddAlarm(alarm);
        }

        public static void ClearAlarm(string missionId)
        {
            var scenario = AlarmClockScenario.Instance;
            if (scenario == null || scenario.alarms == null) return;
            var tag = Tag(missionId);
            var ids = scenario.alarms.Values.Where(a => a.description != null && a.description.Contains(tag)).Select(a => a.Id).ToList();
            foreach (var id in ids) AlarmClockScenario.DeleteAlarm(id);
        }

        /// <summary>The Warp to Due Time button: on-rails warp to the UT, stopping there.</summary>
        public static void WarpTo(double ut)
        {
            if (TimeWarp.fetch == null || Planetarium.GetUniversalTime() >= ut) return;
            TimeWarp.fetch.WarpTo(ut);
        }

        /// <summary>Warp to 1×, cancelling any warp-to in progress.</summary>
        public static void DropWarp()
        {
            if (TimeWarp.fetch == null) return;
            TimeWarp.fetch.CancelAutoWarp();
            TimeWarp.SetRate(0, true);
        }

        /// <summary>The Go to <i>vessel</i> button: switches to the Mission's vessel from Flight, or flies it from the Tracking Station or Space Center.</summary>
        public static void GoToVessel(string vesselId)
        {
            Vessel target = null;
            foreach (var vessel in FlightGlobals.Vessels)
                if (vessel.id.ToString("N") == vesselId) target = vessel;
            if (target == null)
            {
                ScreenMessages.PostScreenMessage("Claudia: can't find the Mission's vessel.", 6f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            if (HighLogic.LoadedSceneIsFlight && target.loaded)
            {
                FlightGlobals.SetActiveVessel(target);
                return;
            }
            FlightDriver.StartAndFocusVessel(HighLogic.CurrentGame, FlightGlobals.Vessels.IndexOf(target));
        }
    }
}
