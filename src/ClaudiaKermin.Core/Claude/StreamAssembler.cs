// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>What a stream update tells the listener.</summary>
    public enum StreamUpdateKind
    {
        /// <summary>The stream failed after content and is being retried from scratch: discard what was shown.</summary>
        Restarted,
        BlockStarted,
        TextDelta,
        ThinkingDelta,
        ToolInputDelta,
        BlockStopped,
    }

    public sealed class StreamUpdate
    {
        public StreamUpdateKind Kind;
        public int Index;
        /// <summary>The block's type for <see cref="StreamUpdateKind.BlockStarted"/>.</summary>
        public string BlockType;
        /// <summary>The delta's text for the delta kinds.</summary>
        public string Text;
    }

    /// <summary>The stream sent an <c>error</c> event.</summary>
    internal sealed class StreamErrorEventException : Exception
    {
        public readonly string ErrorType;

        public StreamErrorEventException(string errorType, string message) : base(message)
        {
            ErrorType = errorType;
        }
    }

    /// <summary>A known event whose data is not usable JSON.</summary>
    internal sealed class BadStreamException : Exception
    {
        public BadStreamException(string message) : base(message) { }
    }

    /// <summary>
    /// Builds an <see cref="AssistantMessage"/> from parsed stream events. Unknown events and
    /// deltas are logged and ignored, unknown blocks are kept as raw JSON, and unknown fields are
    /// never read, so new API fields cannot break it.
    /// </summary>
    internal sealed class StreamAssembler
    {
        private readonly Action<StreamUpdate> _listener;
        private readonly Action<string> _ignored;
        private readonly SortedDictionary<int, ContentBlock> _blocks = new SortedDictionary<int, ContentBlock>();
        private readonly Dictionary<int, System.Text.StringBuilder> _toolInputs = new Dictionary<int, System.Text.StringBuilder>();

        public readonly AssistantMessage Message = new AssistantMessage();

        /// <summary>True once any content block has started or streamed: from then on a failure retries only once.</summary>
        public bool ContentStarted { get; private set; }

        public bool Stopped { get; private set; }

        /// <param name="ignored">Told what was ignored, as a short description with no content text.</param>
        public StreamAssembler(Action<StreamUpdate> listener, Action<string> ignored)
        {
            _listener = listener ?? (u => { });
            _ignored = ignored;
        }

        public void Apply(SseEvent sse)
        {
            switch (sse.Name)
            {
                case "ping":
                    return;
                case "message_start":
                    MessageStart(Parse(sse));
                    return;
                case "content_block_start":
                    BlockStart(Parse(sse));
                    return;
                case "content_block_delta":
                    BlockDelta(Parse(sse));
                    return;
                case "content_block_stop":
                    BlockStop(Parse(sse));
                    return;
                case "message_delta":
                    MessageDelta(Parse(sse));
                    return;
                case "message_stop":
                    Finish();
                    return;
                case "error":
                    var error = Parse(sse)["error"];
                    throw new StreamErrorEventException(JsonNode.Str(error != null ? error["type"] : null) ?? "unknown",
                        JsonNode.Str(error != null ? error["message"] : null) ?? "");
                default:
                    _ignored("event " + Safe(sse.Name));
                    return;
            }
        }

        private static JsonNode Parse(SseEvent sse)
        {
            try
            {
                var node = JsonReader.Parse(sse.Data);
                if (node.Kind != JsonKind.Object) throw new BadStreamException(sse.Name + " data is not an object");
                return node;
            }
            catch (JsonException e)
            {
                throw new BadStreamException(sse.Name + " data is not JSON: " + e.Message);
            }
        }

        private void MessageStart(JsonNode data)
        {
            var message = data["message"];
            if (message == null) return;
            Message.Id = JsonNode.Str(message["id"]);
            Message.Model = JsonNode.Str(message["model"]);
            ReadUsage(message["usage"]);
        }

        private void BlockStart(JsonNode data)
        {
            var index = Index(data);
            var block = data["content_block"];
            if (block == null || block.Kind != JsonKind.Object) throw new BadStreamException("content_block_start without a block");
            ContentStarted = true;
            var type = JsonNode.Str(block["type"]);
            ContentBlock typed;
            switch (type)
            {
                case "text":
                    typed = new TextBlock { Text = JsonNode.Str(block["text"]) ?? "" };
                    break;
                case "thinking":
                    typed = new ThinkingBlock
                    {
                        Thinking = JsonNode.Str(block["thinking"]) ?? "",
                        Signature = JsonNode.Str(block["signature"]) ?? "",
                    };
                    break;
                case "tool_use":
                    typed = new ToolUseBlock { Id = JsonNode.Str(block["id"]), Name = JsonNode.Str(block["name"]) };
                    _toolInputs[index] = new System.Text.StringBuilder();
                    break;
                case "fallback":
                    typed = new FallbackBlock
                    {
                        FromModel = JsonNode.Str(block["from"] != null ? block["from"]["model"] : null),
                        ToModel = JsonNode.Str(block["to"] != null ? block["to"]["model"] : null),
                        RawJson = block.Raw,
                    };
                    break;
                case "compaction":
                    typed = new CompactionBlock { Content = JsonNode.Str(block["content"]), RawJson = block.Raw };
                    break;
                default:
                    typed = new UnknownBlock { Type = type, RawJson = block.Raw };
                    _ignored("block type " + Safe(type) + " kept as raw JSON");
                    break;
            }
            _blocks[index] = typed;
            _listener(new StreamUpdate { Kind = StreamUpdateKind.BlockStarted, Index = index, BlockType = type });
        }

        private void BlockDelta(JsonNode data)
        {
            var index = Index(data);
            var delta = data["delta"];
            ContentStarted = true;
            ContentBlock block;
            if (!_blocks.TryGetValue(index, out block))
            {
                _ignored("delta for unstarted block " + index);
                return;
            }
            var type = JsonNode.Str(delta != null ? delta["type"] : null);

            var unknown = block as UnknownBlock;
            if (unknown != null)
            {
                unknown.IgnoredDeltas++;
                _ignored("delta " + Safe(type) + " on raw block");
                return;
            }

            if (type == "text_delta" && block is TextBlock)
            {
                var text = JsonNode.Str(delta["text"]) ?? "";
                ((TextBlock)block).Text += text;
                _listener(new StreamUpdate { Kind = StreamUpdateKind.TextDelta, Index = index, Text = text });
            }
            else if (type == "thinking_delta" && block is ThinkingBlock)
            {
                var text = JsonNode.Str(delta["thinking"]) ?? "";
                ((ThinkingBlock)block).Thinking += text;
                _listener(new StreamUpdate { Kind = StreamUpdateKind.ThinkingDelta, Index = index, Text = text });
            }
            else if (type == "signature_delta" && block is ThinkingBlock)
            {
                ((ThinkingBlock)block).Signature += JsonNode.Str(delta["signature"]) ?? "";
            }
            else if (type == "input_json_delta" && block is ToolUseBlock)
            {
                var part = JsonNode.Str(delta["partial_json"]) ?? "";
                _toolInputs[index].Append(part);
                _listener(new StreamUpdate { Kind = StreamUpdateKind.ToolInputDelta, Index = index, Text = part });
            }
            else if (type == "compaction_delta" && block is CompactionBlock)
            {
                var content = JsonNode.Str(delta["content"]);
                if (content != null)
                {
                    var compaction = (CompactionBlock)block;
                    compaction.Content = (compaction.Content ?? "") + content;
                    compaction.ContentStreamed = true;
                }
            }
            else
            {
                _ignored("delta " + Safe(type));
            }
        }

        private void BlockStop(JsonNode data)
        {
            var index = Index(data);
            ContentBlock block;
            if (!_blocks.TryGetValue(index, out block)) return;
            var tool = block as ToolUseBlock;
            if (tool != null) FinishToolInput(index, tool);
            _listener(new StreamUpdate { Kind = StreamUpdateKind.BlockStopped, Index = index });
        }

        private void FinishToolInput(int index, ToolUseBlock tool)
        {
            System.Text.StringBuilder input;
            if (!_toolInputs.TryGetValue(index, out input)) return;
            _toolInputs.Remove(index);
            var text = input.ToString();
            tool.InputJson = text.Length == 0 ? "{}" : text;
            tool.InputIsValid = JsonReader.IsValid(tool.InputJson);
        }

        private void MessageDelta(JsonNode data)
        {
            var delta = data["delta"];
            if (delta != null)
            {
                var stop = JsonNode.Str(delta["stop_reason"]);
                if (stop != null)
                {
                    Message.RawStopReason = stop;
                    Message.StopReason = AssistantMessage.ParseStopReason(stop);
                }
                var details = delta["stop_details"];
                if (details != null && details.Kind == JsonKind.Object)
                    Message.RefusalCategory = JsonNode.Str(details["category"]);
            }
            ReadUsage(data["usage"]);
        }

        private void Finish()
        {
            // A tool_use cut off without its stop still gets its input checked.
            foreach (var index in _toolInputs.Keys.ToList())
                FinishToolInput(index, (ToolUseBlock)_blocks[index]);
            Message.Content.Clear();
            Message.Content.AddRange(_blocks.Values);
            Stopped = true;
        }

        // message_delta usage is cumulative: any field present replaces the earlier value.
        private void ReadUsage(JsonNode usage)
        {
            if (usage == null || usage.Kind != JsonKind.Object) return;
            var u = Message.Usage;
            u.InputTokens = JsonNode.Long(usage["input_tokens"]) ?? u.InputTokens;
            u.OutputTokens = JsonNode.Long(usage["output_tokens"]) ?? u.OutputTokens;
            u.CacheCreationInputTokens = JsonNode.Long(usage["cache_creation_input_tokens"]) ?? u.CacheCreationInputTokens;
            u.CacheReadInputTokens = JsonNode.Long(usage["cache_read_input_tokens"]) ?? u.CacheReadInputTokens;
            var iterations = usage["iterations"];
            if (iterations == null || iterations.Kind != JsonKind.Array) return;
            u.Iterations.Clear();
            foreach (var item in iterations.Items)
            {
                u.Iterations.Add(new UsageIteration
                {
                    Type = JsonNode.Str(item["type"]),
                    Model = JsonNode.Str(item["model"]),
                    InputTokens = JsonNode.Long(item["input_tokens"]) ?? 0,
                    OutputTokens = JsonNode.Long(item["output_tokens"]) ?? 0,
                    CacheCreationInputTokens = JsonNode.Long(item["cache_creation_input_tokens"]) ?? 0,
                    CacheReadInputTokens = JsonNode.Long(item["cache_read_input_tokens"]) ?? 0,
                });
            }
        }

        private static int Index(JsonNode data)
        {
            var index = JsonNode.Long(data["index"]);
            if (index == null || index < 0 || index > int.MaxValue) throw new BadStreamException("event without a block index");
            return (int)index.Value;
        }

        /// <summary>Server-chosen names go to the log, so keep only short identifier characters.</summary>
        internal static string Safe(string name)
        {
            if (name == null) return "(none)";
            var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.').Take(40).ToArray());
            return safe.Length == 0 ? "(unprintable)" : safe;
        }
    }
}
