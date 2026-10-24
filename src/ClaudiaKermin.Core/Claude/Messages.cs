// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Claude
{
    /// <summary>
    /// One content block of a message. The client types the blocks it knows; anything else is an
    /// <see cref="UnknownBlock"/> holding its raw JSON, which is sent back byte for byte.
    /// </summary>
    public abstract class ContentBlock
    {
        internal abstract void Write(JsonWriter json);
    }

    public sealed class TextBlock : ContentBlock
    {
        public string Text = "";

        internal override void Write(JsonWriter json)
        {
            json.BeginObject().Field("type", "text").Field("text", Text).EndObject();
        }
    }

    /// <summary>
    /// Model reasoning. With the default display its text is empty; the signature must go back unchanged.
    /// </summary>
    public sealed class ThinkingBlock : ContentBlock
    {
        public string Thinking = "";
        public string Signature = "";

        internal override void Write(JsonWriter json)
        {
            json.BeginObject().Field("type", "thinking").Field("thinking", Thinking).Field("signature", Signature).EndObject();
        }
    }

    public sealed class ToolUseBlock : ContentBlock
    {
        public string Id;
        public string Name;

        /// <summary>The tool input as JSON text, exactly as streamed ("{}" when nothing was streamed).</summary>
        public string InputJson = "{}";

        /// <summary>False when the streamed input is not complete JSON (e.g. cut off by max_tokens).</summary>
        public bool InputIsValid = true;

        internal override void Write(JsonWriter json)
        {
            json.BeginObject().Field("type", "tool_use").Field("id", Id).Field("name", Name)
                .Name("input").Raw(InputIsValid ? InputJson : "{}").EndObject();
        }
    }

    /// <summary>The mod's answer to a <see cref="ToolUseBlock"/>; sent in a user message.</summary>
    public sealed class ToolResultBlock : ContentBlock
    {
        public string ToolUseId;
        public string Content = "";
        public bool IsError;

        internal override void Write(JsonWriter json)
        {
            json.BeginObject().Field("type", "tool_result").Field("tool_use_id", ToolUseId).Field("content", Content);
            if (IsError) json.Field("is_error", true);
            json.EndObject();
        }
    }

    /// <summary>Marks where a server-side fallback switched models. Sent back as received.</summary>
    public sealed class FallbackBlock : ContentBlock
    {
        public string FromModel;
        public string ToModel;
        internal string RawJson;

        internal override void Write(JsonWriter json)
        {
            json.Raw(RawJson);
        }
    }

    /// <summary>A server-side compaction summary; must be sent back so the API can drop what it replaced.</summary>
    public sealed class CompactionBlock : ContentBlock
    {
        /// <summary>The summary, or null when the API returned none.</summary>
        public string Content;

        internal override void Write(JsonWriter json)
        {
            json.BeginObject().Field("type", "compaction").Field("content", Content).EndObject();
        }
    }

    /// <summary>A block type this client does not know, kept and re-sent exactly as the API sent it.</summary>
    public sealed class UnknownBlock : ContentBlock
    {
        public string Type;
        public string RawJson;

        /// <summary>Deltas the API streamed into this block; the client cannot apply them, so they are only counted.</summary>
        public int IgnoredDeltas;

        internal override void Write(JsonWriter json)
        {
            json.Raw(RawJson);
        }
    }

    public enum Role
    {
        User,
        Assistant,
    }

    public sealed class Message
    {
        public Role Role;
        public readonly List<ContentBlock> Content = new List<ContentBlock>();

        public static Message UserText(string text)
        {
            var message = new Message { Role = Role.User };
            message.Content.Add(new TextBlock { Text = text });
            return message;
        }
    }

    public enum StopReason
    {
        /// <summary>No stop reason arrived.</summary>
        None,
        EndTurn,
        ToolUse,
        MaxTokens,
        Refusal,
        ModelContextWindowExceeded,
        /// <summary>A stop reason outside the typed set; see <see cref="AssistantMessage.RawStopReason"/>.</summary>
        Other,
    }

    /// <summary>One attempt inside a request (a compaction, the message, a fallback model's message).</summary>
    public sealed class UsageIteration
    {
        public string Type;
        /// <summary>The model that ran this attempt, when the API names one.</summary>
        public string Model;
        public long InputTokens;
        public long OutputTokens;
        public long CacheCreationInputTokens;
        public long CacheReadInputTokens;
    }

    public sealed class Usage
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheCreationInputTokens;
        public long CacheReadInputTokens;
        public readonly List<UsageIteration> Iterations = new List<UsageIteration>();
    }

    /// <summary>Claude's reply, assembled from the stream.</summary>
    public sealed class AssistantMessage
    {
        public string Id;
        /// <summary>The model that produced the message (a fallback model when one served it).</summary>
        public string Model;
        public StopReason StopReason;
        public string RawStopReason;
        /// <summary>The refusal category from <c>stop_details</c>, when there is one.</summary>
        public string RefusalCategory;
        public readonly Usage Usage = new Usage();
        public readonly List<ContentBlock> Content = new List<ContentBlock>();

        /// <summary>The text blocks joined, for display.</summary>
        public string Text
        {
            get
            {
                var text = new System.Text.StringBuilder();
                foreach (var block in Content)
                {
                    var t = block as TextBlock;
                    if (t != null) text.Append(t.Text);
                }
                return text.ToString();
            }
        }

        /// <summary>True when a fallback model ran during this request.</summary>
        public bool FallbackRan
        {
            get
            {
                foreach (var iteration in Usage.Iterations)
                    if (iteration.Type == "fallback_message") return true;
                return false;
            }
        }

        public Message ToMessage()
        {
            var message = new Message { Role = Role.Assistant };
            message.Content.AddRange(Content);
            return message;
        }

        public static StopReason ParseStopReason(string raw)
        {
            switch (raw)
            {
                case null: return StopReason.None;
                case "end_turn": return StopReason.EndTurn;
                case "tool_use": return StopReason.ToolUse;
                case "max_tokens": return StopReason.MaxTokens;
                case "refusal": return StopReason.Refusal;
                case "model_context_window_exceeded": return StopReason.ModelContextWindowExceeded;
                default: return StopReason.Other;
            }
        }
    }
}
