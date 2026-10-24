// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>
    /// Universal time (seconds) as Kerbin calendar text: 6-hour days, 426-day years, the first year and day
    /// being 1. Times from now read "in 2d 3h 10m"; the date is for hover.
    /// </summary>
    public static class KerbinTime
    {
        public const int SecondsPerMinute = 60;
        public const int SecondsPerHour = 3600;
        public const int SecondsPerDay = 6 * SecondsPerHour;
        public const int DaysPerYear = 426;
        public const long SecondsPerYear = (long)DaysPerYear * SecondsPerDay;

        /// <summary>"in 2d 3h 10m", "3h 5m ago", or "now" inside a minute. Zero units are left out.</summary>
        public static string FromNow(double ut, double now)
        {
            var delta = ut - now;
            if (Math.Abs(delta) < SecondsPerMinute) return "now";
            var text = Span(Math.Abs(delta));
            return delta > 0 ? "in " + text : text + " ago";
        }

        /// <summary>A length of time as "2d 3h 10m"; under a minute is "0m".</summary>
        public static string Span(double seconds)
        {
            var total = (long)Math.Floor(Math.Max(0, seconds));
            var days = total / SecondsPerDay;
            var hours = total % SecondsPerDay / SecondsPerHour;
            var minutes = total % SecondsPerHour / SecondsPerMinute;
            var parts = "";
            if (days > 0) parts += days.ToString(CultureInfo.InvariantCulture) + "d ";
            if (hours > 0) parts += hours.ToString(CultureInfo.InvariantCulture) + "h ";
            if (minutes > 0 || parts.Length == 0) parts += minutes.ToString(CultureInfo.InvariantCulture) + "m ";
            return parts.TrimEnd();
        }

        /// <summary>"Year 2, Day 15, 03:10": the date a time lands on, for hover.</summary>
        public static string Date(double ut)
        {
            var total = (long)Math.Floor(Math.Max(0, ut));
            var year = total / SecondsPerYear + 1;
            var inYear = total % SecondsPerYear;
            var day = inYear / SecondsPerDay + 1;
            var hour = inYear % SecondsPerDay / SecondsPerHour;
            var minute = inYear % SecondsPerHour / SecondsPerMinute;
            return "Year " + year.ToString(CultureInfo.InvariantCulture) + ", Day " + day.ToString(CultureInfo.InvariantCulture)
                + ", " + hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
