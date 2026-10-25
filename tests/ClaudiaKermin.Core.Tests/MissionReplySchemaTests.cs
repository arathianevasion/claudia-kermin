// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The structured-output schemas stay small enough for the API to compile (#199): with 14 unions the
    /// MissionReply schema was rejected on every request as "compiled grammar is too large".
    /// </summary>
    public class MissionReplySchemaTests
    {
        /// <summary>
        /// The four nullable parts (mission_plan, sequence_plan, debrief, alert). Raise it only after the schema
        /// probe (tools/ClaudiaKermin.SchemaProbe) shows the bigger schema still compiles, with one more to spare.
        /// </summary>
        private const int MissionReplyUnionBudget = 4;

        [Fact]
        public void MissionReplyStaysWithinItsUnionBudget()
        {
            Assert.InRange(SchemaComplexity.Unions(MissionReply.SchemaJson), 0, MissionReplyUnionBudget);
        }

        [Fact]
        public void CraftCheckRulesHaveNoUnions()
        {
            Assert.Equal(0, SchemaComplexity.Unions(CraftCheckRules.SchemaJson()));
        }

        [Fact]
        public void NeitherSchemaHasOptionalParameters()
        {
            Assert.Equal(0, SchemaComplexity.OptionalParameters(MissionReply.SchemaJson));
            Assert.Equal(0, SchemaComplexity.OptionalParameters(CraftCheckRules.SchemaJson()));
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
