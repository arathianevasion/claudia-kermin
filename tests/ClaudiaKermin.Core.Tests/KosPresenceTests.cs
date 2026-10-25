// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class KosPresenceTests
    {
        private static KeyValuePair<string, Version> Entry(string name, string version)
        {
            return new KeyValuePair<string, Version>(name, new Version(version));
        }

        [Fact]
        public void MissingKosIsReported()
        {
            Assert.Contains("not installed", KosPresence.Check(null));
        }

        [Theory]
        [InlineData("1.4.0.0")]
        [InlineData("1.5.9.0")]
        [InlineData("0.20")]
        public void OlderThan16IsReportedWithItsVersion(string version)
        {
            var v = new Version(version);
            var message = KosPresence.Check(v);

            Assert.Contains("too old", message);
            Assert.Contains("kOS " + v.Major + "." + v.Minor + " ", message);
        }

        [Theory]
        [InlineData("1.6.0.0")]
        [InlineData("1.7.0.0")]
        [InlineData("2.0.0.0")]
        public void SixteenOrNewerIsQuiet(string version)
        {
            Assert.Equal("", KosPresence.Check(new Version(version)));
        }

        [Fact]
        public void FindIgnoresOtherAssembliesAndPicksTheNewestKos()
        {
            var found = KosPresence.Find(new[] { Entry("Other", "9.0"), Entry("kOS", "1.4"), Entry("KOS", "1.6") });

            Assert.Equal(new Version("1.6"), found);
        }

        [Fact]
        public void FindReturnsNullWithoutKos()
        {
            Assert.Null(KosPresence.Find(new[] { Entry("Other", "9.0") }));
        }
    }
}
