// PROTOTYPE: throwaway Claude API round-trip spike. Answers Claude API round-trip spike in KSP (issue #21).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SimpleJSON;
using UnityEngine;

namespace ClaudiaApiSpike
{
    public static class SpikeLog
    {
        public static readonly List<string> Lines = new List<string>();

        // Main thread only. Worker threads queue through StreamRun.
        public static void Log(string msg)
        {
            UnityEngine.Debug.Log("[ClaudiaApiSpike] " + msg);
            Lines.Add(msg);
            if (Lines.Count > 300) Lines.RemoveAt(0);
        }
    }

    public static class Api
    {
        public const string Url = "https://api.anthropic.com/v1/messages";
        public const string Version = "2026-10-21";
        public const string FallbackBeta = "server-side-fallback-2026-07-01";
        public const string Model = "claude-opus-5-5";

        public static string KeyPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/ClaudiaApiSpike/PluginData/api_key.txt"); }
        }

        // Never logged. Only its length and prefix shape are reported.
        public static string LoadKey()
        {
            if (!File.Exists(KeyPath)) return null;
            return File.ReadAllText(KeyPath).Trim();
        }

        public const string StreamPrompt =
            "Count from 1 to 40, one number per line. After each number write one true fact about spaceflight in about ten words.";

        public static string Body(string prompt, int maxTokens, string bigSystem)
        {
            var root = new JSONObject();
            root["model"] = Model;
            root["max_tokens"] = maxTokens;
            root["stream"] = true;
            root["fallbacks"] = "default";
            var oc = new JSONObject();
            oc["effort"] = "low";
            root["output_config"] = oc;
            if (bigSystem != null)
            {
                var sys = new JSONArray();
                var block = new JSONObject();
                block["type"] = "text";
                block["text"] = bigSystem;
                var cc = new JSONObject();
                cc["type"] = "ephemeral";
                cc["ttl"] = "1h";
                block["cache_control"] = cc;
                sys.Add(block);
                root["system"] = sys;
            }
            var msgs = new JSONArray();
            var m = new JSONObject();
            m["role"] = "user";
            m["content"] = prompt;
            msgs.Add(m);
            root["messages"] = msgs;
            return root.ToString();
        }

        // About 12K tokens of stable text, enough to pass the minimum cacheable prefix.
        public static string BigSystem()
        {
            var sb = new StringBuilder("You are a test fixture. The reference table below is filler for a prompt-caching test; do not summarise it.\n");
            for (int i = 1; i <= 600; i++)
                sb.AppendFormat("Reference line {0}: body K{1} orbit altitude {2} m, transfer delta-v {3} m/s, return delta-v {4} m/s.\n",
                    i, i % 17, 70000 + i * 137, 800 + (i * 31) % 700, 600 + (i * 53) % 900);
            return sb.ToString();
        }
    }

    // One request's streamed result: SSE parsing, timing and a summary. Fed bytes or lines by a transport.
    public class StreamRun
    {
        public readonly string Label;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly StringBuilder Text = new StringBuilder();
        public readonly List<long> ChunkMs = new List<long>();
        public long FirstTextMs = -1;
        public bool Done;
        public string Outcome = "running";
        public string RequestId = "";
        public long HttpStatus;

        private readonly Decoder utf8 = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder pending = new StringBuilder();
        private readonly StringBuilder rawBody = new StringBuilder();
        private string eventName;
        private readonly StringBuilder data = new StringBuilder();
        private int textDeltas, thinkingBlocks, events;
        private string stopReason = "", model = "";
        private JSONNode usage;

        public StreamRun(string label) { Label = label; }

        public void OnBytes(byte[] buf, int len)
        {
            ChunkMs.Add(Clock.ElapsedMilliseconds);
            var chars = new char[utf8.GetCharCount(buf, 0, len)];
            utf8.GetChars(buf, 0, len, chars, 0);
            pending.Append(chars);
            string all = pending.ToString();
            int nl;
            while ((nl = all.IndexOf('\n')) >= 0)
            {
                OnLine(all.Substring(0, nl).TrimEnd('\r'));
                all = all.Substring(nl + 1);
            }
            pending.Length = 0;
            pending.Append(all);
        }

        // HttpWebRequest delivers whole lines with their arrival time.
        public void OnLine(string line, long ms)
        {
            ChunkMs.Add(ms);
            OnLine(line);
        }

        private void OnLine(string line)
        {
            if (rawBody.Length < 4000) rawBody.Append(line).Append('\n');
            if (line.Length == 0) { Dispatch(); return; }
            if (line.StartsWith("event:")) eventName = line.Substring(6).Trim();
            else if (line.StartsWith("data:")) data.Append(line.Substring(5).Trim());
        }

        private void Dispatch()
        {
            if (data.Length == 0) return;
            events++;
            JSONNode d = JSON.Parse(data.ToString());
            data.Length = 0;
            string type = d["type"];
            switch (type)
            {
                case "message_start":
                    model = d["message"]["model"];
                    usage = d["message"]["usage"];
                    break;
                case "content_block_start":
                    if (d["content_block"]["type"] == "thinking") thinkingBlocks++;
                    break;
                case "content_block_delta":
                    if (d["delta"]["type"] == "text_delta")
                    {
                        if (FirstTextMs < 0) FirstTextMs = Clock.ElapsedMilliseconds;
                        textDeltas++;
                        Text.Append((string)d["delta"]["text"]);
                    }
                    break;
                case "message_delta":
                    stopReason = d["delta"]["stop_reason"];
                    if (usage == null) usage = new JSONObject();
                    foreach (var kv in d["usage"]) usage[kv.Key] = kv.Value;
                    break;
                case "error":
                    Outcome = "STREAM ERROR " + d["error"].ToString();
                    break;
            }
            eventName = null;
        }

        public void Finish(string outcome)
        {
            if (Done) return;
            Done = true;
            if (pending.Length > 0) OnLine(pending.ToString());
            Dispatch();
            if (Outcome == "running") Outcome = outcome;
            SpikeLog.Log(Summary());
        }

        public string Summary()
        {
            long maxGap = 0;
            for (int i = 1; i < ChunkMs.Count; i++) maxGap = Math.Max(maxGap, ChunkMs[i] - ChunkMs[i - 1]);
            long first = ChunkMs.Count > 0 ? ChunkMs[0] : -1, last = ChunkMs.Count > 0 ? ChunkMs[ChunkMs.Count - 1] : -1;
            var sb = new StringBuilder();
            sb.AppendFormat("END {0}: {1} | http={2} request-id={3} model={4}\n", Label, Outcome, HttpStatus, RequestId, model);
            sb.AppendFormat("  timing ms: first chunk={0} first text={1} last chunk={2} total={3} | chunks={4} max gap={5}\n",
                first, FirstTextMs, last, Clock.ElapsedMilliseconds, ChunkMs.Count, maxGap);
            sb.AppendFormat("  sse events={0} text deltas={1} thinking blocks={2} stop_reason={3} text chars={4}\n",
                events, textDeltas, thinkingBlocks, stopReason, Text.Length);
            sb.AppendFormat("  usage: {0}\n", usage == null ? "none" : usage.ToString());
            sb.AppendFormat("  chunk arrival ms: {0}\n", string.Join(" ", ChunkMs.Take(80).Select(x => x.ToString()).ToArray()));
            if (events == 0 || HttpStatus >= 400) sb.AppendFormat("  raw body: {0}\n", rawBody.ToString().Replace("\n", " ⏎ "));
            else sb.AppendFormat("  text tail: {0}", Tail(Text.ToString(), 200).Replace("\n", " ⏎ "));
            return sb.ToString();
        }

        public static string Tail(string s, int n) { return s.Length <= n ? s : "…" + s.Substring(s.Length - n); }
    }
}
