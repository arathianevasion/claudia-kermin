// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

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
        /// mid-write leaves the previous transcript whole.
        /// </summary>
        public static void WriteTranscript(string folder, string missionId, string model, IList<string> system, IEnumerable<Message> messages)
        {
            var json = new JsonWriter().BeginObject()
                .Field("mission_id", missionId)
                .Field("model", model);
            json.Name("system");
            MessagesRequest.WriteSystem(json, system);
            json.Name("messages").BeginArray();
            foreach (var message in messages)
                message.Write(json, message.Content);
            json.EndArray().EndObject();
            ReplaceAtomically(Path.Combine(folder, TranscriptFile), json.ToString());
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
