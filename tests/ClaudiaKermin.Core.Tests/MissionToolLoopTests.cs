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
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The tool loop (#274) through <see cref="MissionControl"/> with scripted tool_use replies on the streaming fake: the tools
    /// offered per request kind, the Alert error, the one commit, byte-exact steps, the call and step limits, the Spending Cap,
    /// and what a refusal, a cut-off, a fallback, a save, a load and a Pause do to a loop mid-way.
    /// </summary>
    public sealed class MissionToolLoopTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Vacuum = "{\"situation\":\"vacuum\"}";
        private const string CutOffNote = "Your last attempt ran out of room while thinking. Decide with what you have and keep your reasoning short.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly List<ManualResetEventSlim> _gates = new List<ManualResetEventSlim>();

        public void Dispose()
        {
            foreach (var gate in _gates) gate.Set();
            _save.Dispose();
        }

        // ---- Setup ----

        private MissionControl Control(IHttpTransport transport, PlanningTools tools = null, bool route = true)
        {
            var client = new ClaudeClient(route ? new CraftCheckRouter(transport) : transport, () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            if (tools != null) mission.Tools = tools;
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private void Start(MissionControl mission, decimal cap = 25m)
        {
            mission.Send(new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
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

        private static ScriptedResponse Fixture(string name)
        {
            return ScriptedResponse.Fixture(name);
        }

        private static ScriptedResponse StagePerformance(string tag, string input = Vacuum)
        {
            return ToolUseStreams.ToolUse(tag, ("stage_performance", input));
        }

        /// <summary>A tool for the loop tests: it answers at once, or waits on a gate.</summary>
        private sealed class TestTool : IPlanningTool
        {
            public string Name { get; set; } = "fake";
            public string Description { get { return "A test tool."; } }
            public string InputSchemaJson { get { return "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"; } }
            public bool OnCraftCheck { get; set; }
            public string Activity { get; set; } = "faking it";
            public Func<ToolOutcome> Answer = () => new ToolOutcome { Json = "{\"ok\":true}", Summary = "ok" };

            public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
            {
                return Answer();
            }
        }

        private static PlanningTools With(params IPlanningTool[] extra)
        {
            return new PlanningTools(new IPlanningTool[] { new StagePerformance() }.Concat(extra));
        }

        // ---- Reading requests ----

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body)) return doc.RootElement.Clone();
        }

        private static List<string> Messages(HttpRequestSpec request)
        {
            return Body(request).GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToList();
        }

        private static string ToolsJson(HttpRequestSpec request)
        {
            return Body(request).GetProperty("tools").GetRawText();
        }

        private static string[] ToolNames(HttpRequestSpec request)
        {
            return Body(request).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        }

        /// <summary>The tool_result blocks of a request's last message.</summary>
        private static List<JsonElement> Results(HttpRequestSpec request)
        {
            var last = Body(request).GetProperty("messages").EnumerateArray().Last();
            Assert.Equal("user", last.GetProperty("role").GetString());
            return last.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "tool_result").ToList();
        }

        private static string ErrorOf(JsonElement result)
        {
            Assert.True(result.GetProperty("is_error").GetBoolean());
            using (var doc = JsonDocument.Parse(result.GetProperty("content").GetString())) return doc.RootElement.GetProperty("error").GetString();
        }

        private static int Count(string text, string part)
        {
            return text.Split(new[] { part }, StringSplitOptions.None).Length - 1;
        }

        private static string[] Efforts(HttpRequestSpec request)
        {
            return Body(request).GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).ToArray();
        }

        private string Folder(MissionControl mission)
        {
            return Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId);
        }

        private string Events(MissionControl mission)
        {
            var path = Path.Combine(Folder(mission), MissionStore.EventsFile);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static List<LogEntry> ToolLines(MissionControl mission)
        {
            return mission.View.Log.Where(e => e.Kind == LogKind.Notice && e.Text.StartsWith("Planning Tool ")).ToList();
        }

        // ---- Tools offered per request kind ----

        private static string DeltaVCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"delta_v_used\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Nothing should burn.\"}],";
        }

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace("\\\"script\\\":", fields.Replace("\"", "\\\"") + "\\\"script\\\":");
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>From Start Mission to a crossed Telemetry Check's Alert, with whatever replies follow the Sequence Plan.</summary>
        private StartScript ToRunning(MissionControl mission)
        {
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        private static GameSnapshot Burned()
        {
            var snapshot = OnPad();
            snapshot.Stages[0].DeltaVVac = 3100;
            return snapshot;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        [Fact]
        public void EveryMissionRequestCarriesTheSameToolsListAndTheCraftCheckAndMarginRecheckOnlyStagePerformance()
        {
            var transport = new StreamingFakeTransport(
                Fixture("mission-plan.sse"),
                Fixture("mission-answer.sse"),
                WithSequenceFields(Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                Fixture("mission-alert-carry-on-plain.sse"),
                Fixture("mission-debrief.sse"));
            var router = new CraftCheckRouter(transport);
            var mission = Control(router, With(new TestTool { Name = "predict_burn" }), route: false);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 2);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new Approve());
            var start = mission.TakeCommands().OfType<StartScript>().Single();
            mission.Send(Burned());
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // Mission Plan, answer, Sequence Plan, Alert and Debrief: one tools list, byte for byte, and tool_choice auto on all.
            Assert.Equal(5, transport.Requests.Count);
            Assert.Contains("Alert: a Telemetry Check", transport.Requests[3].Body);
            Assert.Equal(new[] { "stage_performance", "predict_burn" }, ToolNames(transport.Requests[0]));
            foreach (var request in transport.Requests)
            {
                Assert.Equal(ToolsJson(transport.Requests[0]), ToolsJson(request));
                Assert.Equal("{\"type\":\"auto\"}", Body(request).GetProperty("tool_choice").GetRawText());
                // Non-strict: the module checks the input, so the schemas take none of the reply schema's grammar budget.
                Assert.All(Body(request).GetProperty("tools").EnumerateArray(), t => Assert.False(t.TryGetProperty("strict", out _)));
            }
            // The Alert's turn says no tool is available.
            Assert.Contains("No Planning Tool is available during an Alert", transport.Requests[3].Body);

            // The start check and the Debrief's Margin recheck: stage_performance only.
            Assert.Equal(2, router.CheckRequests.Count);
            Assert.Contains("Margin recheck after a Maneuver Sequence", router.CheckRequests[1].Body);
            foreach (var check in router.CheckRequests)
            {
                Assert.Equal(new[] { "stage_performance" }, ToolNames(check));
                Assert.Equal("{\"type\":\"auto\"}", Body(check).GetProperty("tool_choice").GetRawText());
            }
        }

        [Fact]
        public void AToolCallDuringAnAlertIsAnsweredWithAnErrorAndTheAlertIsStillJudged()
        {
            var transport = new StreamingFakeTransport(
                Fixture("mission-plan.sse"),
                WithSequenceFields(Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                StagePerformance("alert"),
                Fixture("mission-alert-carry-on-plain.sse"));
            var mission = Control(transport);
            ToRunning(mission);
            mission.Send(Burned());
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);

            var result = Assert.Single(Results(transport.Requests[3]));
            Assert.Equal("toolu_alert_0", result.GetProperty("tool_use_id").GetString());
            Assert.Equal("not available during an Alert", ErrorOf(result));
            Assert.Equal("{\"error\":\"not available during an Alert\"}", result.GetProperty("content").GetString());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.StartsWith("Alert, delta-v used: carry on.", mission.View.LatestLine);
            Assert.Contains(ToolLines(mission), e => e.Text.EndsWith("error: not available during an Alert"));
        }

        // ---- One commit, byte-exact steps ----

        [Fact]
        public void EachStepIsTheLastRequestPlusTwoMessagesAndTheLoopCommitsOnceWithTheAcceptedReply()
        {
            var transport = new StreamingFakeTransport(
                StagePerformance("a"),
                StagePerformance("b", "{\"situation\":\"surface\",\"body\":\"Mun\"}"),
                Fixture("mission-plan.sse"),
                Fixture("mission-answer.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(3, transport.Requests.Count);

            for (var k = 0; k < 2; k++)
            {
                var before = Messages(transport.Requests[k]);
                var after = Messages(transport.Requests[k + 1]);
                Assert.Equal(before.Count + 2, after.Count);
                Assert.Equal(before, after.Take(before.Count).ToList());
                // The prefix is the same bytes in the request body itself, not just the same JSON.
                Assert.Contains(string.Join(",", before), transport.Requests[k + 1].Body);
            }
            // Claude's tool_use turn goes back exactly: its thinking signature and its input as streamed.
            var echoed = Messages(transport.Requests[1])[Messages(transport.Requests[1]).Count - 2];
            Assert.Contains("\"signature\":\"c2lnLXRvb2wta\"", echoed);
            Assert.Contains("\"input\":{\"situation\":\"vacuum\"}", echoed);
            var result = Assert.Single(Results(transport.Requests[1]));
            Assert.Equal("toolu_a_0", result.GetProperty("tool_use_id").GetString());
            Assert.False(result.TryGetProperty("is_error", out _));
            Assert.Equal(PlanningTools.Default().Run("stage_performance", Vacuum, OnPad(), null).Json, result.GetProperty("content").GetString());
            Assert.Contains("\"body\":\"Mun\"", Results(transport.Requests[2]).Single().GetProperty("content").GetString());
            // One effort for the whole loop.
            Assert.Equal(new[] { "high" }, Efforts(transport.Requests[2]));

            // The loop joined the conversation once, with the plan: the next turn builds on all of it, once.
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 4);
            var next = Messages(transport.Requests[3]);
            var loop = Messages(transport.Requests[2]);
            Assert.Equal(loop, next.Take(loop.Count).ToList());
            Assert.Equal(1, Count(transport.Requests[3].Body, "\"id\":\"toolu_a_0\""));
            Assert.Equal(1, Count(transport.Requests[3].Body, "\"id\":\"toolu_b_0\""));
            var transcript = File.ReadAllText(Path.Combine(Folder(mission), MissionFolder.TranscriptFile));
            Assert.Equal(1, Count(transcript, "toolu_a_0\",\"name\""));
        }

        [Fact]
        public void TheCapcomStripNamesTheToolWhileItRunsAndTheMissionLogHasOneLinePerCall()
        {
            var gate = new ManualResetEventSlim();
            _gates.Add(gate);
            var tool = new TestTool { Activity = "checking stage performance", Answer = () => { gate.Wait(TimeSpan.FromSeconds(5)); return new ToolOutcome { Json = "{\"ok\":true}", Summary = "ok" }; } };
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("cap", ("fake", "{}"), ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(tool));
            Start(mission);
            DrainUntil(mission, v => v.Activity == "checking stage performance");
            Assert.Equal("Claudia is checking stage performance…", MissionWindowRules.BusyLine(mission.View));
            gate.Set();
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var lines = ToolLines(mission);
            Assert.Equal(2, lines.Count);
            Assert.Equal("Planning Tool fake ({}): ok", lines[0].Text);
            Assert.Equal("Planning Tool stage_performance ({\"situation\":\"vacuum\"}): 1 stage, 3200 m/s in all in vacuum at Kerbin", lines[1].Text);
            Assert.Equal(2, Count(Events(mission), "\"tool\""));
        }

        // ---- Limits ----

        [Fact]
        public void UpToSixCallsOfAStepRunTogetherAndAnyMoreAreAnsweredWithAnError()
        {
            var started = new CountdownEvent(6);
            var tool = new TestTool
            {
                Answer = () =>
                {
                    started.Signal();
                    // Only six calls running at once get past this.
                    var together = started.Wait(TimeSpan.FromSeconds(3));
                    return new ToolOutcome { Json = "{\"together\":" + (together ? "true" : "false") + "}", Summary = "ok" };
                },
            };
            var calls = Enumerable.Range(0, 7).Select(i => ("fake", "{}")).ToArray();
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("par", calls), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(tool));
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            // Every call's result in one user message, in the order of the calls.
            var results = Results(transport.Requests[1]);
            Assert.Equal(Enumerable.Range(0, 7).Select(i => "toolu_par_" + i), results.Select(r => r.GetProperty("tool_use_id").GetString()));
            Assert.All(results.Take(6), r => Assert.Equal("{\"together\":true}", r.GetProperty("content").GetString()));
            Assert.Equal("at most 6 tool calls run in one step; call this again in the next", ErrorOf(results[6]));
        }

        [Fact]
        public void AfterEightStepsEveryResultSaysToReplyNowAndTheReplyIsAccepted()
        {
            var transport = new StreamingFakeTransport();
            for (var i = 0; i < 9; i++) transport.Add(ToolUseStreams.ToolUse("s" + i, ("stage_performance", Vacuum), ("stage_performance", Vacuum)));
            transport.Add(Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(10, transport.Requests.Count);
            for (var step = 1; step <= 8; step++)
                Assert.All(Results(transport.Requests[step]), r => Assert.False(r.TryGetProperty("is_error", out _)));
            var last = Results(transport.Requests[9]);
            Assert.Equal(2, last.Count);
            Assert.All(last, r => Assert.Equal("tool limit reached; reply now", ErrorOf(r)));
            Assert.Equal(16, ToolLines(mission).Count(l => !l.Text.Contains("error")));
        }

        [Fact]
        public void AReplyThatStillCallsToolsAfterTheLimitFailsTheTurnAndRetrySendsItFromScratch()
        {
            var transport = new StreamingFakeTransport();
            for (var i = 0; i < 10; i++) transport.Add(StagePerformance("s" + i));
            transport.Add(Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(10, transport.Requests.Count);
            Assert.Equal("Claudia kept calling Planning Tools after the limit of 8 steps.", mission.View.PauseReason);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(transport.Requests[0].Body, transport.Requests[10].Body);
        }

        [Fact]
        public void ACallOverItsTimeLimitIsAnsweredWithAnError()
        {
            var gate = new ManualResetEventSlim();
            _gates.Add(gate);
            var tool = new TestTool { Answer = () => { gate.Wait(TimeSpan.FromSeconds(5)); return new ToolOutcome { Json = "{}" }; } };
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUse("slow", ("fake", "{}"), ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(tool));
            mission.ToolCallTimeout = TimeSpan.FromMilliseconds(100);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var results = Results(transport.Requests[1]);
            Assert.Equal("the calculation took longer than 0.1 s", ErrorOf(results[0]));
            Assert.False(results[1].TryGetProperty("is_error", out _));
            Assert.Equal(TimeSpan.FromSeconds(5), ToolStep.CallTimeout);
        }

        // ---- The Spending Cap ----

        [Fact]
        public void AStepWithNoRoomUnderTheSpendingCapPausesTheMissionAndDiscardsTheLoop()
        {
            // The first request's worst case fits $0.45; after the tool step's $0.20 the next step's no longer does.
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUsePriced("cap", 100000, ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission, 0.45m);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Single(transport.Requests);
            Assert.Contains("Spending Cap", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies.ToArray());
            Assert.Contains("tool_loop_discarded", Events(mission));

            mission.Send(new RaiseCap(25m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            // Sent again from scratch: the request is the turn's first, with no tool step in it.
            Assert.Equal(transport.Requests[0].Body, transport.Requests[1].Body);
        }

        // ---- Mid-loop failures ----

        [Fact]
        public void ARefusalMidLoopPausesAsTodayAndRetrySendsTheTurnFromScratch()
        {
            var transport = new StreamingFakeTransport(StagePerformance("r"), Fixture("refusal.sse"), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal("Claudia declined to answer that. Retry, Retry with a note, or End Mission.", mission.View.PauseReason);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(transport.Requests[0].Body, transport.Requests[2].Body);
        }

        [Fact]
        public void ACutOffMidLoopGoesToTheAutomaticRetryWithTheLoopDiscarded()
        {
            var transport = new StreamingFakeTransport(StagePerformance("c"), Fixture("max-tokens-tool.sse"), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(3, transport.Requests.Count);
            var retry = transport.Requests[2];
            Assert.Equal(Messages(transport.Requests[0]).Count, Messages(retry).Count);
            Assert.DoesNotContain("tool_result", retry.Body);
            Assert.Equal(new[] { "medium" }, Efforts(retry));
            Assert.Equal(1, Count(retry.Body, CutOffNote));
        }

        [Fact]
        public void AServerSideFallbackMidLoopCarriesOnAndIsLogged()
        {
            var transport = new StreamingFakeTransport(ToolUseStreams.ToolUseAfterFallback("f", ("stage_performance", Vacuum)), Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Single(Results(transport.Requests[1]));
            Assert.Contains("tool_loop_fallback", Events(mission));
            Assert.Contains("served by claude-opus-5-5", Events(mission));
        }

        // ---- Save, load and quit mid-loop ----

        [Fact]
        public void ASaveMidLoopHoldsOnlyTheTurnSoAfterAQuitItIsSentFromScratch()
        {
            var plan = ScriptedResponse.Fixture("mission-plan.sse", 64);
            plan.PauseAt = 100;
            _gates.Add(plan.Gate);
            var transport = new StreamingFakeTransport(StagePerformance("q"), plan);
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => transport.Requests.Count == 2);
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();

            var after = new StreamingFakeTransport(Fixture("mission-plan.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Single(after.Requests);
            Assert.DoesNotContain("tool_use", after.Requests[0].Body);
            Assert.Equal(Messages(transport.Requests[0]).Count, Messages(after.Requests[0]).Count);
        }

        [Fact]
        public void ALoadMidLoopRunsNoToolsAndTheTurnIsSentFromScratch()
        {
            var step = StagePerformance("l");
            step.ChunkSize = 64;
            step.PauseAt = 100;
            _gates.Add(step.Gate);
            var transport = new StreamingFakeTransport(step, Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => transport.Requests.Count == 1);
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();

            mission.Send(new MissionsRestored(_save.Path, saved));
            step.Gate.Set();
            // The tool_use reply lands after the load: the save's state comes back, and the craft's next snapshot carries it on.
            DrainUntil(mission, v => !v.Busy);
            Assert.Single(transport.Requests);
            mission.Send(OnPad());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(2, transport.Requests.Count);
            Assert.DoesNotContain("tool_use", transport.Requests[1].Body);
            Assert.Empty(ToolLines(mission));
        }

        // ---- The Craft Check and the Margin recheck ----

        [Fact]
        public void TheCraftCheckRunsItsOwnToolLoopAndEachStepIsChargedToTheMission()
        {
            // No router: the Craft Check's requests come first on the one transport.
            var transport = new StreamingFakeTransport(StagePerformance("cc"), Fixture("craft-check-pass-free.sse"), Fixture("mission-plan.sse"));
            var mission = Control(transport, With(new TestTool { Name = "predict_burn" }), route: false);
            Start(mission);
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(new[] { "stage_performance" }, ToolNames(transport.Requests[0]));
            var result = Assert.Single(Results(transport.Requests[1]));
            Assert.Contains("\"total_dv\":3200", result.GetProperty("content").GetString());
            Assert.Equal(new[] { "stage_performance", "predict_burn" }, ToolNames(transport.Requests[2]));
            var costLog = File.ReadAllText(Path.Combine(Folder(mission), MissionFolder.CostLogFile));
            Assert.Equal(2, Count(costLog, "\"craft_check_pending\""));
            Assert.Contains("\"tool_use\"", costLog);
        }

        [Fact]
        public void TheMarginRecheckRunsStagePerformanceAndItsFindingsStillReachTheDebrief()
        {
            var transport = new StreamingFakeTransport(Fixture("mission-plan.sse"), Fixture("mission-sequence-plan.sse"), Fixture("mission-debrief.sse"));
            var router = new CraftCheckRouter(transport);
            router.Rechecks.Enqueue(StagePerformance("m", "{\"situation\":\"surface\"}"));
            var mission = Control(router, route: false);
            var start = ToRunning(mission);
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            // The start check, then the recheck's two steps.
            Assert.Equal(3, router.CheckRequests.Count);
            var result = Assert.Single(Results(router.CheckRequests[2]));
            Assert.Contains("\"situation\":\"surface\"", result.GetProperty("content").GetString());
            Assert.Contains("The mod's margin recheck (a Craft Check", transport.Requests[2].Body);
            Assert.Contains(ToolLines(mission), e => e.Text.StartsWith("Planning Tool stage_performance for the margin recheck"));
        }
    }
}
