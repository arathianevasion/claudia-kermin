// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The kOS behaviour the Core Library's bundling relies on, checked on kOS.Safe itself.</summary>
    public class HarnessTests
    {
        [Fact]
        public void Runs_a_script_and_captures_what_it_prints()
        {
            var printed = KosHarness.Run("print 1 + 2. local l is lexicon(\"a\", 1). print l:haskey(\"a\").");
            Assert.Equal(new[] { "3", "True" }, printed);
        }

        [Fact]
        public void A_function_declared_further_down_the_file_can_be_called_from_the_top()
        {
            // The bundle puts the Mission Script first and the Core Library after it.
            var printed = KosHarness.Run("@lazyglobal off.\nprint later(2).\nfunction later { parameter x. return x * 10. }");
            Assert.Equal(new[] { "20" }, printed);
        }

        [Fact]
        public void A_lazyglobal_directive_further_down_the_file_is_rejected()
        {
            // So the bundle drops the Core Library files' own @lazyglobal lines.
            Assert.Contains("LAZYGLOBAL", KosHarness.CompileError("@lazyglobal off.\nprint 1.\n@lazyglobal off.\nprint 2."));
        }

        [Fact]
        public void A_compile_error_is_reported()
        {
            Assert.NotNull(KosHarness.CompileError("print (1 + ."));
        }
    }
}
