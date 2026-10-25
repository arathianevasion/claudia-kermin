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
        /// <summary>The earliest UT the sequence may start (a wait longer than the in-sequence cap comes first); null when it can start right away.</summary>
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
            + "\"required\":[\"why\",\"sequences\",\"total_delta_v\",\"expected_duration\",\"mission_time_s\",\"child_vessels\"],\"properties\":{"
            + "\"why\":{\"type\":\"string\",\"description\":\"One short sentence.\"},"
            + "\"child_vessels\":{\"type\":\"array\",\"description\":\"The Child Vessels you plan to fly, each named once; separated vessels not named here are ignored.\","
            + "\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"name\",\"separation_stage\",\"vessel_id\"],\"properties\":{"
            + "\"name\":{\"type\":\"string\",\"description\":\"Your name for it, used as the vessel of its sequences.\"},"
            + "\"separation_stage\":{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"null\"}],\"description\":\"The separation stage that releases it, from the separations facts; null for one already separated.\"},"
            + "\"vessel_id\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}],\"description\":\"For a vessel already separated, its id from the mod's notice; otherwise null.\"}}}},"
            + "\"sequences\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
            + "\"required\":[\"name\",\"intent\",\"delta_v\",\"timing\",\"due_time_ut\",\"manual\",\"vessel\"],\"properties\":{"
            + "\"vessel\":{\"type\":\"string\",\"description\":\"craft, or the name of the Child Vessel this sequence flies.\"},"
            + "\"name\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"},"
            + "\"delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"timing\":{\"type\":\"string\",\"description\":\"Due Time or rough timing\"},"
            + "\"due_time_ut\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"The Due Time as UT in seconds when the wait before this sequence is longer than the in-sequence wait cap; otherwise null.\"},"
            + "\"manual\":{\"type\":\"boolean\",\"description\":\"True for a stretch the player flies by hand.\"}}}},"
            + "\"total_delta_v\":{\"type\":\"number\",\"description\":\"m/s\"},"
            + "\"expected_duration\":{\"type\":\"string\"},"
            + "\"mission_time_s\":{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"null\"}],\"description\":\"Expected total Mission time in seconds, waits between sequences included.\"}}},{\"type\":\"null\"}]},"
            + "\"sequence_plan\":{\"anyOf\":[{\"type\":\"object\",\"additionalProperties\":false,"
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
            + "\"eva_science\":{\"type\":\"string\",\"description\":\"Science only a Kerbal on EVA can do next, as an instruction to the player; empty if none is needed.\"}}},{\"type\":\"null\"}]},"
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
                            SeparationStage = stage.HasValue ? (int?)stage.Value : null,
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
                    DueTimeUt = JsonNode.Double(sequence["due_time_ut"]),
                    WindowClosesUt = JsonNode.Double(sequence["window_closes_ut"]),
                    LateWindowDeltaV = JsonNode.Double(sequence["late_window_dv"]),
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
