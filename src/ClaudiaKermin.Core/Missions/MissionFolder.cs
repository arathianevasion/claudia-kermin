// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Writes under <c>&lt;save&gt;/ClaudiaKermin/</c>: each Mission's folder
    /// (<c>missions/&lt;id&gt;/</c>) and debug Game Snapshot captures (<c>snapshots/</c>).
    /// </summary>
    internal static class MissionFolder
    {
        public const string TranscriptFile = "transcript.json";

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static string Root(string saveDirectory)
        {
            return Path.Combine(saveDirectory, "ClaudiaKermin");
        }

        public static string For(string saveDirectory, string missionId)
        {
            return Path.Combine(Root(saveDirectory), "missions", missionId);
        }

        /// <summary>
        /// The conversation exactly as it is sent: the frozen system layers and every message, assistant
        /// blocks as the API returned them (unknown blocks byte for byte). Replaced atomically, so a crash
        /// mid-write leaves the previous transcript whole. Every message stays, compacted or not: the on-demand compaction
        /// block in force is kept beside them, byte for byte, with how many of them it stands in for (#268), and the
        /// conversation's input size from the last committed reply.
        /// </summary>
        public static void WriteTranscript(string folder, string missionId, string model, IList<string> system, IEnumerable<Message> messages,
            CompactionBlock compaction = null, int compactedMessages = 0, long contextTokens = 0)
        {
            var json = new JsonWriter().BeginObject()
                .Field("mission_id", missionId)
                .Field("model", model);
            json.Name("system");
            MessagesRequest.WriteSystem(json, system);
            json.Name("messages").BeginArray();
            foreach (var message in messages)
                message.Write(json, message.Content);
            json.EndArray();
            if (compaction != null)
            {
                json.Name("compaction").BeginObject().Field("covers", compactedMessages).Name("block");
                compaction.Write(json);
                json.EndObject();
            }
            if (contextTokens > 0) json.Field("context_tokens", contextTokens);
            json.EndObject();
            ReplaceAtomically(Path.Combine(folder, TranscriptFile), json.ToString());
        }

        public const string EndedFile = "ended.json";

        /// <summary>Records that the player ended the Mission; the rest of the folder is left as it is.</summary>
        public static void WriteEnded(string folder, string missionId, string stage)
        {
            var json = new JsonWriter().BeginObject()
                .Field("mission_id", missionId)
                .Field("ended_by", "player")
                .Field("stage", stage)
                .EndObject();
            ReplaceAtomically(Path.Combine(folder, EndedFile), json.ToString());
        }

        public const string CostLogFile = "cost-log.jsonl";

        /// <summary>
        /// Appends one line to the Mission's cost and event log: a request's pending charge, what it really
        /// cost (refused, cut-off and off-schema replies too), a worst-case charge kept for a cancelled or
        /// lost request, a request refused by the Spending Cap, or a raised cap. Never rewritten.
        /// </summary>
        public static void AppendCostEvent(string folder, string kind, int request, string model, decimal amount, string outcome,
            decimal spent, decimal held, decimal cap)
        {
            Directory.CreateDirectory(folder);
            var json = new JsonWriter().BeginObject()
                .Field("event", kind)
                .Field("request", request)
                .Field("model", model)
                .Name("amount").Raw(Dollars(amount))
                .Field("outcome", outcome)
                .Name("spent").Raw(Dollars(spent))
                .Name("held").Raw(Dollars(held))
                .Name("cap").Raw(Dollars(cap))
                .Field("at", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
                .EndObject();
            File.AppendAllText(Path.Combine(folder, CostLogFile), json + "\n", Utf8);
        }

        private static string Dollars(decimal value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Writes a temporary file beside the target, then swaps it in.</summary>
        public static void ReplaceAtomically(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, Utf8);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>Writes a captured Game Snapshot to a new file and returns its path.</summary>
        public static string WriteSnapshot(string saveDirectory, GameSnapshot snapshot)
        {
            var folder = Path.Combine(Root(saveDirectory), "snapshots");
            Directory.CreateDirectory(folder);
            var stem = "snapshot-ut" + ((long)snapshot.Ut).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(folder, stem + ".json");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(path, snapshot.ToJson(), Utf8);
            return path;
        }
    }
}
