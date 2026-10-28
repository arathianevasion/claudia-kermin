// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Config;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>What a Telemetry Check measures while Running. The units are the threshold's.</summary>
    public enum TelemetryQuantity
    {
        /// <summary>Delta-v used since the run started, m/s (the stock simulator's delta-v left, then and now).</summary>
        DeltaVUsed,
        /// <summary>The craft's delta-v left, m/s.</summary>
        DeltaVLeft,
        /// <summary>Game seconds since the run started.</summary>
        Duration,
        /// <summary>Apoapsis above sea level, m.</summary>
        Apoapsis,
        /// <summary>Periapsis above sea level, m.</summary>
        Periapsis,
        /// <summary>Inclination, degrees.</summary>
        Inclination,
        /// <summary>Altitude above sea level, m.</summary>
        Altitude,
        /// <summary>Vertical speed, m/s (negative going down).</summary>
        VerticalSpeed,
        /// <summary>A resource's level, percent of its capacity on the craft (<see cref="TelemetryCheck.Resource"/>).</summary>
        Resource,
        /// <summary>
        /// The needed-fuel margin, percent: how far the delta-v left is over (positive) or under (negative) what the
        /// Mission Plan still needs, this sequence's unspent share and every later sequence's.
        /// </summary>
        FuelMargin,
    }

    /// <summary>
    /// A Telemetry Check: a quantity the mod measures while Running, and the threshold whose crossing raises an Alert.
    /// Set in the Sequence Plan by Claudia, or a config default (<see cref="TelemetryDefaults"/>).
    /// </summary>
    public sealed class TelemetryCheck
    {
        public TelemetryQuantity Quantity;
        /// <summary>The resource's name for <see cref="TelemetryQuantity.Resource"/> (e.g. ElectricCharge); "" otherwise.</summary>
        public string Resource = "";
        /// <summary>Crossed when the quantity falls below the threshold; false is when it rises above.</summary>
        public bool Below;
        public double Threshold;
        /// <summary>Claudia's short reason; "" for a default.</summary>
        public string Why = "";
        /// <summary>A config default filling a gap Claudia left.</summary>
        public bool Default;

        /// <summary>The quantity and its threshold as the player reads them, e.g. "delta-v used over 1,200 m/s".</summary>
        public string Describe()
        {
            return Name() + (Below ? " under " : " over ") + Format(Threshold);
        }

        /// <summary>The quantity's name, e.g. "delta-v used", "ElectricCharge".</summary>
        public string Name()
        {
            switch (Quantity)
            {
                case TelemetryQuantity.DeltaVUsed: return "delta-v used";
                case TelemetryQuantity.DeltaVLeft: return "delta-v left";
                case TelemetryQuantity.Duration: return "time elapsed";
                case TelemetryQuantity.Apoapsis: return "apoapsis";
                case TelemetryQuantity.Periapsis: return "periapsis";
                case TelemetryQuantity.Inclination: return "inclination";
                case TelemetryQuantity.Altitude: return "altitude";
                case TelemetryQuantity.VerticalSpeed: return "vertical speed";
                case TelemetryQuantity.Resource: return Resource;
                default: return "needed-fuel margin";
            }
        }

        /// <summary>A value of this check's quantity with its unit, e.g. "1,200 m/s", "450 s", "10%".</summary>
        public string Format(double value)
        {
            switch (Quantity)
            {
                case TelemetryQuantity.DeltaVUsed:
                case TelemetryQuantity.DeltaVLeft:
                case TelemetryQuantity.VerticalSpeed:
                    return value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s";
                case TelemetryQuantity.Duration: return value.ToString("#,0", CultureInfo.InvariantCulture) + " s";
                case TelemetryQuantity.Apoapsis:
                case TelemetryQuantity.Periapsis:
                case TelemetryQuantity.Altitude:
                    return value.ToString("#,0", CultureInfo.InvariantCulture) + " m";
                case TelemetryQuantity.Inclination: return value.ToString("0.##", CultureInfo.InvariantCulture) + "°";
                default: return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            }
        }

        /// <summary>The same quantity watched the same way, for telling whether Claudia already set a check a default would fill.</summary>
        public bool SameQuantity(TelemetryCheck other)
        {
            return Quantity == other.Quantity && Below == other.Below
                && (Quantity != TelemetryQuantity.Resource || string.Equals(Resource, other.Resource, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The quantity comes from stock's delta-v, which can misread for a snapshot, so a crossing must repeat on the next reading.</summary>
        public bool NeedsConfirmation
        {
            get
            {
                return Quantity == TelemetryQuantity.DeltaVUsed || Quantity == TelemetryQuantity.DeltaVLeft
                    || Quantity == TelemetryQuantity.FuelMargin;
            }
        }

        /// <summary>
        /// An orbit quantity starts wrong on the pad (periapsis far under the surface), so its check watches only once the
        /// reading has been on the safe side of the threshold during the run (#419).
        /// </summary>
        public bool NeedsArming
        {
            get { return Quantity == TelemetryQuantity.Periapsis || Quantity == TelemetryQuantity.Apoapsis; }
        }

        /// <summary>The reading is past <paramref name="threshold"/>; false when the quantity can't be measured now.</summary>
        public bool Crossed(double? reading, double threshold)
        {
            if (!reading.HasValue) return false;
            return Below ? reading.Value < threshold : reading.Value > threshold;
        }

        /// <summary>The schema's name for a quantity, as Claudia writes it in a Sequence Plan.</summary>
        public static string Key(TelemetryQuantity quantity)
        {
            switch (quantity)
            {
                case TelemetryQuantity.DeltaVUsed: return "delta_v_used";
                case TelemetryQuantity.DeltaVLeft: return "delta_v_left";
                case TelemetryQuantity.Duration: return "duration";
                case TelemetryQuantity.Apoapsis: return "apoapsis";
                case TelemetryQuantity.Periapsis: return "periapsis";
                case TelemetryQuantity.Inclination: return "inclination";
                case TelemetryQuantity.Altitude: return "altitude";
                case TelemetryQuantity.VerticalSpeed: return "vertical_speed";
                case TelemetryQuantity.Resource: return "resource";
                default: return "fuel_margin";
            }
        }

        public static TelemetryQuantity? Parse(string key)
        {
            foreach (TelemetryQuantity quantity in Enum.GetValues(typeof(TelemetryQuantity)))
                if (Key(quantity) == key) return quantity;
            return null;
        }
    }

    /// <summary>
    /// What the mod measures of the craft while Running, from a Game Snapshot and the run's start; a quantity it can't
    /// measure now is null (no snapshot of the Mission's vessel, the stock delta-v simulator off, no such resource).
    /// </summary>
    public sealed class LiveReadings
    {
        public double ElapsedSeconds;
        public double? DeltaVUsed;
        public double? DeltaVLeft;
        public double? FuelMarginPercent;
        public double? Apoapsis;
        public double? Periapsis;
        public double? Inclination;
        public double? Altitude;
        public double? VerticalSpeed;
        public readonly Dictionary<string, double> ResourcePercent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <param name="snapshot">The latest snapshot of the Mission's vessel; null when there is none.</param>
        /// <param name="startDeltaVLeft">The delta-v left when the run started; null when it couldn't be read.</param>
        /// <param name="expectedDeltaV">The Sequence Plan's expected delta-v, m/s.</param>
        /// <param name="laterDeltaV">What the Mission Plan's later sequences need, m/s.</param>
        public static LiveReadings Read(GameSnapshot snapshot, double elapsedSeconds, double? startDeltaVLeft, double expectedDeltaV, double laterDeltaV)
        {
            var readings = new LiveReadings { ElapsedSeconds = Math.Max(0, elapsedSeconds) };
            if (snapshot == null) return readings;
            readings.Altitude = snapshot.Altitude;
            readings.VerticalSpeed = snapshot.VerticalSpeed;
            if (snapshot.Orbit != null)
            {
                readings.Apoapsis = snapshot.Orbit.Apoapsis;
                readings.Periapsis = snapshot.Orbit.Periapsis;
                readings.Inclination = snapshot.Orbit.Inclination;
            }
            foreach (var group in snapshot.Resources.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var max = group.Sum(r => r.Max);
                if (max > 0) readings.ResourcePercent[group.Key] = 100 * group.Sum(r => r.Amount) / max;
            }
            var left = DeltaVLeftOf(snapshot);
            if (left.HasValue)
            {
                readings.DeltaVLeft = left;
                if (startDeltaVLeft.HasValue) readings.DeltaVUsed = Math.Max(0, startDeltaVLeft.Value - left.Value);
                var needed = Math.Max(0, expectedDeltaV - (readings.DeltaVUsed ?? 0)) + Math.Max(0, laterDeltaV);
                if (needed > 0) readings.FuelMarginPercent = 100 * (left.Value - needed) / needed;
            }
            return readings;
        }

        /// <summary>The craft's vacuum delta-v left from the stock simulator; null when it is off or a stage's reading is unreliable (a trickle throttle, #234).</summary>
        public static double? DeltaVLeftOf(GameSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.DeltaVSimulator || snapshot.DeltaVUnreliable) return null;
            return snapshot.Stages.Sum(s => s.DeltaVVac);
        }

        /// <summary>The craft's stock MonoPropellant, in units, across all its tanks (0 when it carries none); null when there is no snapshot.</summary>
        public static double? MonopropellantOf(GameSnapshot snapshot)
        {
            if (snapshot == null) return null;
            return snapshot.Resources.Where(r => string.Equals(r.Name, "MonoPropellant", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Amount);
        }

        /// <summary>This reading of a check's quantity; null when it can't be measured now.</summary>
        public double? Of(TelemetryCheck check)
        {
            switch (check.Quantity)
            {
                case TelemetryQuantity.DeltaVUsed: return DeltaVUsed;
                case TelemetryQuantity.DeltaVLeft: return DeltaVLeft;
                case TelemetryQuantity.Duration: return ElapsedSeconds;
                case TelemetryQuantity.Apoapsis: return Apoapsis;
                case TelemetryQuantity.Periapsis: return Periapsis;
                case TelemetryQuantity.Inclination: return Inclination;
                case TelemetryQuantity.Altitude: return Altitude;
                case TelemetryQuantity.VerticalSpeed: return VerticalSpeed;
                case TelemetryQuantity.Resource:
                    double percent;
                    return ResourcePercent.TryGetValue(check.Resource, out percent) ? percent : (double?)null;
                default: return FuelMarginPercent;
            }
        }
    }

    /// <summary>
    /// The config defaults that fill the gaps Claudia leaves in a Sequence Plan's Telemetry Checks (spec story 85):
    /// delta-v used over 120% of the planned delta-v, time elapsed over 150% of the planned duration, electric charge
    /// under 10%, and the needed-fuel margin under minus 10% (the delta-v left under what the Mission Plan still needs,
    /// less 10%). A quantity Claudia set a check on keeps hers. A plan expecting no delta-v or no time gets no default
    /// on it, since any reading would cross it.
    /// </summary>
    public static class TelemetryDefaults
    {
        public static List<TelemetryCheck> Fill(SequencePlan plan, PlayerConfig config)
        {
            var checks = plan.TelemetryChecks.ToList();
            var defaults = new List<TelemetryCheck>();
            if (plan.ExpectedDeltaV > 0)
                defaults.Add(new TelemetryCheck { Quantity = TelemetryQuantity.DeltaVUsed, Threshold = Math.Round(plan.ExpectedDeltaV * config.CheckDeltaVUsedPercent / 100.0) });
            if (plan.ExpectedDurationSeconds > 0)
                defaults.Add(new TelemetryCheck { Quantity = TelemetryQuantity.Duration, Threshold = Math.Round(plan.ExpectedDurationSeconds * config.CheckDurationPercent / 100.0) });
            defaults.Add(new TelemetryCheck { Quantity = TelemetryQuantity.Resource, Resource = "ElectricCharge", Below = true, Threshold = config.CheckElectricChargePercent });
            defaults.Add(new TelemetryCheck { Quantity = TelemetryQuantity.FuelMargin, Below = true, Threshold = -config.CheckFuelMarginPercent });
            foreach (var check in defaults)
            {
                if (checks.Any(c => c.SameQuantity(check))) continue;
                check.Default = true;
                checks.Add(check);
            }
            return checks;
        }
    }
}
