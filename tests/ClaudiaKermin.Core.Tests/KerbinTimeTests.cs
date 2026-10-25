// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public sealed class KerbinTimeTests
    {
        private const double Day = 6 * 3600;
        private const double Year = 426 * Day;

        [Fact]
        public void ADayIsSixHoursAndAYearIs426Days()
        {
            Assert.Equal(21600, KerbinTime.SecondsPerDay);
            Assert.Equal(9201600, KerbinTime.SecondsPerYear);
        }

        [Fact]
        public void TimesAheadReadAsDaysHoursAndMinutesFromNow()
        {
            Assert.Equal("in 2d 3h 10m", KerbinTime.FromNow(1000 + 2 * Day + 3 * 3600 + 10 * 60, 1000));
        }

        [Fact]
        public void ZeroUnitsAreLeftOut()
        {
            Assert.Equal("in 2d 10m", KerbinTime.FromNow(2 * Day + 600, 0));
            Assert.Equal("in 3h", KerbinTime.FromNow(3 * 3600, 0));
            Assert.Equal("in 1d", KerbinTime.FromNow(Day, 0));
        }

        [Fact]
        public void SixHoursIsOneKerbinDayNotQuarterOfAnEarthDay()
        {
            Assert.Equal("in 1d", KerbinTime.FromNow(6 * 3600, 0));
            Assert.Equal("in 5h 59m", KerbinTime.FromNow(6 * 3600 - 60, 0));
        }

        [Fact]
        public void TimesBehindReadAgo()
        {
            Assert.Equal("3h 5m ago", KerbinTime.FromNow(0, 3 * 3600 + 5 * 60));
        }

        [Fact]
        public void UnderAMinuteEitherWayIsNow()
        {
            Assert.Equal("now", KerbinTime.FromNow(100, 100));
            Assert.Equal("now", KerbinTime.FromNow(130, 100));
            Assert.Equal("now", KerbinTime.FromNow(70, 100));
        }

        [Fact]
        public void LongSpansStayInDaysSoNoYearUnitIsNeeded()
        {
            Assert.Equal("in 500d", KerbinTime.FromNow(500 * Day, 0));
        }

        [Fact]
        public void TheDateOfUtZeroIsYear1Day1()
        {
            Assert.Equal("Year 1, Day 1, 00:00", KerbinTime.Date(0));
        }

        [Fact]
        public void TheDateRollsOverAtDay426()
        {
            Assert.Equal("Year 1, Day 426, 05:59", KerbinTime.Date(Year - 60));
            Assert.Equal("Year 2, Day 1, 00:00", KerbinTime.Date(Year));
        }

        [Fact]
        public void TheDateCountsDaysAndTimeOfDay()
        {
            Assert.Equal("Year 2, Day 16, 03:10", KerbinTime.Date(Year + 15 * Day + 3 * 3600 + 10 * 60 + 59));
        }

        [Fact]
        public void SpanOfUnderAMinuteIsSecondsRoundedUp()
        {
            Assert.Equal("30s", KerbinTime.Span(30));
            Assert.Equal("1s", KerbinTime.Span(0.2));
            Assert.Equal("59s", KerbinTime.Span(58.5));
            Assert.Equal("1m", KerbinTime.Span(59.5));
            Assert.Equal("1m", KerbinTime.Span(59.999));
            Assert.Equal("now", KerbinTime.FromNow(30, 0));
        }

        [Fact]
        public void SpanOfZeroIsZeroMinutesAndAMinuteIsOneMinute()
        {
            Assert.Equal("0m", KerbinTime.Span(0));
            Assert.Equal("1m", KerbinTime.Span(60));
        }
    }
}
