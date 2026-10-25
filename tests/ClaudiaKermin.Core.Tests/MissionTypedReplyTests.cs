// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Words typed at a pending plan are acted on by what Claudia reads in them (reply_intent), whichever way they were
    /// sent: approve shows the confirmation, revise is one more request, a goal change comes with a revised Mission
    /// Plan carrying new_goal. Replies are hand-written; nothing calls the API.
    /// </summary>
    public sealed class MissionTypedReplyTests : IDisposable
    {
        private const string Goal = "Deorbit the station and land at the pad.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\n"
                + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\n"
                + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        /// <summary>A Mission Plan reply with one sequence named <paramref name="name"/>; <paramref name="newGoal"/> is JSON (a quoted string, "\"\"" for none).</summary>
        private static ScriptedResponse Plan(string name, string newGoal = "\"\"", string intent = "\"none\"")
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan: " + name + ".\",\"reply_intent\":" + intent + ","
                + "\"mission_plan\":{\"why\":\"Because.\",\"sequences\":[{\"name\":\"" + name + "\",\"intent\":\"Do it.\",\"delta_v\":100,\"timing\":\"right away\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":100,\"expected_duration\":\"an hour\",\"mission_time_s\":-1,\"new_goal\":" + newGoal + "},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse Answer(string intent)
        {
            return Reply("{\"kind\":\"answer\",\"say\":\"Noted.\",\"reply_intent\":" + intent + ",\"mission_plan\":null,\"sequence_plan\":null,\"debrief\":null}");
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        /// <summary>A Mission whose first Mission Plan (the first scripted response) awaits Approval.</summary>
        private MissionControl WithPlanPending(StreamingFakeTransport transport)
        {
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            return mission;
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var users = doc.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").ToArray();
                var content = users.Last().GetProperty("content");
                return content.ValueKind == JsonValueKind.String ? content.GetString()
                    : string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }

        private string Events(MissionControl mission)
        {
            return File.ReadAllText(Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "events.jsonl"));
        }

        private string Info(MissionControl mission)
        {
            return File.ReadAllText(Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "mission.json"));
        }

        [Fact]
        public void AQuestionReadAsARevisionAsksOnceForARevisedPlanThatReplacesThePendingOne()
        {
            var transport = new StreamingFakeTransport(Plan("Deorbit"), Answer("\"revise\""), Plan("Night deorbit"));
            var mission = WithPlanPending(transport);
            Assert.Equal(new[] { "Deorbit" }, mission.View.PlanSequences);

            mission.Send(new AskQuestion("revise to land at night"));
            DrainUntil(mission, v => !v.Busy && v.PlanSequences.SequenceEqual(new[] { "Night deorbit" }));

            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Contains("wants a change", LastUserText(transport.Requests[2]));
            Assert.Contains("revise to land at night", LastUserText(transport.Requests[2]));
            Assert.Null(mission.View.ConfirmPrompt);
            // The question and the change both stay in the planning schema (#199), so the prompt cache holds.
            Assert.All(transport.Requests, r => Assert.Contains(MissionReply.PlanningSchemaJson, r.Body));
        }

        [Fact]
        public void AQuestionReadAsAnApprovalShowsTheConfirmationAndStartsNothing()
        {
            var transport = new StreamingFakeTransport(Plan("Deorbit"), Answer("\"approve\""));
            var mission = WithPlanPending(transport);

            mission.Send(new AskQuestion("approve"));
            DrainUntil(mission, v => !v.Busy && v.ConfirmPrompt != null);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("Approve the Mission Plan?", mission.View.ConfirmPrompt);
            Assert.Equal(new[] { QuickReply.ConfirmApproval, QuickReply.NotYet }, mission.View.QuickReplies);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
        }

        [Theory]
        [InlineData("\"question\"")]
        [InlineData("\"none\"")]
        [InlineData("\"unclear\"")]
        public void AQuestionWithNoApproveOrReviseIntentChangesNothing(string intent)
        {
            var transport = new StreamingFakeTransport(Plan("Deorbit"), Answer(intent));
            var mission = WithPlanPending(transport);

            mission.Send(new AskQuestion("how much delta-v is that?"));
            DrainUntil(mission, v => !v.Busy && v.Log.Any(e => e.Kind == LogKind.Claudia && e.Text.Contains("Noted")));

            Assert.Equal(2, transport.Requests.Count);
            Assert.Null(mission.View.ConfirmPrompt);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            Assert.Equal(new[] { "Deorbit" }, mission.View.PlanSequences);
        }

        [Fact]
        public void AGoalChangeGivesARevisedPlanAndApprovingItChangesThePinnedAndStoredGoalAndRecordsAnEvent()
        {
            const string newGoal = "Land at the pad at night.";
            var transport = new StreamingFakeTransport(Plan("Deorbit"), Answer("\"revise\""), Plan("Night deorbit", "\"" + newGoal + "\""), Plan("Burn"));
            var mission = WithPlanPending(transport);

            mission.Send(new AskQuestion("change the goal to landing at night"));
            DrainUntil(mission, v => !v.Busy && v.PlanSequences.SequenceEqual(new[] { "Night deorbit" }));
            Assert.Equal(Goal, mission.View.Goal);
            Assert.DoesNotContain("goal_changed", Events(mission));

            mission.Send(new Approve());
            Assert.Equal(newGoal, mission.View.Goal);
            Assert.Contains(newGoal, Info(mission));
            Assert.Contains("goal_changed", Events(mission));
            Assert.Contains(newGoal, Events(mission));
            DrainUntil(mission, v => !v.Busy);
            Assert.Contains("The Mission Goal is now", LastUserText(transport.Requests[3]));
        }

        [Fact]
        public void AChangedGoalSurvivesSaveAndReloadAndSoDoesAPendingGoalChange()
        {
            const string newGoal = "Land at the pad at night.";
            var transport = new StreamingFakeTransport(Plan("Deorbit"), Plan("Night deorbit", "\"" + newGoal + "\""), Plan("Burn"));
            var mission = WithPlanPending(transport);
            mission.Send(new Reply("new goal: land at night"));
            DrainUntil(mission, v => !v.Busy && v.PlanSequences.SequenceEqual(new[] { "Night deorbit" }));

            // Saved while the revised plan still waits: the new goal is not the goal yet, but it is not lost.
            var pending = mission.ExportStates();
            Assert.Equal(Goal, pending[0].Goal);
            Assert.Equal(newGoal, pending[0].MissionPlan.NewGoal);

            mission.Send(new Approve());
            DrainUntil(mission, v => !v.Busy);
            var saved = mission.ExportStates();
            Assert.Equal(newGoal, saved[0].Goal);

            // A load of the later save brings the new goal back; a load of the earlier one brings back the pending change.
            mission.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(newGoal, mission.View.Goal);
            mission.Send(new MissionsRestored(_save.Path, pending));
            Assert.Equal(Goal, mission.View.Goal);
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            mission.Send(new Approve());
            Assert.Equal(newGoal, mission.View.Goal);
        }

        [Fact]
        public void TheMissionPlanSchemaAsksForANullableNewGoalAndABlankOneChangesNothing()
        {
            var transport = new StreamingFakeTransport(Plan("Deorbit", "\"   \""), Plan("Burn"));
            var mission = WithPlanPending(transport);
            Assert.Contains("new_goal", transport.Requests[0].Body);

            mission.Send(new Approve());
            DrainUntil(mission, v => !v.Busy);
            Assert.Equal(Goal, mission.View.Goal);
            Assert.DoesNotContain("goal_changed", Events(mission));
        }

        [Theory]
        [InlineData(MissionStage.AwaitingPlanApproval)]
        [InlineData(MissionStage.AwaitingSequenceApproval)]
        public void WhenAPlanStartsToAwaitApprovalThePromptGoesBackToReply(MissionStage stage)
        {
            var view = new MissionView { Stage = stage, MissionId = "m1", CanEndMission = true, CanType = true };
            Assert.Equal(PromptFor.Reply, MissionWindowRules.ChosenForPlan(false, view, PromptFor.Question));
            Assert.Equal(PromptFor.Reply, MissionWindowRules.Prompt(view, MissionWindowRules.ChosenForPlan(false, view, PromptFor.Question), false).For);
        }

        [Fact]
        public void AskPickedWhileAPlanAlreadyWaitsIsKept()
        {
            var view = new MissionView { Stage = MissionStage.AwaitingPlanApproval, MissionId = "m1", CanEndMission = true, CanType = true };
            Assert.Equal(PromptFor.Question, MissionWindowRules.ChosenForPlan(true, view, PromptFor.Question));
        }
    }
}
