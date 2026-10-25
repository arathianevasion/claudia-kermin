// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Live supervision (spec stories 83 to 89 and 125), driven through the Mission seam: the waiting call at the
    /// Spending Cap, one request per Mission with Alerts ahead of questions, questions while Running, Telemetry
    /// Checks and their Alerts, and the Debrief that lists every crossing.
    /// </summary>
    public sealed class MissionLiveTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The waiting call ----

        [Fact]
        public void AQuestionThatWouldPassTheCapWhileRunningWaitsAndTheMissionStaysRunning()
        {
            // The priced plan and Sequence Plan cost $1.08, so a question's worst case no longer fits a $1.20 cap.
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 1.20m);

            mission.Send(new AskQuestion("How long will it take?"));

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Contains(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains(mission.TakeCommands().OfType<PostScreenMessage>(), m => m.Text.Contains("Spending Cap") && m.Always);
            // The run is not touched.
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.LatestLine == "About 5 seconds, and no engines fire.");
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.CapWait);
            Assert.DoesNotContain(QuickReply.RaiseCap, mission.View.QuickReplies);
            Assert.Contains("\"blocked\"", CostLog(mission));

            // The run ends as usual afterwards.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void ALateApprovalReplanThatWouldPassTheCapWaitsWithoutPausing()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan-priced.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan-priced.sse"), "\"window_closes_ut\":1300,\"late_window_dv\":null,"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, 1.20m, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            mission.Send(new UtAdvanced(1400));
            mission.Send(new Approve());

            Assert.Equal(MissionStage.Sequencing, mission.View.Stage);
            Assert.Contains("Spending Cap", mission.View.CapWait);
            Assert.Equal(new[] { QuickReply.RaiseCap }, mission.View.QuickReplies);
            Assert.Equal(2, transport.Requests.Count);
        }

        // ---- Questions while Running ----

        [Fact]
        public void QuestionsWhileRunningGoOneAtATimeWithACompactCraftSnapshotAndNeverTouchTheRun()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);
            Assert.True(mission.View.CanType);
            Assert.Contains(QuickReply.AskQuestion, mission.View.QuickReplies);

            mission.Send(new AskQuestion("How long will it take?"));
            Assert.True(mission.View.Busy);
            // Asked while the first is out: it waits its turn.
            mission.Send(new AskQuestion("And the fuel?"));
            Assert.Equal(2, mission.View.Log.Count(e => e.Kind == LogKind.Player && e.Text.EndsWith("?")));

            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.DoesNotContain(mission.TakeCommands(), c => c is AbortScript || c is StartScript);

            var first = LastUserText(transport.Requests[2]);
            Assert.Contains("How long will it take?", first);
            Assert.Contains("Craft now (compact, JSON data):", first);
            Assert.Contains("\"situation\":\"PRELAUNCH\"", first);
            Assert.Contains("\"run_elapsed_s\"", first);
            // Compact: not the whole Game Snapshot.
            Assert.DoesNotContain("Game Snapshot (JSON data", first);
            Assert.DoesNotContain("\"parts\"", first);
            Assert.Contains("And the fuel?", LastUserText(transport.Requests[3]));
            Assert.Equal("low", Effort(transport.Requests[3]));

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            Assert.Equal(MissionStage.Debriefing, mission.View.Stage);
        }

        [Fact]
        public void AQuestionClaudiaCantAnswerWhileRunningLeavesTheMissionRunning()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("refusal.sse"),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission, 10m);

            mission.Send(new AskQuestion("Why?"));
            DrainUntil(mission, v => !v.Busy);

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Equal("", mission.View.PauseReason);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Claudia couldn't answer that"));

            // The run's Debrief is the next request, as usual.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Equal(4, transport.Requests.Count);
        }

        // ---- Helpers ----

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var escaped = fields.Replace("\"", "\\\"");
            var replaced = text.Replace("\\\"script\\\":", escaped + "\\\"script\\\":");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>The effort in force for a request: the last effort-only system message in its conversation.</summary>
        private static string Effort(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                    .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).Last();
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config ?? new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private StartScript ToRunning(MissionControl mission, decimal cap)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
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

        private string CostLog(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        /// <summary>The text of the last user message in a request: the facts the turn gave Claudia.</summary>
        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
        }
    }
}
