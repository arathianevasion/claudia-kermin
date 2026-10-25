// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>What a Mission's folder remembers of how it started; written once, for a Mission whose save has no snapshot.</summary>
    internal sealed class MissionInfo
    {
        public string Id = "";
        public string Goal = "";
        public string Model = "";
        public decimal SpendingCap;
    }

    /// <summary>A turn that was sent and not yet answered, as the Mission folder holds it.</summary>
    internal sealed class UnansweredTurn
    {
        public string Purpose = "";
        public string Effort;
        public string UserText = "";
        public string Notice = "";
        /// <summary>A rewrite after a failed Script Check, so a re-sent one gets no further automatic rewrite.</summary>
        public bool ScriptRewrite;
        /// <summary>The turn waits at the Spending Cap instead of Pausing the Mission (the waiting call).</summary>
        public bool Waits;
        /// <summary>How many messages the committed conversation held when the turn was sent; the turn is stale once the transcript has grown past it.</summary>
        public int BaseMessages;
    }

    /// <summary>What a Mission has cost so far, as its cost log says.</summary>
    internal sealed class CostTotals
    {
        public decimal Spent;
        public decimal Held;
        public int Requests;
        /// <summary>The log exists but could not be read: the totals are unknown, not zero.</summary>
        public bool Unreadable;
    }

    /// <summary>The transcript as read back from the Mission folder: the frozen system layers and every message.</summary>
    internal sealed class StoredTranscript
    {
        public readonly List<string> System = new List<string>();
        public readonly List<Message> Messages = new List<Message>();
    }

    /// <summary>
    /// The Mission folder's persistence files, beside <see cref="MissionFolder"/>'s transcript. Nothing here is
    /// ever rewound by loading a save: <c>events.jsonl</c> is append-only, <c>mission.json</c> is written once, and
    /// the unanswered turn is the only file removed (when its reply is committed).
    /// </summary>
    internal static class MissionStore
    {
        public const string InfoFile = "mission.json";
        public const string UnansweredFile = "unanswered-turn.json";
        public const string EventsFile = "events.jsonl";
        public const string FinishedFile = "finished.json";

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static void WriteInfo(string folder, MissionInfo info)
        {
            var json = new JsonWriter().BeginObject()
                .Field("mission_id", info.Id)
                .Field("goal", info.Goal)
                .Field("model", info.Model)
                .Field("spending_cap", info.SpendingCap.ToString(CultureInfo.InvariantCulture))
                .EndObject();
            MissionFolder.ReplaceAtomically(Path.Combine(folder, InfoFile), json.ToString());
        }

        public static MissionInfo ReadInfo(string folder)
        {
            var root = ReadJson(Path.Combine(folder, InfoFile));
            if (root == null) return null;
            var info = new MissionInfo
            {
                Id = JsonNode.Str(root["mission_id"]) ?? "",
                Goal = JsonNode.Str(root["goal"]) ?? "",
                Model = JsonNode.Str(root["model"]) ?? "",
            };
            decimal cap;
            if (decimal.TryParse(JsonNode.Str(root["spending_cap"]), NumberStyles.Number, CultureInfo.InvariantCulture, out cap)) info.SpendingCap = cap;
            return info.Id.Length == 0 ? null : info;
        }

        public static void WriteUnanswered(string folder, UnansweredTurn turn)
        {
            var json = new JsonWriter().BeginObject()
                .Field("purpose", turn.Purpose)
                .Field("effort", turn.Effort)
                .Field("user_text", turn.UserText)
                .Field("notice", turn.Notice)
                .Field("script_rewrite", turn.ScriptRewrite)
                .Field("waits", turn.Waits)
                .Field("base_messages", turn.BaseMessages)
                .EndObject();
            MissionFolder.ReplaceAtomically(Path.Combine(folder, UnansweredFile), json.ToString());
        }

        public static UnansweredTurn ReadUnanswered(string folder)
        {
            var root = ReadJson(Path.Combine(folder, UnansweredFile));
            if (root == null) return null;
            return new UnansweredTurn
            {
                Purpose = JsonNode.Str(root["purpose"]) ?? "",
                Effort = JsonNode.Str(root["effort"]),
                UserText = JsonNode.Str(root["user_text"]) ?? "",
                Notice = JsonNode.Str(root["notice"]) ?? "",
                ScriptRewrite = JsonNode.Bool(root["script_rewrite"]) ?? false,
                Waits = JsonNode.Bool(root["waits"]) ?? false,
                BaseMessages = (int)(JsonNode.Long(root["base_messages"]) ?? 0),
            };
        }

        public static void DeleteUnanswered(string folder)
        {
            var path = Path.Combine(folder, UnansweredFile);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Appends one line to the Mission's event log. Never rewritten, never rewound.</summary>
        public static void AppendEvent(string folder, double ut, string kind, string text)
        {
            var line = new JsonWriter().BeginObject()
                .Field("ut", ut)
                .Field("event", kind)
                .Field("text", text)
                .EndObject().ToString();
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, EventsFile), line + "\n", Utf8);
        }

        /// <summary>Records that the Mission ran its course (goal met, or a Failed Debrief).</summary>
        public static void WriteFinished(string folder, string missionId, string stage)
        {
            var json = new JsonWriter().BeginObject()
                .Field("mission_id", missionId)
                .Field("stage", stage)
                .EndObject();
            MissionFolder.ReplaceAtomically(Path.Combine(folder, FinishedFile), json.ToString());
        }

        public const string VesselFile = "vessel.json";

        /// <summary>The Mission's craft as last known, kept in the folder so the Missions list can switch to it whatever the save holds.</summary>
        public static void WriteVessel(string folder, string vesselId, string vesselName)
        {
            if (string.IsNullOrEmpty(vesselId)) return;
            var json = new JsonWriter().BeginObject().Field("vessel_id", vesselId).Field("vessel_name", vesselName ?? "").EndObject();
            MissionFolder.ReplaceAtomically(Path.Combine(folder, VesselFile), json.ToString());
        }

        public static void ReadVessel(string folder, out string vesselId, out string vesselName)
        {
            var root = ReadJson(Path.Combine(folder, VesselFile));
            vesselId = root == null ? "" : JsonNode.Str(root["vessel_id"]) ?? "";
            vesselName = root == null ? "" : JsonNode.Str(root["vessel_name"]) ?? "";
        }

        /// <summary>How a Mission that is over finished: Succeeded or Lost from <c>finished.json</c>, Ended for a Failed Debrief or the player ending it; null when it is not over.</summary>
        public static MissionStage? FinishedStage(string folder)
        {
            var root = ReadJson(Path.Combine(folder, FinishedFile));
            if (root != null)
            {
                MissionStage stage;
                if (Enum.TryParse(JsonNode.Str(root["stage"]) ?? "", out stage) && (stage == MissionStage.Succeeded || stage == MissionStage.Lost)) return stage;
                return MissionStage.Ended;
            }
            return File.Exists(Path.Combine(folder, MissionFolder.EndedFile)) ? MissionStage.Ended : (MissionStage?)null;
        }

        /// <summary>The player ended it, or it finished: a read-only history entry rather than something to resume.</summary>
        public static bool IsOver(string folder)
        {
            return File.Exists(Path.Combine(folder, MissionFolder.EndedFile)) || File.Exists(Path.Combine(folder, FinishedFile));
        }

        /// <summary>The newest Mission folder in the save that has not finished and can be resumed, or null.</summary>
        public static MissionInfo NewestResumable(string saveDirectory)
        {
            var missions = Path.Combine(MissionFolder.Root(saveDirectory), "missions");
            if (!Directory.Exists(missions)) return null;
            // Ids start with a UTC timestamp, so name order is start order.
            foreach (var folder in Directory.GetDirectories(missions).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                if (IsOver(folder)) continue;
                var info = ReadInfo(folder);
                if (info != null) return info;
            }
            return null;
        }

        /// <summary>
        /// Spent and Held as the cost log last recorded them: the log is append-only and never rewound, so it is
        /// the truth whatever save is loaded. A pending charge with no settlement after it (a crash mid-request)
        /// counts as kept. A torn last line (a crash mid-append) is skipped. Zero when there is no log; Unreadable when
        /// there is one that can't be read (an IO or access error), which is not zero spend.
        /// </summary>
        public static CostTotals ReadCostTotals(string folder)
        {
            var totals = new CostTotals();
            var path = Path.Combine(folder, MissionFolder.CostLogFile);
            if (!File.Exists(path)) return totals;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Utf8);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                totals.Unreadable = true;
                return totals;
            }
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                JsonNode line;
                try
                {
                    line = JsonReader.Parse(lines[i]);
                }
                catch (JsonException)
                {
                    continue;
                }
                decimal spent, held, amount;
                if (line.Kind != JsonKind.Object || !TryDecimal(line["spent"], out spent) || !TryDecimal(line["held"], out held)) continue;
                totals.Spent = spent;
                totals.Held = held;
                totals.Requests = (int)(JsonNode.Long(line["request"]) ?? 0);
                if (JsonNode.Str(line["event"]) == "pending" && TryDecimal(line["amount"], out amount)) totals.Held += amount;
                return totals;
            }
            return totals;
        }

        private static bool TryDecimal(JsonNode node, out decimal value)
        {
            value = 0m;
            return node != null && node.Kind == JsonKind.Number && decimal.TryParse(node.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The transcript the Mission folder holds, or null when there is none or it is unreadable.</summary>
        public static StoredTranscript ReadTranscript(string folder)
        {
            var root = ReadJson(Path.Combine(folder, MissionFolder.TranscriptFile));
            if (root == null) return null;
            var transcript = new StoredTranscript();
            var system = root["system"];
            if (system != null)
                foreach (var layer in system.Items)
                    transcript.System.Add(JsonNode.Str(layer["text"]) ?? "");
            var messages = root["messages"];
            if (messages != null)
                foreach (var node in messages.Items)
                {
                    var message = ReadMessage(node);
                    if (message != null) transcript.Messages.Add(message);
                }
            return transcript;
        }

        private static Message ReadMessage(JsonNode node)
        {
            switch (JsonNode.Str(node["role"]))
            {
                case "system":
                    var config = node["output_config"];
                    if (config != null) return Message.SystemEffort(JsonNode.Str(config["effort"]));
                    return Message.SystemText(JsonNode.Str(node["content"]) ?? "");
                case "user":
                case "assistant":
                    var message = new Message { Role = JsonNode.Str(node["role"]) == "user" ? Role.User : Role.Assistant };
                    var content = node["content"];
                    if (content != null)
                        foreach (var block in content.Items) message.Content.Add(ReadBlock(block));
                    return message;
                default:
                    return null;
            }
        }

        /// <summary>
        /// A block as the transcript holds it. The blocks the client types are rebuilt (the writer produces the
        /// same bytes again); anything else, a fallback marker included, is kept as raw JSON and sent back as is.
        /// </summary>
        private static ContentBlock ReadBlock(JsonNode node)
        {
            var type = JsonNode.Str(node["type"]) ?? "";
            switch (type)
            {
                case "text":
                    return new TextBlock { Text = JsonNode.Str(node["text"]) ?? "" };
                case "thinking":
                    return new ThinkingBlock { Thinking = JsonNode.Str(node["thinking"]) ?? "", Signature = JsonNode.Str(node["signature"]) ?? "" };
                case "tool_use":
                    var input = node["input"];
                    return new ToolUseBlock { Id = JsonNode.Str(node["id"]), Name = JsonNode.Str(node["name"]), InputJson = input != null ? input.Raw : "{}" };
                case "tool_result":
                    return new ToolResultBlock { ToolUseId = JsonNode.Str(node["tool_use_id"]), Content = JsonNode.Str(node["content"]) ?? "", IsError = JsonNode.Bool(node["is_error"]) ?? false };
                case "compaction":
                    return new CompactionBlock { Content = JsonNode.Str(node["content"]), RawJson = node.Raw };
                default:
                    return new UnknownBlock { Type = type, RawJson = node.Raw };
            }
        }

        private static JsonNode ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var root = JsonReader.Parse(File.ReadAllText(path, Utf8));
                return root.Kind == JsonKind.Object ? root : null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
