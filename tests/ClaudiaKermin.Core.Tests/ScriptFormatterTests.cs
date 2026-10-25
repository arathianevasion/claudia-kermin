// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public sealed class ScriptFormatterTests
    {
        [Fact]
        public void AOneLineScriptWithNestedBlocksGetsOneStatementPerLineAndTwoSpaceIndents()
        {
            var raw = "set x to 1. until x > 3 { if x = 2 { print x. } else { print 0. } set x to x + 1. }";
            var expected =
                "set x to 1.\n" +
                "until x > 3 {\n" +
                "  if x = 2 {\n" +
                "    print x.\n" +
                "  } else {\n" +
                "    print 0.\n" +
                "  }\n" +
                "  set x to x + 1.\n" +
                "}";
            Assert.Equal(expected, ScriptFormatter.Format(raw));
        }

        [Fact]
        public void BracesAndDotsInStringsAndCommentsAreNotTouched()
        {
            var raw = "print \"a { b. c }\". // not { a block.\nif true { print \"}\". }";
            var expected =
                "print \"a { b. c }\". // not { a block.\n" +
                "if true {\n" +
                "  print \"}\".\n" +
                "}";
            Assert.Equal(expected, ScriptFormatter.Format(raw));
        }

        [Fact]
        public void AnAlreadyFormattedScriptIsLeftUnchangedBlankLinesIncluded()
        {
            var raw = "// launch\nlock throttle to 1.\n\nuntil ship:altitude > 1000 {\n  print \"up\".\n\n  wait 0.5.\n}\n";
            Assert.Equal(raw, ScriptFormatter.Format(raw));
        }

        [Fact]
        public void CrlfLineEndingsAreKeptAndDecimalPointsAreNotStatementEnds()
        {
            Assert.Equal("wait 0.5. print 1.".Replace(". print", ".\r\nprint") + "\r\n", ScriptFormatter.Format("wait 0.5. print 1.\r\n"));
        }

        [Fact]
        public void AMalformedScriptFallsBackToTheRawText()
        {
            Assert.Equal("if x { print 1.", ScriptFormatter.Format("if x { print 1."));
            Assert.Equal("print 1. } {", ScriptFormatter.Format("print 1. } {"));
            Assert.Equal("print \"open. set x to 1.", ScriptFormatter.Format("print \"open. set x to 1."));
        }
    }
}
