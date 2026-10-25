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
        /// <summary>The vessel it flies: "" or "craft" for the Mission's craft, otherwise a Child Vessel's name from the Mission Plan.</summary>
        public string Vessel = "";
    }

    /// <summary>A Child Vessel the Mission Plan names: one the staging plan will release, or one that has already separated.</summary>
    public sealed class PlannedChildVessel
    {
        public string Name = "";
        /// <summary>The separation stage that releases it; null for a vessel already separated.</summary>
        public int? SeparationStage;
        /// <summary>An already separated vessel's id, from the mod's separation notice; "" otherwise.</summary>
        public string VesselId = "";
    }

    /// <summary>The ordered Maneuver Sequences Claudia intends to fly to reach the Mission Goal.</summary>
    public sealed class MissionPlan
    {
        public string Why = "";
        public List<PlannedSequence> Sequences = new List<PlannedSequence>();
        /// <summary>The Child Vessels Claudia plans to fly; separated vessels not named here are ignored.</summary>
        public List<PlannedChildVessel> ChildVessels = new List<PlannedChildVessel>();
        public double TotalDeltaV;
        public string ExpectedDuration = "";
        /// <summary>The expected total Mission time in seconds, waits between sequences included; null when Claudia gave none.</summary>
        public double? MissionTimeSeconds;
        /// <summary>The player asked to change the Mission Goal: the new goal in their words, applied on Approval; null when the goal stays.</summary>
        public string NewGoal;
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
        /// <summary>The vessel it flies: "" or "craft" for the Mission's craft, otherwise a Child Vessel's name from the Mission Plan.</summary>
        public string Vessel = "";
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
    /// One Claudia reply in a Mission; <see cref="Kind"/> says which part is filled in. A request sends one of two
    /// structured-output schemas (<see cref="PlanningSchemaJson"/>, <see cref="FlightSchemaJson"/>) with only the
    /// parts it can come back as, and <see cref="Parse"/> reads every part whatever the schema.
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

        // Grammar budget (#199): the API compiles the schema to a grammar, and past an internal size limit every
        // request is a 400 ("compiled grammar is too large"). One schema holding all four parts went over it even
        // with only four unions, so each request sends only the parts it can come back as, in one of two schemas
        // (two, not one per request, because a changed schema costs the prompt cache). Only the parts are nullable;
        // any other "none" is a sentinel (-1, "", "none") that Parse maps back to null. A schema that grows must be
        // probed against the API first (tools/ClaudiaKermin.SchemaProbe); MissionReplySchemaTests holds the budget.
        // Limits: https://platform.claude.com/docs/en/build-with-claude/structured-outputs#schema-complexity-limits

        /// <summary>Every kind, in the schema's order.</summary>
        public static readonly string[] AllKinds = { MissionPlanKind, SequencePlanKind, DebriefKind, AnswerKind, AlertKind };

        // Declared before the schemas below: static fields are initialized in order.
        private static readonly KeyValuePair<string, string>[] Parts =
        {
            new KeyValuePair<string, string>(MissionPlanKind, MissionPlanSchema),
            new KeyValuePair<string, string>(SequencePlanKind, SequencePlanSchema),
            new KeyValuePair<string, string>(DebriefKind, DebriefSchema),
            new KeyValuePair<string, string>(AlertKind, AlertSchema),
        };

        /// <summary>
        /// Mission Plan, Mission Settings, typed replies and changes to a pending plan, and questions before a Mission
        /// Plan is approved: an answer, a Mission Plan, or (a revision of a pending one) a Sequence Plan.
        /// </summary>
        public static readonly string PlanningSchemaJson = SchemaFor(AnswerKind, MissionPlanKind, SequencePlanKind);

        /// <summary>Sequence Plans, Debriefs, Alerts and questions once a Mission Plan is approved.</summary>
        public static readonly string FlightSchemaJson = SchemaFor(AnswerKind, SequencePlanKind, DebriefKind, AlertKind);

        /// <summary>
        /// The structured-output schema for a reply that is one of <paramref name="kinds"/>. Each part but answer is
        /// required, and nullable when there is another kind to choose. Short "why" fields only: the mod never asks
        /// for written-out thinking. No numeric or length limits (structured outputs don't support them).
        /// </summary>
        public static string SchemaFor(params string[] kinds)
        {
            var parts = Parts.Where(p => kinds.Contains(p.Key)).ToList();
            var nullable = kinds.Distinct().Count() > 1;
            var json = new System.Text.StringBuilder();
            json.Append("{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"kind\",\"say\",\"reply_intent\"");
            foreach (var part in parts) json.Append(",\"").Append(part.Key).Append('"');
            json.Append("],\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[")
                .Append(string.Join(",", AllKinds.Where(kinds.Contains).Select(k => "\"" + k + "\"").ToArray())).Append("]},")
                .Append(SayAndIntent);
            foreach (var part in parts)
            {
                json.Append(",\"").Append(part.Key).Append("\":");
                if (nullable) json.Append("{\"anyOf\":[").Append(part.Value).Append(",{\"type\":\"null\"}]}");
                else json.Append(part.Value);
            }
            return json.Append("}}").ToString();
        }

        private const string SayAndIntent =
            "\"say\":{\"type\":\"string\",\"description\":\"Your line to the player: concise mission-control voice, key numbers first, one or two sentences.\"},"
            + "\"reply_intent\":{\"type\":\"string\",\"enum\":[\"none\",\"approve\",\"revise\",\"question\",\"unclear\"],"
            + "\"description\":\"Only when classifying the player's typed reply to a pending plan; otherwise none.\"}";

        private const string MissionPlanSchema =
            "{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"why\",\"sequences\",\"total_delta_v\",\"expected_duration\",\"mission_time_s\",\"new_goal\",\"child_vessels\"],\"properties\":{"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"child_vessels\":{\"type\":\"array\",\"description\":\"The Child Vessels you plan to fly, each named once; separated vessels not named here are ignored.\","
            + "\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"name\",\"separation_stage\",\"vessel_id\"],\"properties\":{"
            + "\"name\":{\"type\":\"string\",\"description\":\"Your name for it, used as the vessel of its sequences.\"},"
            + "\"separation_stage\":{\"type\":\"integer\",\"description\":\"The separation stage that releases it, from the separations facts; -1 for one already separated.\"},"
            + "\"vessel_id\":{\"type\":\"string\",\"description\":\"For a vessel already separated, its id from the mod's notice; otherwise empty.\"}}}},"
            + "\"sequences\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"delta_v\",\"timing\",\"due_time_ut\",\"manual\",\"vessel\"],\"properties\":{"
            + "\"vessel\":{\"type\":\"string\",\"description\":\"craft, or the name of the Child Vessel this sequence flies.\"},"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"timing\":{\"type\":\"string\",\"description\":\"Due Time or rough timing\"},"
            + "\"due_time_ut\":{\"type\":\"number\",\"description\":\"The Due Time as UT in seconds, only when the wait before this sequence needs the player (e.g. docking or an EVA by hand); otherwise -1. A wait for a window, daylight or a hold goes inside the sequence's script, which warps there.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True for a stretch the player flies by hand.\"}}}},"
            + "\"total_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration\":{\"type\":\"string\"},"
            + "\"mission_time_s\":{\"type\":\"number\",\"description\":\"Expected total Mission time in seconds, waits between sequences included; -1 if you can't say.\"},"
            + "\"new_goal\":{\"type\":\"string\",\"description\":\"Only when the player asked to change the Mission Goal itself: the new Mission Goal in their words, which replaces the old one if they approve this plan; otherwise empty.\"}}}";

        private const string SequencePlanSchema =
            "{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"why\",\"target\",\"tolerances\",\"expected_delta_v\",\"expected_duration_s\",\"abort_conditions\",\"telemetry_checks\",\"risks\",\"custom_script\",\"custom_script_reason\",\"script\",\"due_time_ut\",\"window_closes_ut\",\"late_window_dv\",\"manual\",\"vessel\"],"
            + "\"properties\":{"
            + "\"vessel\":{\"type\":\"string\",\"description\":\"craft, or the name of a Child Vessel from the Mission Plan: the script goes only to that vessel, "
            + "over a CommNet path from you, and every other vessel of the Mission must be stable (in orbit, landed or splashed down) when it starts.\"},"
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
            + "\"due_time_ut\":{\"type\":\"number\",\"description\":\"The earliest UT in seconds the sequence may start, only when a wait that needs the player comes first; otherwise -1. The script itself warps through waits for windows, daylight or holds.\"},"
            + "\"window_closes_ut\":{\"type\":\"number\",\"description\":\"UT in seconds after which the planned window is missed, if the sequence depends on one; otherwise -1.\"},"
            + "\"late_window_dv\":{\"type\":\"number\",\"description\":\"Delta-v in m/s of the next window if the planned one is missed; -1 when there is none.\"},"
            + "\"script\":{\"type\":\"string\",\"description\":\"The complete Mission Script in kerboscript; empty for a manual stretch.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True when the player flies this stretch by hand (docking, EVA science, or when they say they will fly it): the script is then empty.\"}}}";

        private const string DebriefSchema =
            "{\"type\":\"object\",\"additionalProperties\":false,"
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
            + "\"follow_up_text\":{\"type\":\"string\",\"description\":\"The corrective sequence or the revised Mission Plan in a few words; for reduced_goal, the reduced Mission Goal itself; otherwise empty.\"}}}";

        private const string AlertSchema =
            "{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"verdict\",\"why\",\"set_follow_up_threshold\",\"follow_up_threshold\"],\"properties\":{"
            + "\"verdict\":{\"type\":\"string\",\"enum\":[\"carry_on\",\"abort\"],\"description\":\"abort recommends the player Abort; you never abort yourself.\"},"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"set_follow_up_threshold\":{\"type\":\"boolean\",\"description\":\"True only with carry_on, when you set follow_up_threshold.\"},"
            + "\"follow_up_threshold\":{\"type\":\"number\",\"description\":\"When set_follow_up_threshold is true: one more threshold for the same check, in its units, for one more Alert; otherwise 0 (ignored).\"}}}";

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
                    MissionTimeSeconds = NonNegative(plan["mission_time_s"]),
                    NewGoal = Goal(JsonNode.Str(plan["new_goal"])),
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
                            DueTimeUt = NonNegative(s["due_time_ut"]),
                            Manual = JsonNode.Bool(s["manual"]) ?? false,
                            Vessel = (JsonNode.Str(s["vessel"]) ?? "").Trim(),
                        });
                    }
                }
                var children = plan["child_vessels"];
                if (children != null)
                {
                    foreach (var c in children.Items.Where(i => i.Kind == JsonKind.Object))
                    {
                        var name = (JsonNode.Str(c["name"]) ?? "").Trim();
                        if (name.Length == 0 || reply.MissionPlan.ChildVessels.Any(v => string.Equals(v.Name, name, System.StringComparison.OrdinalIgnoreCase))) continue;
                        var stage = JsonNode.Long(c["separation_stage"]);
                        reply.MissionPlan.ChildVessels.Add(new PlannedChildVessel
                        {
                            Name = name,
                            SeparationStage = stage.HasValue && stage.Value >= 0 ? (int?)stage.Value : null,
                            VesselId = (JsonNode.Str(c["vessel_id"]) ?? "").Trim(),
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
                    DueTimeUt = NonNegative(sequence["due_time_ut"]),
                    WindowClosesUt = NonNegative(sequence["window_closes_ut"]),
                    LateWindowDeltaV = NonNegative(sequence["late_window_dv"]),
                    Manual = JsonNode.Bool(sequence["manual"]) ?? false,
                    Vessel = (JsonNode.Str(sequence["vessel"]) ?? "").Trim(),
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
                        FollowUpThreshold = verdict == "abort" ? null : FollowUpThreshold(alert),
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

        /// <summary>A UT, delta-v or duration: null when absent, null (saved plans) or the -1 sentinel (any negative).</summary>
        private static double? NonNegative(JsonNode node)
        {
            var value = JsonNode.Double(node);
            return value.HasValue && value.Value >= 0 ? value : null;
        }

        /// <summary>
        /// A threshold can be negative (vertical_speed), so it has a flag instead of a sentinel. A reply without the
        /// flag (one recorded before #199) keeps its threshold when there is one.
        /// </summary>
        private static double? FollowUpThreshold(JsonNode alert)
        {
            var set = JsonNode.Bool(alert["set_follow_up_threshold"]);
            return set == false ? null : JsonNode.Double(alert["follow_up_threshold"]);
        }

        private static string Goal(string text)
        {
            text = (text ?? "").Trim();
            return text.Length == 0 ? null : text;
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
