// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    public sealed class PlannedSequence
    {
        public string Name = "";
        public string Intent = "";
        /// <summary>m/s.</summary>
        public double DeltaV;
        /// <summary>Due Time or rough timing, in Claudia's words.</summary>
        public string Timing = "";
    }

    /// <summary>The ordered Maneuver Sequences Claudia intends to fly to reach the Mission Goal.</summary>
    public sealed class MissionPlan
    {
        public string Why = "";
        public List<PlannedSequence> Sequences = new List<PlannedSequence>();
        public double TotalDeltaV;
        public string ExpectedDuration = "";
    }

    /// <summary>Claudia's proposal for the next Maneuver Sequence, with its Mission Script.</summary>
    public sealed class SequencePlan
    {
        public string Name = "";
        public string Intent = "";
        public string Why = "";
        public string Target = "";
        public string Tolerances = "";
        public double ExpectedDeltaV;
        public double ExpectedDurationSeconds;
        public List<string> AbortConditions = new List<string>();
        public List<string> Risks = new List<string>();
        /// <summary>The Mission Script (kerboscript). The mod runs it only after Approval.</summary>
        public string Script = "";
    }

    public sealed class Debrief
    {
        /// <summary>Nominal, Off-Nominal or Failed.</summary>
        public string Outcome = "";
        public string Why = "";
        public string WhatHappened = "";
        public bool GoalReached;
        public string Next = "";
    }

    /// <summary>How Claude classified a reply the player typed while a plan awaited Approval.</summary>
    public enum ReplyIntent
    {
        None,
        Approve,
        Revise,
        Question,
        Unclear,
    }

    /// <summary>
    /// One Claudia reply in a Mission. Every Mission turn uses the same structured-output schema, so the
    /// request's <c>output_config</c> never changes and the cached prefix stays intact; <see cref="Kind"/>
    /// says which part is filled in.
    /// </summary>
    internal sealed class MissionReply
    {
        public const string MissionPlanKind = "mission_plan";
        public const string SequencePlanKind = "sequence_plan";
        public const string DebriefKind = "debrief";
        public const string AnswerKind = "answer";

        public string Kind = "";
        /// <summary>Claudia's line for the Capcom strip.</summary>
        public string Say = "";
        public ReplyIntent Intent;
        public MissionPlan MissionPlan;
        public SequencePlan SequencePlan;
        public Debrief Debrief;

        /// <summary>
        /// The structured-output schema. Short "why" fields only: the mod never asks for written-out
        /// thinking. No numeric or length limits (structured outputs don't support them).
        /// </summary>
        public const string SchemaJson =
            "{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"kind\",\"say\",\"reply_intent\",\"mission_plan\",\"sequence_plan\",\"debrief\"],"
            + "\"properties\":{"
            + "\"kind\":{\"type\":\"string\",\"enum\":[\"mission_plan\",\"sequence_plan\",\"debrief\",\"answer\"]},"
            + "\"say\":{\"type\":\"string\",\"description\":\"Your line to the player: concise mission-control voice, key numbers first, one or two sentences.\"},"
            + "\"reply_intent\":{\"anyOf\":[{\"type\":\"string\",\"enum\":[\"approve\",\"revise\",\"question\",\"unclear\"]},{\"type\":\"null\"}],"
            + "\"description\":\"Only when classifying the player's typed reply to a pending plan; otherwise null.\"},"
            + "\"mission_plan\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"why\",\"sequences\",\"total_delta_v\",\"expected_duration\"],\"properties\":{"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"sequences\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"delta_v\",\"timing\"],\"properties\":{"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"timing\":{\"type\":\"string\",\"description\":\"Due Time or rough timing\"}}}},"
            + "\"total_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration\":{\"type\":\"string\"}}},{\"type\":\"null\"}]},"
            + "\"sequence_plan\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"why\",\"target\",\"tolerances\",\"expected_delta_v\",\"expected_duration_s\",\"abort_conditions\",\"risks\",\"script\"],"
            + "\"properties\":{"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"target\":{\"type\":\"string\"},\"tolerances\":{\"type\":\"string\"},"
            + "\"expected_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration_s\":{\"type\":\"number\"},"
            + "\"abort_conditions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"risks\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"script\":{\"type\":\"string\",\"description\":\"The complete Mission Script in kerboscript.\"}}},{\"type\":\"null\"}]},"
            + "\"debrief\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"outcome\",\"why\",\"what_happened\",\"goal_reached\",\"next\"],\"properties\":{"
            + "\"outcome\":{\"type\":\"string\",\"enum\":[\"Nominal\",\"Off-Nominal\",\"Failed\"]},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"what_happened\":{\"type\":\"string\"},"
            + "\"goal_reached\":{\"type\":\"boolean\"},"
            + "\"next\":{\"type\":\"string\"}}},{\"type\":\"null\"}]}"
            + "}}";

        /// <summary>The reply in a message's text, or null when it isn't a reply in this schema.</summary>
        public static MissionReply Parse(string text)
        {
            JsonNode root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
            if (root.Kind != JsonKind.Object) return null;
            var reply = new MissionReply
            {
                Kind = JsonNode.Str(root["kind"]) ?? "",
                Say = JsonNode.Str(root["say"]) ?? "",
                Intent = ParseIntent(JsonNode.Str(root["reply_intent"])),
            };

            var plan = Object(root["mission_plan"]);
            if (plan != null)
            {
                reply.MissionPlan = new MissionPlan
                {
                    Why = JsonNode.Str(plan["why"]) ?? "",
                    TotalDeltaV = JsonNode.Double(plan["total_delta_v"]) ?? 0,
                    ExpectedDuration = JsonNode.Str(plan["expected_duration"]) ?? "",
                };
                var sequences = plan["sequences"];
                if (sequences != null)
                {
                    foreach (var s in sequences.Items.Where(i => i.Kind == JsonKind.Object))
                    {
                        reply.MissionPlan.Sequences.Add(new PlannedSequence
                        {
                            Name = JsonNode.Str(s["name"]) ?? "",
                            Intent = JsonNode.Str(s["intent"]) ?? "",
                            DeltaV = JsonNode.Double(s["delta_v"]) ?? 0,
                            Timing = JsonNode.Str(s["timing"]) ?? "",
                        });
                    }
                }
            }

            var sequence = Object(root["sequence_plan"]);
            if (sequence != null)
            {
                reply.SequencePlan = new SequencePlan
                {
                    Name = JsonNode.Str(sequence["name"]) ?? "",
                    Intent = JsonNode.Str(sequence["intent"]) ?? "",
                    Why = JsonNode.Str(sequence["why"]) ?? "",
                    Target = JsonNode.Str(sequence["target"]) ?? "",
                    Tolerances = JsonNode.Str(sequence["tolerances"]) ?? "",
                    ExpectedDeltaV = JsonNode.Double(sequence["expected_delta_v"]) ?? 0,
                    ExpectedDurationSeconds = JsonNode.Double(sequence["expected_duration_s"]) ?? 0,
                    AbortConditions = Strings(sequence["abort_conditions"]),
                    Risks = Strings(sequence["risks"]),
                    Script = JsonNode.Str(sequence["script"]) ?? "",
                };
            }

            var debrief = Object(root["debrief"]);
            if (debrief != null)
            {
                reply.Debrief = new Debrief
                {
                    Outcome = JsonNode.Str(debrief["outcome"]) ?? "",
                    Why = JsonNode.Str(debrief["why"]) ?? "",
                    WhatHappened = JsonNode.Str(debrief["what_happened"]) ?? "",
                    GoalReached = JsonNode.Bool(debrief["goal_reached"]) ?? false,
                    Next = JsonNode.Str(debrief["next"]) ?? "",
                };
            }
            return reply;
        }

        private static JsonNode Object(JsonNode node)
        {
            return node != null && node.Kind == JsonKind.Object ? node : null;
        }

        private static List<string> Strings(JsonNode node)
        {
            if (node == null) return new List<string>();
            return node.Items.Select(JsonNode.Str).Where(s => s != null).ToList();
        }

        private static ReplyIntent ParseIntent(string intent)
        {
            switch (intent)
            {
                case "approve": return ReplyIntent.Approve;
                case "revise": return ReplyIntent.Revise;
                case "question": return ReplyIntent.Question;
                case "unclear": return ReplyIntent.Unclear;
                default: return ReplyIntent.None;
            }
        }
    }
}
