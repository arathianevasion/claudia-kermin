// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The test-only kOS bridge's rule (ADR 0006): shut outside a block test, and open only to the test's processor.</summary>
    public sealed class ToolTestGateTests
    {
        [Fact]
        public void The_bridge_is_shut_until_a_block_test_opens_it_and_shut_again_when_it_ends()
        {
            var gate = new ToolTestGate();
            Assert.False(gate.IsOpen);
            Assert.Contains("only while a block test is running", gate.Refusal(42));
            gate.Open(42);
            Assert.Null(gate.Refusal(42));
            gate.Close();
            Assert.Contains("only while a block test is running", gate.Refusal(42));
        }

        [Fact]
        public void During_a_test_only_the_tests_own_processor_may_call()
        {
            var gate = new ToolTestGate();
            gate.Open(42);
            Assert.Contains("only to the processor running the block test", gate.Refusal(7));
        }

        [Fact]
        public void The_shared_gate_starts_shut()
        {
            Assert.False(ToolTestGate.Shared.IsOpen);
        }

        [Fact]
        public void A_kos_lexicon_becomes_the_tools_json_input()
        {
            var input = new Dictionary<string, object>
            {
                { "burns", new List<object> { new Dictionary<string, object> { { "at", "ap" }, { "dv", new Dictionary<string, object> { { "prograde", 12.5 } } } } } },
                { "target", "Mun" },
                { "flag", true },
            };
            Assert.Equal("{\"burns\":[{\"at\":\"ap\",\"dv\":{\"prograde\":12.5}}],\"target\":\"Mun\",\"flag\":true}", ToolTestGate.InputJson(input));
        }

        [Fact]
        public void A_tools_json_result_becomes_plain_values_for_kos_with_null_as_empty()
        {
            var values = ToolTestGate.ResultValues("{\"patches\":[{\"ap\":12.5,\"ends\":\"stable\"}],\"ok\":true,\"burn_time\":null}");
            var patch = (IDictionary<string, object>)((IList<object>)values["patches"])[0];
            Assert.Equal(12.5, patch["ap"]);
            Assert.Equal("stable", patch["ends"]);
            Assert.Equal(true, values["ok"]);
            Assert.Equal("", values["burn_time"]);
        }

        [Fact]
        public void A_timing_line_says_whether_the_call_fit_the_frame_budget()
        {
            Assert.Equal("tool test timing: predict_burn took 0.50 ms (snapshot 2.00 ms) against a frame budget of 1.7 ms: within budget",
                ToolTestGate.TimingLine("predict_burn", 2, 0.5));
            Assert.EndsWith("OVER budget", ToolTestGate.TimingLine("predict_burn", 2, 30));
        }
    }
}
