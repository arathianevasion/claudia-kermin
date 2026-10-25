// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The structured-output schemas stay small enough for the API to compile (#199): one MissionReply schema with
    /// every part was rejected on every request as "compiled grammar is too large", at 14 unions and still at 4.
    /// </summary>
    public class MissionReplySchemaTests
    {
        // The API's grammar limit is internal (https://platform.claude.com/docs/en/build-with-claude/structured-outputs#schema-complexity-limits),
        // so these budgets come from probing it (tools/ClaudiaKermin.SchemaProbe, claude-haiku-5-5). By the
        // SchemaComplexity.Size proxy, size 75 (11 unions, before #65) compiled and 89 (12 unions, main before the Child
        // Vessels merge) worked in play; 95 (14 unions) and 97 (4 unions, every part with sentinels) were refused. Raise a
        // budget only after the probe shows the bigger schema, plus one more nullable field, compiles.

        /// <summary>The flight schema's three nullable parts (sequence_plan, debrief, alert).</summary>
        private const int UnionBudget = 3;

        /// <summary>Below the 89 that compiled, with room under the 95 that did not.</summary>
        private const int SizeBudget = 85;

        public static TheoryData<string, string> Schemas()
        {
            return new TheoryData<string, string>
            {
                { "planning", MissionReply.PlanningSchemaJson },
                { "flight", MissionReply.FlightSchemaJson },
                { "craft check", CraftCheckRules.SchemaJson() },
            };
        }

        [Theory]
        [MemberData(nameof(Schemas))]
        public void EverySchemaSentStaysWithinTheGrammarBudget(string name, string schema)
        {
            Assert.True(SchemaComplexity.Unions(schema) <= UnionBudget, name + ": " + SchemaComplexity.Unions(schema) + " unions");
            Assert.True(SchemaComplexity.Size(schema) <= SizeBudget, name + ": size " + SchemaComplexity.Size(schema));
            Assert.Equal(0, SchemaComplexity.OptionalParameters(schema));
        }

        [Fact]
        public void CraftCheckRulesHaveNoUnions()
        {
            Assert.Equal(0, SchemaComplexity.Unions(CraftCheckRules.SchemaJson()));
        }

        [Fact]
        public void EveryPartTogetherIsOverTheBudget()
        {
            // Why there are two schemas: the one #199 found refused, even with sentinels.
            Assert.True(SchemaComplexity.Size(MissionReply.SchemaFor(MissionReply.AllKinds)) > SizeBudget);
        }

        [Fact]
        public void EachSchemaOffersTheKindsItsRequestsCanComeBackAs()
        {
            Assert.Equal(new[] { "mission_plan", "sequence_plan", "answer" }, Kinds(MissionReply.PlanningSchemaJson));
            Assert.Equal(new[] { "sequence_plan", "debrief", "answer", "alert" }, Kinds(MissionReply.FlightSchemaJson));
            var planning = JsonReader.Parse(MissionReply.PlanningSchemaJson);
            Assert.Equal(new[] { "kind", "say", "reply_intent", "mission_plan", "sequence_plan" }, planning["required"].Items.Select(JsonNode.Str).ToArray());
        }

        [Fact]
        public void ASingleKindIsNotNullable()
        {
            var schema = MissionReply.SchemaFor(MissionReply.AlertKind);
            Assert.Equal(0, SchemaComplexity.Unions(schema));
            Assert.Equal("object", JsonNode.Str(JsonReader.Parse(schema)["properties"]["alert"]["type"]));
        }

        private static string[] Kinds(string schema)
        {
            return JsonReader.Parse(schema)["properties"]["kind"]["enum"].Items.Select(JsonNode.Str).ToArray();
        }

        [Fact]
        public void TheSizeCountsPropertiesAndEnumValuesAtAnyDepth()
        {
            const string schema = "{\"type\":\"object\",\"properties\":{"
                + "\"a\":{\"anyOf\":[{\"type\":\"object\",\"properties\":{\"c\":{\"type\":\"string\",\"enum\":[\"x\",\"y\"]}}},{\"type\":\"null\"}]},"
                + "\"b\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"d\":{\"type\":\"number\"}}}}}}";
            Assert.Equal(6, SchemaComplexity.Size(schema));
        }

        [Fact]
        public void TheCounterSeesAnyOfAndTypeArraysAtAnyDepth()
        {
            const string schema = "{\"type\":\"object\",\"required\":[\"a\",\"b\"],\"properties\":{"
                + "\"a\":{\"anyOf\":[{\"type\":\"object\",\"required\":[\"c\"],\"properties\":{\"c\":{\"type\":[\"number\",\"null\"]},\"d\":{\"type\":\"string\"}}},{\"type\":\"null\"}]},"
                + "\"b\":{\"type\":\"array\",\"items\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}]}}}}";
            Assert.Equal(3, SchemaComplexity.Unions(schema));
            Assert.Equal(1, SchemaComplexity.OptionalParameters(schema));
        }

        private const string Sentinels =
            "{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\","
            + "\"mission_plan\":{\"why\":\"w\",\"sequences\":[{\"name\":\"Launch\",\"intent\":\"i\",\"delta_v\":3400,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}],"
            + "\"child_vessels\":[{\"name\":\"Probe\",\"separation_stage\":-1,\"vessel_id\":\"\"}],"
            + "\"total_delta_v\":3400,\"expected_duration\":\"10 min\",\"mission_time_s\":-1,\"new_goal\":\"\"},"
            + "\"sequence_plan\":{\"name\":\"n\",\"intent\":\"i\",\"why\":\"w\",\"target\":\"t\",\"tolerances\":\"t\",\"expected_delta_v\":1,\"expected_duration_s\":2,"
            + "\"abort_conditions\":[],\"telemetry_checks\":[],\"risks\":[],\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print 1.\","
            + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1,\"manual\":false,\"vessel\":\"craft\"},"
            + "\"debrief\":null,"
            + "\"alert\":{\"verdict\":\"carry_on\",\"why\":\"w\",\"set_follow_up_threshold\":false,\"follow_up_threshold\":0}}";

        [Fact]
        public void EverySentinelReadsAsTheNullItReplaced()
        {
            var reply = MissionReply.Parse(Sentinels);

            Assert.Equal(ReplyIntent.None, reply.Intent);
            Assert.Null(reply.MissionPlan.MissionTimeSeconds);
            Assert.Null(reply.MissionPlan.NewGoal);
            Assert.Null(reply.MissionPlan.Sequences[0].DueTimeUt);
            Assert.Null(reply.MissionPlan.ChildVessels[0].SeparationStage);
            Assert.Equal("", reply.MissionPlan.ChildVessels[0].VesselId);
            Assert.Null(reply.SequencePlan.DueTimeUt);
            Assert.Null(reply.SequencePlan.WindowClosesUt);
            Assert.Null(reply.SequencePlan.LateWindowDeltaV);
            Assert.Null(reply.Alert.FollowUpThreshold);
        }

        [Fact]
        public void TheOldNullShapeStillReads()
        {
            // Saved plans (MissionSaveState) are written in the reply's shape with nulls.
            var reply = MissionReply.Parse(Sentinels.Replace("-1", "null").Replace("\"none\"", "null").Replace("\"new_goal\":\"\"", "\"new_goal\":null")
                .Replace("\"set_follow_up_threshold\":false,\"follow_up_threshold\":0", "\"follow_up_threshold\":null"));

            Assert.Equal(ReplyIntent.None, reply.Intent);
            Assert.Null(reply.MissionPlan.MissionTimeSeconds);
            Assert.Null(reply.MissionPlan.NewGoal);
            Assert.Null(reply.MissionPlan.Sequences[0].DueTimeUt);
            Assert.Null(reply.MissionPlan.ChildVessels[0].SeparationStage);
            Assert.Null(reply.SequencePlan.DueTimeUt);
            Assert.Null(reply.SequencePlan.WindowClosesUt);
            Assert.Null(reply.SequencePlan.LateWindowDeltaV);
            Assert.Null(reply.Alert.FollowUpThreshold);
        }

        [Fact]
        public void RealValuesSurviveTheSentinels()
        {
            var reply = MissionReply.Parse(Sentinels
                .Replace("\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}]", "\"due_time_ut\":0,\"manual\":false,\"vessel\":\"craft\"}]")
                .Replace("\"separation_stage\":-1,\"vessel_id\":\"\"", "\"separation_stage\":0,\"vessel_id\":\"abc\"")
                .Replace("\"mission_time_s\":-1,\"new_goal\":\"\"", "\"mission_time_s\":600,\"new_goal\":\"Orbit the Mun\"")
                .Replace("\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1", "\"due_time_ut\":100,\"window_closes_ut\":200,\"late_window_dv\":0")
                .Replace("\"reply_intent\":\"none\"", "\"reply_intent\":\"approve\""));

            Assert.Equal(ReplyIntent.Approve, reply.Intent);
            Assert.Equal(600, reply.MissionPlan.MissionTimeSeconds);
            Assert.Equal("Orbit the Mun", reply.MissionPlan.NewGoal);
            Assert.Equal(0, reply.MissionPlan.Sequences[0].DueTimeUt);
            Assert.Equal(0, reply.MissionPlan.ChildVessels[0].SeparationStage);
            Assert.Equal("abc", reply.MissionPlan.ChildVessels[0].VesselId);
            Assert.Equal(100, reply.SequencePlan.DueTimeUt);
            Assert.Equal(200, reply.SequencePlan.WindowClosesUt);
            Assert.Equal(0, reply.SequencePlan.LateWindowDeltaV);
        }

        [Fact]
        public void AFollowUpThresholdCanBeNegative()
        {
            // vertical_speed thresholds are often below zero, so -1 can't mean "none" here.
            var reply = MissionReply.Parse(Sentinels.Replace("\"set_follow_up_threshold\":false,\"follow_up_threshold\":0",
                "\"set_follow_up_threshold\":true,\"follow_up_threshold\":-1"));
            Assert.Equal(-1, reply.Alert.FollowUpThreshold);
        }
    }
}
