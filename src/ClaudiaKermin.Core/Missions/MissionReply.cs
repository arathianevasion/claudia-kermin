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
        /// <summary>The Due Time (UT, s) when the wait before this sequence needs the player; null for "right after the previous one" (ADR 0004).</summary>
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
        /// <summary>Claudia's Telemetry Checks; the config defaults fill the gaps when the sequence runs (<see cref="TelemetryDefaults"/>).</summary>
        public List<TelemetryCheck> TelemetryChecks = new List<TelemetryCheck>();
        /// <summary>The Mission Script (kerboscript). The mod runs it only after Approval.</summary>
        public string Script = "";
        /// <summary>The script is a Custom Script: written from scratch instead of from Building Blocks.</summary>
        public bool CustomScript;
        /// <summary>Why no Building Block fits, for the Custom Script badge; "" otherwise.</summary>
        public string CustomScriptReason = "";
        /// <summary>The earliest UT the sequence may start (a wait that needs the player comes first); null when it can start right away (ADR 0004).</summary>
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
        /// <summary>The outcome against the Sequence Plan's target, from the script's report and the mod's observations.</summary>
        public string OutcomeVsTarget = "";
        /// <summary>The delta-v and resources left against the rest of the Mission Plan.</summary>
        public string PlanMargin = "";
        /// <summary>The science collected, in her words.</summary>
        public string Science = "";
        /// <summary>The Mission Goal as steps, each done or not.</summary>
        public List<GoalItem> Checklist = new List<GoalItem>();
        /// <summary>What she proposes next (see <see cref="FollowUps"/>); "" or "none" when nothing more is proposed.</summary>
        public string FollowUp = "";
        /// <summary>The corrective sequence or revised Mission Plan in a few words, or for "reduced_goal" the reduced Mission Goal itself.</summary>
        public string FollowUpText = "";
    }

    /// <summary>The values of a Debrief's follow_up.</summary>
    public static class FollowUps
    {
        public const string None = "none";
        public const string CorrectiveSequence = "corrective_sequence";
        public const string RevisedPlan = "revised_plan";
        public const string ReducedGoal = "reduced_goal";
        public const string EndMission = "end_mission";
    }

    /// <summary>Claudia's verdict on an Alert: let the sequence carry on, or recommend the player Abort (she never aborts herself).</summary>
    public sealed class AlertVerdict
    {
        public bool Abort;
        public string Why = "";
        /// <summary>After a carry on: one more threshold for the same check, in its units; null for none.</summary>
        public double? FollowUpThreshold;
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
        public const string AlertKind = "alert";

        public string Kind = "";
        /// <summary>Claudia's line for the Capcom strip.</summary>
        public string Say = "";
        public ReplyIntent Intent;
        public MissionPlan MissionPlan;
        public SequencePlan SequencePlan;
        public Debrief Debrief;
        public AlertVerdict Alert;

        /// <summary>
        /// The structured-output schema. Short "why" fields only: the mod never asks for written-out
        /// thinking. No numeric or length limits (structured outputs don't support them).
        /// </summary>
        public const string SchemaJson =
            "{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"kind\",\"say\",\"reply_intent\",\"mission_plan\",\"sequence_plan\",\"debrief\",\"alert\"],"
            + "\"properties\":{"
            + "\"kind\":{\"type\":\"string\",\"enum\":[\"mission_plan\",\"sequence_plan\",\"debrief\",\"answer\",\"alert\"]},"
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
            + "\"due_time_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"The Due Time as UT in seconds, only when the wait before this sequence needs the player (e.g. docking or an EVA by hand); otherwise null. A wait for a window, daylight or a hold goes inside the sequence's script, which warps there.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True for a stretch the player flies by hand.\"}}}},"
            + "\"total_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration\":{\"type\":\"string\"},"
            + "\"mission_time_s\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"Expected total Mission time in seconds, waits between sequences included.\"}}},{\"type\":\"null\"}]},"
            + "\"sequence_plan\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"why\",\"target\",\"tolerances\",\"expected_delta_v\",\"expected_duration_s\",\"abort_conditions\",\"telemetry_checks\",\"risks\",\"custom_script\",\"custom_script_reason\",\"script\",\"due_time_ut\",\"window_closes_ut\",\"late_window_dv\",\"manual\"],"
            + "\"properties\":{"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"target\":{\"type\":\"string\"},\"tolerances\":{\"type\":\"string\"},"
            + "\"expected_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration_s\":{\"type\":\"number\",\"description\":\"Seconds of game time from start to report, waits included; the mod stops a script still running at 300% of it.\"},"
            + "\"abort_conditions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"telemetry_checks\":{\"type\":\"array\",\"description\":\"What the mod watches while the sequence runs; crossing one sends you an Alert. "
            + "Units: delta_v_used, delta_v_left and vertical_speed m/s; duration s since the start; apoapsis, periapsis and altitude m; inclination degrees; "
            + "resource percent of capacity; fuel_margin percent of delta-v left over what the Mission Plan still needs. Config defaults fill any quantity you leave out.\","
            + "\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"quantity\",\"resource\",\"direction\",\"threshold\",\"why\"],\"properties\":{"
            + "\"quantity\":{\"type\":\"string\",\"enum\":[\"delta_v_used\",\"delta_v_left\",\"duration\",\"apoapsis\",\"periapsis\",\"inclination\",\"altitude\",\"vertical_speed\",\"resource\",\"fuel_margin\"]},"
            + "\"resource\":{\"type\":\"string\",\"description\":\"The resource name for quantity resource, e.g. ElectricCharge; otherwise empty.\"},"
            + "\"direction\":{\"type\":\"string\",\"enum\":[\"above\",\"below\"],\"description\":\"Crossed when the quantity goes above or below the threshold.\"},"
            + "\"threshold\":{\"type\":\"number\"},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"}}}},"
            + "\"risks\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
            + "\"custom_script\":{\"type\":\"boolean\",\"description\":\"True when the script is written from scratch instead of from Building Blocks.\"},"
            + "\"custom_script_reason\":{\"type\":\"string\",\"description\":\"For a Custom Script, one short sentence on why no Building Block fits; otherwise empty.\"},"
            + "\"due_time_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"The earliest UT in seconds the sequence may start, only when a wait that needs the player comes first; otherwise null. The script itself warps through waits for windows, daylight or holds.\"},"
            + "\"window_closes_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"UT in seconds after which the planned window is missed, if the sequence depends on one; otherwise null.\"},"
            + "\"late_window_dv\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"Delta-v in m/s of the next window if the planned one is missed; null when there is none.\"},"
            + "\"script\":{\"type\":\"string\",\"description\":\"The complete Mission Script in kerboscript; empty for a manual stretch.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True when the player flies this stretch by hand (docking, EVA science, or when they say they will fly it): the script is then empty.\"}}},{\"type\":\"null\"}]},"
            + "\"debrief\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"outcome\",\"why\",\"what_happened\",\"goal_reached\",\"next\",\"eva_science\",\"outcome_vs_target\",\"plan_margin\",\"science\",\"goal_checklist\",\"follow_up\",\"follow_up_text\"],\"properties\":{"
            + "\"outcome\":{\"type\":\"string\",\"enum\":[\"Nominal\",\"Off-Nominal\",\"Failed\"]},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"what_happened\":{\"type\":\"string\"},"
            + "\"goal_reached\":{\"type\":\"boolean\"},"
            + "\"next\":{\"type\":\"string\"},"
            + "\"eva_science\":{\"type\":\"string\",\"description\":\"Science only a Kerbal on EVA can do next, as an instruction to the player; empty if none is needed.\"},"
            + "\"outcome_vs_target\":{\"type\":\"string\",\"description\":\"The outcome against the Sequence Plan's target and tolerances, from the script's report and the mod's events, with the numbers.\"},"
            + "\"plan_margin\":{\"type\":\"string\",\"description\":\"The delta-v and resources left against the rest of the Mission Plan, using the mod's margin recheck.\"},"
            + "\"science\":{\"type\":\"string\",\"description\":\"The science collected in this sequence and what is still to gain; empty if none.\"},"
            + "\"goal_checklist\":{\"type\":\"array\",\"description\":\"The Mission Goal as its steps, each done or not yet.\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"item\",\"done\"],\"properties\":{\"item\":{\"type\":\"string\"},\"done\":{\"type\":\"boolean\"}}}},"
            + "\"follow_up\":{\"type\":\"string\",\"enum\":[\"none\",\"corrective_sequence\",\"revised_plan\",\"reduced_goal\",\"end_mission\"],"
            + "\"description\":\"Off-Nominal: corrective_sequence or revised_plan. Failed: reduced_goal (the player must approve it) or end_mission. Nominal: none.\"},"
            + "\"follow_up_text\":{\"type\":\"string\",\"description\":\"The corrective sequence or the revised Mission Plan in a few words; for reduced_goal, the reduced Mission Goal itself; otherwise empty.\"}}},{\"type\":\"null\"}]},"
            + "\"alert\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"verdict\",\"why\",\"follow_up_threshold\"],\"properties\":{"
            + "\"verdict\":{\"type\":\"string\",\"enum\":[\"carry_on\",\"abort\"],\"description\":\"abort recommends the player Abort; you never abort yourself.\"},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"follow_up_threshold\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"With carry_on: one more threshold for the same check, in its units, for one more Alert; otherwise null.\"}}},{\"type\":\"null\"}]}"
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
                    TelemetryChecks = Checks(sequence["telemetry_checks"]),
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
                    OutcomeVsTarget = (JsonNode.Str(debrief["outcome_vs_target"]) ?? "").Trim(),
                    PlanMargin = (JsonNode.Str(debrief["plan_margin"]) ?? "").Trim(),
                    Science = (JsonNode.Str(debrief["science"]) ?? "").Trim(),
                    Checklist = Checklist(debrief["goal_checklist"]),
                    FollowUp = (JsonNode.Str(debrief["follow_up"]) ?? "").Trim(),
                    FollowUpText = (JsonNode.Str(debrief["follow_up_text"]) ?? "").Trim(),
                };
            }

            var alert = Object(root["alert"]);
            if (alert != null)
            {
                var verdict = JsonNode.Str(alert["verdict"]);
                if (verdict == "carry_on" || verdict == "abort")
                {
                    reply.Alert = new AlertVerdict
                    {
                        Abort = verdict == "abort",
                        Why = JsonNode.Str(alert["why"]) ?? "",
                        FollowUpThreshold = verdict == "abort" ? null : JsonNode.Double(alert["follow_up_threshold"]),
                    };
                }
            }
            return reply;
        }

        /// <summary>Claudia's Telemetry Checks; one with an unknown quantity, no threshold or no resource name is left out.</summary>
        private static List<TelemetryCheck> Checks(JsonNode node)
        {
            var checks = new List<TelemetryCheck>();
            if (node == null) return checks;
            foreach (var item in node.Items.Where(i => i.Kind == JsonKind.Object))
            {
                var quantity = TelemetryCheck.Parse(JsonNode.Str(item["quantity"]) ?? "");
                var threshold = JsonNode.Double(item["threshold"]);
                var resource = (JsonNode.Str(item["resource"]) ?? "").Trim();
                if (!quantity.HasValue || !threshold.HasValue) continue;
                if (quantity == TelemetryQuantity.Resource && resource.Length == 0) continue;
                checks.Add(new TelemetryCheck
                {
                    Quantity = quantity.Value,
                    Resource = quantity == TelemetryQuantity.Resource ? resource : "",
                    Below = JsonNode.Str(item["direction"]) == "below",
                    Threshold = threshold.Value,
                    Why = JsonNode.Str(item["why"]) ?? "",
                });
            }
            return checks;
        }

        private static List<GoalItem> Checklist(JsonNode node)
        {
            var items = new List<GoalItem>();
            if (node == null) return items;
            foreach (var item in node.Items.Where(i => i.Kind == JsonKind.Object))
            {
                var text = (JsonNode.Str(item["item"]) ?? "").Trim();
                if (text.Length > 0) items.Add(new GoalItem { Item = text, Done = JsonNode.Bool(item["done"]) ?? false });
            }
            return items;
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
