// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Config;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>How Claudia trades time against delta-v when she picks a window.</summary>
    public enum TimeVersusDeltaV
    {
        /// <summary>Never waits for a better window.</summary>
        Fastest,
        /// <summary>Waits up to 6 h to save delta-v.</summary>
        Balanced,
        /// <summary>Waits for the cheapest window.</summary>
        MostEfficient,
    }

    /// <summary>
    /// The Mission Settings: the player's choices for how Claudia meets the Mission Goal. Chosen at start,
    /// persisted with the Mission (as the name/value pairs of <see cref="ToDictionary"/>), editable later.
    /// </summary>
    public sealed class MissionSettingsValues
    {
        public const string DaylightKey = "daylight_landing";
        public const string TimeKey = "time_vs_dv";

        /// <summary>Prefer to land in daylight; drives the <c>land</c> block's <c>daylight</c> option.</summary>
        public bool DaylightLanding = true;
        public TimeVersusDeltaV TimeVersusDeltaV = TimeVersusDeltaV.Balanced;

        public static MissionSettingsValues Defaults(PlayerConfig config)
        {
            return new MissionSettingsValues { DaylightLanding = config.DefaultDaylightLanding, TimeVersusDeltaV = config.DefaultTimeVersusDeltaV };
        }

        /// <summary>The settings in a Mission's name/value pairs; a missing or unreadable one takes the default.</summary>
        public static MissionSettingsValues FromDictionary(IDictionary<string, string> pairs, MissionSettingsValues defaults)
        {
            var values = new MissionSettingsValues { DaylightLanding = defaults.DaylightLanding, TimeVersusDeltaV = defaults.TimeVersusDeltaV };
            string text;
            if (pairs != null && pairs.TryGetValue(DaylightKey, out text) && (text == "true" || text == "false")) values.DaylightLanding = text == "true";
            TimeVersusDeltaV time;
            if (pairs != null && pairs.TryGetValue(TimeKey, out text) && Enum.TryParse(text, out time)) values.TimeVersusDeltaV = time;
            return values;
        }

        public void WriteTo(IDictionary<string, string> pairs)
        {
            pairs[DaylightKey] = DaylightLanding ? "true" : "false";
            pairs[TimeKey] = TimeVersusDeltaV.ToString();
        }

        public MissionSettingsValues Copy()
        {
            return new MissionSettingsValues { DaylightLanding = DaylightLanding, TimeVersusDeltaV = TimeVersusDeltaV };
        }

        public bool SameAs(MissionSettingsValues other)
        {
            return other != null && DaylightLanding == other.DaylightLanding && TimeVersusDeltaV == other.TimeVersusDeltaV;
        }

        public static string Label(TimeVersusDeltaV time)
        {
            switch (time)
            {
                case TimeVersusDeltaV.Fastest: return "Fastest";
                case TimeVersusDeltaV.MostEfficient: return "Most efficient";
                default: return "Balanced (waits up to 6 h)";
            }
        }

        /// <summary>The settings in the player's words, for the Mission board.</summary>
        public string Summary()
        {
            return "Daylight landing: " + (DaylightLanding ? "on" : "off") + "; time versus delta-v: " + Label(TimeVersusDeltaV);
        }
    }

    /// <summary>Time as the player reads it: the Kerbin calendar (6 h days, 426 days a year).</summary>
    public static class MissionClock
    {
        private const double Minute = 60, Hour = 3600, Day = 6 * Hour, Year = 426 * Day;

        /// <summary>A UT as "Y1 D12 03:20:00" (year and day count from 1).</summary>
        public static string Ut(double ut)
        {
            if (ut < 0) ut = 0;
            var year = (long)(ut / Year);
            var rest = ut - year * Year;
            var day = (long)(rest / Day);
            rest -= day * Day;
            var hour = (long)(rest / Hour);
            rest -= hour * Hour;
            var minute = (long)(rest / Minute);
            var second = (long)(rest - minute * Minute);
            return string.Format(CultureInfo.InvariantCulture, "Y{0} D{1} {2:00}:{3:00}:{4:00}", year + 1, day + 1, hour, minute, second);
        }

        /// <summary>A span as "2 d 3 h" or "45 min" (days are Kerbin days of 6 h).</summary>
        public static string Span(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var days = (long)(seconds / Day);
            var hours = (long)((seconds - days * Day) / Hour);
            var minutes = (long)((seconds - days * Day - hours * Hour) / Minute);
            if (days > 0) return days.ToString(CultureInfo.InvariantCulture) + " d " + hours.ToString(CultureInfo.InvariantCulture) + " h";
            if (hours > 0) return hours.ToString(CultureInfo.InvariantCulture) + " h " + minutes.ToString(CultureInfo.InvariantCulture) + " min";
            if (minutes > 0) return minutes.ToString(CultureInfo.InvariantCulture) + " min";
            return ((long)seconds).ToString(CultureInfo.InvariantCulture) + " s";
        }
    }

    // ---- Player actions ----

    /// <summary>
    /// Edit the Mission Settings of the Mission in play. Claudia then revises the Mission Plan, which needs
    /// Approval again. Taken only while a plan awaits Approval and no request is out.
    /// </summary>
    public sealed class EditSettings : MissionInput
    {
        public readonly MissionSettingsValues Settings;
        public EditSettings(MissionSettingsValues settings) { Settings = settings; }
    }

    /// <summary>The Warp to Due Time button: asks the adapter to warp to the Due Time on rails. The mod warps no other time between sequences.</summary>
    public sealed class WarpToDueTime : MissionInput
    {
    }

    /// <summary>The Go to <i>vessel</i> button, shown at the Due Time: switches to the Mission's vessel.</summary>
    public sealed class GoToVessel : MissionInput
    {
    }

    // ---- Adapter commands ----

    /// <summary>Set a stock Alarm Clock alarm at <see cref="Ut"/> for the Mission, replacing the Mission's earlier one.</summary>
    public sealed class SetDueTimeAlarm : AdapterCommand
    {
        public readonly string MissionId;
        public readonly double Ut;
        public readonly string Title;

        public SetDueTimeAlarm(string missionId, double ut, string title)
        {
            MissionId = missionId;
            Ut = ut;
            Title = title;
        }
    }

    /// <summary>Remove the Mission's Due Time alarm (the sequence started, was replaced, or the Mission ended).</summary>
    public sealed class ClearDueTimeAlarm : AdapterCommand
    {
        public readonly string MissionId;
        public ClearDueTimeAlarm(string missionId) { MissionId = missionId; }
    }

    /// <summary>Warp on rails to <see cref="Ut"/> (the Warp to Due Time button). The adapter stops at the target.</summary>
    public sealed class WarpToUt : AdapterCommand
    {
        public readonly double Ut;
        public WarpToUt(double ut) { Ut = ut; }
    }

    /// <summary>Drop time warp to 1× (the Due Time was reached).</summary>
    public sealed class DropWarp : AdapterCommand
    {
    }

    /// <summary>Switch to the vessel with <see cref="VesselId"/> (the Go to <i>vessel</i> button).</summary>
    public sealed class SwitchToVessel : AdapterCommand
    {
        public readonly string VesselId;
        public SwitchToVessel(string vesselId) { VesselId = vesselId; }
    }
}
