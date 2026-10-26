// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Text;
using System.Text.Json;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Scripted tool_use replies in the shape the Messages API streams them (thinking with a signature, then one tool_use
    /// block per call with its input streamed as input_json_delta, then stop_reason tool_use), for the tool loop tests.
    /// </summary>
    internal static class ToolUseStreams
    {
        /// <summary>A reply calling each (name, input JSON) in <paramref name="calls"/>; ids are <c>toolu_&lt;tag&gt;_&lt;n&gt;</c>.</summary>
        public static ScriptedResponse ToolUse(string tag, params (string Name, string Input)[] calls)
        {
            return Build(tag, null, 3000, calls);
        }

        /// <summary>The same, with <paramref name="inputTokens"/> of input (100000 costs $0.20 on Sonnet 5.5), for the Spending Cap tests.</summary>
        public static ScriptedResponse ToolUsePriced(string tag, long inputTokens, params (string Name, string Input)[] calls)
        {
            return Build(tag, null, inputTokens, calls);
        }

        /// <summary>The same, served by a fallback model after the requested one declined (usage.iterations says so).</summary>
        public static ScriptedResponse ToolUseAfterFallback(string tag, params (string Name, string Input)[] calls)
        {
            return Build(tag, "claude-opus-5-5", 3000, calls);
        }

        private static ScriptedResponse Build(string tag, string fallbackModel, long inputTokens, (string Name, string Input)[] calls)
        {
            var sse = new StringBuilder();
            var model = fallbackModel ?? "claude-sonnet-5-5";
            Event(sse, "message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_" + tag + "\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"" + model
                + "\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":" + inputTokens + ",\"output_tokens\":1}}}");
            Event(sse, "content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}");
            Event(sse, "content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"c2lnLXRvb2wt" + tag + "\"}}");
            Event(sse, "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}");
            for (var i = 0; i < calls.Length; i++)
            {
                var index = i + 1;
                Event(sse, "content_block_start", "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_" + tag + "_" + i
                    + "\",\"name\":\"" + calls[i].Name + "\",\"input\":{}}}");
                Event(sse, "content_block_delta", "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":"
                    + JsonSerializer.Serialize(calls[i].Input) + "}}");
                Event(sse, "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":" + index + "}");
            }
            var iterations = fallbackModel == null
                ? "[{\"type\":\"message\",\"input_tokens\":" + inputTokens + ",\"output_tokens\":60}]"
                : "[{\"type\":\"message\",\"model\":\"claude-sonnet-5-5\",\"input_tokens\":3000,\"output_tokens\":0},{\"type\":\"fallback_message\",\"model\":\"" + fallbackModel + "\",\"input_tokens\":3000,\"output_tokens\":60}]";
            Event(sse, "message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":60,\"iterations\":" + iterations + "}}");
            Event(sse, "message_stop", "{\"type\":\"message_stop\"}");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()), ChunkSize = 4096 };
        }

        private static void Event(StringBuilder sse, string name, string data)
        {
            sse.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data).Append("\n\n");
        }
    }
}
