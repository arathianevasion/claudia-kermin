// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>A client tool. Strict by default, so Claude's input matches the schema exactly.</summary>
    public sealed class ToolDefinition
    {
        public string Name;
        public string Description;
        /// <summary>The JSON Schema as JSON text; needs <c>additionalProperties: false</c> and <c>required</c> when strict.</summary>
        public string InputSchemaJson;
        /// <summary>
        /// False for the Planning Tools (#265): a strict schema counts against the reply schema's grammar budget, so their
        /// schemas go unenforced and the Planning Tools module validates the input itself.
        /// </summary>
        public bool Strict = true;
    }

    /// <summary>
    /// A streaming Messages request: the typed subset the mod uses. Thinking is always adaptive,
    /// tool choice stays auto, server-side fallbacks are always on, and compaction runs at
    /// <see cref="CompactionTriggerTokens"/>.
    /// </summary>
    public sealed class MessagesRequest
    {
        public string Model;
        public int MaxTokens;
        /// <summary>System prompt text blocks (layers), in order; each is a cache breakpoint, so use at most three.</summary>
        public readonly List<string> System = new List<string>();
        public readonly List<Message> Messages = new List<Message>();
        public readonly List<ToolDefinition> Tools = new List<ToolDefinition>();
        /// <summary>low, medium, high, xhigh or max; null leaves the model's default.</summary>
        public string Effort;
        /// <summary>A JSON Schema (as JSON text) for structured output, or null.</summary>
        public string OutputSchemaJson;
        public int CompactionTriggerTokens = 150000;
        /// <summary>Replaces the API's default summarization prompt when compaction runs; null keeps the default.</summary>
        public string CompactionInstructions;

        internal string ToJson()
        {
            var json = new JsonWriter().BeginObject()
                .Field("model", Model)
                .Field("max_tokens", MaxTokens)
                .Field("stream", true)
                .Field("fallbacks", "default");

            json.Name("thinking").BeginObject().Field("type", "adaptive").EndObject();

            // Automatic tail caching with the 1-hour TTL; an explicit breakpoint also sits on the system prompt.
            json.Name("cache_control");
            CacheControl(json);

            if (System.Count > 0)
            {
                json.Name("system");
                WriteSystem(json, System);
            }

            if (Tools.Count > 0)
            {
                json.Name("tools").BeginArray();
                foreach (var tool in Tools)
                {
                    json.BeginObject().Field("name", tool.Name).Field("description", tool.Description)
                        .Name("input_schema").Raw(tool.InputSchemaJson);
                    if (tool.Strict) json.Field("strict", true);
                    json.EndObject();
                }
                json.EndArray();
                json.Name("tool_choice").BeginObject().Field("type", "auto").EndObject();
            }

            if (Effort != null || OutputSchemaJson != null)
            {
                json.Name("output_config").BeginObject();
                if (Effort != null) json.Field("effort", Effort);
                if (OutputSchemaJson != null)
                    json.Name("format").BeginObject().Field("type", "json_schema").Name("schema").Raw(OutputSchemaJson).EndObject();
                json.EndObject();
            }

            json.Name("context_management").BeginObject().Name("edits").BeginArray()
                .BeginObject().Field("type", "compact_20260112")
                .Name("trigger").BeginObject().Field("type", "input_tokens").Field("value", CompactionTriggerTokens).EndObject();
            if (CompactionInstructions != null) json.Field("instructions", CompactionInstructions);
            json.EndObject()
                .EndArray().EndObject();

            json.Name("messages").BeginArray();
            foreach (var message in Messages)
                message.Write(json, Echoed(message));
            json.EndArray();

            return json.EndObject().ToString();
        }

        /// <summary>The <c>system</c> array exactly as requests send it.</summary>
        internal static void WriteSystem(JsonWriter json, IList<string> layers)
        {
            // The API allows four breakpoints; the automatic tail takes one.
            if (layers.Count > 3) throw new System.ArgumentException("at most three system layers can be cached");
            json.BeginArray();
            foreach (var layer in layers)
            {
                // Each layer is its own breakpoint (at most three, plus the automatic tail: four in all).
                json.BeginObject().Field("type", "text").Field("text", layer).Name("cache_control");
                CacheControl(json);
                json.EndObject();
            }
            json.EndArray();
        }

        private static void CacheControl(JsonWriter json)
        {
            json.BeginObject().Field("type", "ephemeral").Field("ttl", "1h").EndObject();
        }

        /// <summary>
        /// The blocks of a message as they go back to the API. After a fallback switched models
        /// mid-output, the declined model's thinking, tool_use and unrecognised blocks before the
        /// last fallback block are left out, as the fallback docs require; everything else is sent unchanged.
        /// </summary>
        internal static IEnumerable<ContentBlock> Echoed(Message message)
        {
            var lastFallback = -1;
            if (message.Role == Role.Assistant)
                for (var i = 0; i < message.Content.Count; i++)
                    if (message.Content[i] is FallbackBlock) lastFallback = i;

            for (var i = 0; i < message.Content.Count; i++)
            {
                var block = message.Content[i];
                if (i < lastFallback && (block is ThinkingBlock || block is ToolUseBlock || block is UnknownBlock)) continue;
                yield return block;
            }
        }
    }
}
