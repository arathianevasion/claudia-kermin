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
        public const string PhysicsWarpKey = "physics_warp";

        /// <summary>Prefer to land in daylight; drives the <c>land</c> block's <c>daylight</c> option.</summary>
        public bool DaylightLanding = true;
        public TimeVersusDeltaV TimeVersusDeltaV = TimeVersusDeltaV.Balanced;

        /// <summary>The player's choice of Physics warp on Coasts (ADR 0008); starts as <c>physics_warp</c> in the config.</summary>
        public bool PhysicsWarp = true;

        /// <summary>
        /// <c>physics_warp</c> in the config is on. Not saved: read from the config each time, so the config alone can turn
        /// physics warp back on, and the setting shows greyed out and off while it is false.
        /// </summary>
        public bool ConfigAllowsPhysicsWarp = true;

        /// <summary>Physics warp is allowed now, the gate's single input: the config and the Mission Setting are both on.</summary>
        public bool PhysicsWarpOn
        {
            get { return ConfigAllowsPhysicsWarp && PhysicsWarp; }
        }

        /// <summary>The wait Balanced allows, hours (<see cref="Label"/> says 6 h); the most a Planning Tool defaults a search window to.</summary>
        public const double BalancedWaitHours = 6;

        /// <summary>How long a Planning Tool looks ahead when Claudia gives no window: Fastest never waits for a better window, Most efficient waits for the best.</summary>
        public double TimeCapHours
        {
            get { return TimeVersusDeltaV == TimeVersusDeltaV.Fastest ? 1 : TimeVersusDeltaV == TimeVersusDeltaV.MostEfficient ? 24 : BalancedWaitHours; }
        }

        public static MissionSettingsValues Defaults(PlayerConfig config)
        {
            return new MissionSettingsValues
            {
                DaylightLanding = config.DefaultDaylightLanding,
                TimeVersusDeltaV = config.DefaultTimeVersusDeltaV,
                // The player's own choice starts on; the config alone gates it (PhysicsWarpOn), so turning the config back on turns it back on.
                PhysicsWarp = true,
                ConfigAllowsPhysicsWarp = config.PhysicsWarp,
            };
        }

        /// <summary>The settings in a Mission's name/value pairs; a missing or unreadable one takes the default.</summary>
        public static MissionSettingsValues FromDictionary(IDictionary<string, string> pairs, MissionSettingsValues defaults)
        {
            var values = defaults.Copy();
            string text;
            if (pairs != null && pairs.TryGetValue(DaylightKey, out text) && (text == "true" || text == "false")) values.DaylightLanding = text == "true";
            TimeVersusDeltaV time;
            if (pairs != null && pairs.TryGetValue(TimeKey, out text) && Enum.TryParse(text, out time)) values.TimeVersusDeltaV = time;
            if (pairs != null && pairs.TryGetValue(PhysicsWarpKey, out text) && (text == "true" || text == "false")) values.PhysicsWarp = text == "true";
            return values;
        }

        public void WriteTo(IDictionary<string, string> pairs)
        {
            pairs[DaylightKey] = DaylightLanding ? "true" : "false";
            pairs[TimeKey] = TimeVersusDeltaV.ToString();
            pairs[PhysicsWarpKey] = PhysicsWarp ? "true" : "false";
        }

        public MissionSettingsValues Copy()
        {
            return new MissionSettingsValues
            {
                DaylightLanding = DaylightLanding,
                TimeVersusDeltaV = TimeVersusDeltaV,
                PhysicsWarp = PhysicsWarp,
                ConfigAllowsPhysicsWarp = ConfigAllowsPhysicsWarp,
            };
        }

        public bool SameAs(MissionSettingsValues other)
        {
            return other != null && DaylightLanding == other.DaylightLanding && TimeVersusDeltaV == other.TimeVersusDeltaV && PhysicsWarp == other.PhysicsWarp;
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
            return "Daylight landing: " + (DaylightLanding ? "on" : "off") + "; time versus delta-v: " + Label(TimeVersusDeltaV)
                + "; physics warp on Coasts: " + (PhysicsWarpOn ? "on" : "off");
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
