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
        /// <summary>The Due Time (UT, s) when the wait before this sequence is longer than the in-sequence cap; null for "right after the previous one".</summary>
        public double? DueTimeUt;
        /// <summary>A stretch the player flies by hand (docking, EVA science): no script, and its Approval gives the player the controls.</summary>
        public bool Manual;
    }

    /// <summary>The ordered Maneuver Sequences Claudia intends to fly to reach the Mission Goal.</summary>
    public sealed class MissionPlan
    {
        public string Why = "";
        public List<PlannedSequence> Sequences = new List<PlannedSequence>();
        public double TotalDeltaV;
        public string ExpectedDuration = "";
        /// <summary>The expected total Mission time in seconds, waits between sequences included; null when Claudia gave none.</summary>
        public double? MissionTimeSeconds;
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
        /// <summary>The script is a Custom Script: written from scratch instead of from Building Blocks.</summary>
        public bool CustomScript;
        /// <summary>Why no Building Block fits, for the Custom Script badge; "" otherwise.</summary>
        public string CustomScriptReason = "";
        /// <summary>The earliest UT the sequence may start (a wait longer than the in-sequence cap comes first); null when it can start right away.</summary>
        public double? DueTimeUt;
        /// <summary>The UT after which the planned window is missed; null when the plan has no window to miss.</summary>
        public double? WindowClosesUt;
        /// <summary>The delta-v (m/s) of the next window if the planned one is missed; null when there is no usable one.</summary>
        public double? LateWindowDeltaV;
        /// <summary>A manual stretch: the player flies it by hand, so there is no script. Approval starts Manual Control.</summary>
        public bool Manual;
    }

    public sealed class Debrief
    {
        /// <summary>Nominal, Off-Nominal or Failed.</summary>
        public string Outcome = "";
        public string Why = "";
        public string WhatHappened = "";
        public bool GoalReached;
        public string Next = "";
        /// <summary>Science only a Kerbal on EVA can do, for the player to do by hand before the Mission carries on; "" if none is needed.</summary>
        public string EvaScience = "";
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
            + "\"required\":[\"why\",\"sequences\",\"total_delta_v\",\"expected_duration\",\"mission_time_s\"],\"properties\":{"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"sequences\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"delta_v\",\"timing\",\"due_time_ut\",\"manual\"],\"properties\":{"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"timing\":{\"type\":\"string\",\"description\":\"Due Time or rough timing\"},"
            + "\"due_time_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"The Due Time as UT in seconds when the wait before this sequence is longer than the in-sequence wait cap; otherwise null.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True for a stretch the player flies by hand.\"}}}},"
            + "\"total_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration\":{\"type\":\"string\"},"
            + "\"mission_time_s\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"Expected total Mission time in seconds, waits between sequences included.\"}}},{\"type\":\"null\"}]},"
            + "\"sequence_plan\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"why\",\"target\",\"tolerances\",\"expected_delta_v\",\"expected_duration_s\",\"abort_conditions\",\"risks\",\"custom_script\",\"custom_script_reason\",\"script\",\"due_time_ut\",\"window_closes_ut\",\"late_window_dv\",\"manual\"],"
            + "\"properties\":{"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"target\":{\"type\":\"string\"},\"tolerances\":{\"type\":\"string\"},"
            + "\"expected_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration_s\":{\"type\":\"number\",\"description\":\"Seconds of game time from start to report, waits included; the mod stops a script still running at 300% of it.\"},"
            + "\"abort_conditions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"risks\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"custom_script\":{\"type\":\"boolean\",\"description\":\"True when the script is written from scratch instead of from Building Blocks.\"},"
            + "\"custom_script_reason\":{\"type\":\"string\",\"description\":\"For a Custom Script, one short sentence on why no Building Block fits; otherwise empty.\"},"
            + "\"due_time_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"The earliest UT in seconds the sequence may start, when a wait longer than the in-sequence wait cap comes first; otherwise null.\"},"
            + "\"window_closes_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"UT in seconds after which the planned window is missed, if the sequence depends on one; otherwise null.\"},"
            + "\"late_window_dv\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"Delta-v in m/s of the next window if the planned one is missed; null when there is none.\"},"
            + "\"script\":{\"type\":\"string\",\"description\":\"The complete Mission Script in kerboscript; empty for a manual stretch.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True when the player flies this stretch by hand (docking, EVA science, or when they say they will fly it): the script is then empty.\"}}},{\"type\":\"null\"}]},"
            + "\"debrief\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"outcome\",\"why\",\"what_happened\",\"goal_reached\",\"next\",\"eva_science\"],\"properties\":{"
            + "\"outcome\":{\"type\":\"string\",\"enum\":[\"Nominal\",\"Off-Nominal\",\"Failed\"]},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"what_happened\":{\"type\":\"string\"},"
            + "\"goal_reached\":{\"type\":\"boolean\"},"
            + "\"next\":{\"type\":\"string\"},"
            + "\"eva_science\":{\"type\":\"string\",\"description\":\"Science only a Kerbal on EVA can do next, as an instruction to the player; empty if none is needed.\"}}},{\"type\":\"null\"}]}"
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
                    MissionTimeSeconds = JsonNode.Double(plan["mission_time_s"]),
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
                            DueTimeUt = JsonNode.Double(s["due_time_ut"]),
                            Manual = JsonNode.Bool(s["manual"]) ?? false,
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
                    CustomScript = JsonNode.Bool(sequence["custom_script"]) ?? false,
                    CustomScriptReason = JsonNode.Str(sequence["custom_script_reason"]) ?? "",
                    DueTimeUt = JsonNode.Double(sequence["due_time_ut"]),
                    WindowClosesUt = JsonNode.Double(sequence["window_closes_ut"]),
                    LateWindowDeltaV = JsonNode.Double(sequence["late_window_dv"]),
                    Manual = JsonNode.Bool(sequence["manual"]) ?? false,
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
                    EvaScience = (JsonNode.Str(debrief["eva_science"]) ?? "").Trim(),
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
