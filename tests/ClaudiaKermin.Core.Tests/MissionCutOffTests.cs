// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The cut-off policy (#267) through <see cref="MissionControl"/> with scripted max_tokens replies: one automatic
    /// retry at medium with a note, a second cut-off failing as before, a Retry that sends the reduced form, and the
    /// Spending Cap gate.
    /// </summary>
    public sealed class MissionCutOffTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Note = "Your last attempt ran out of room while thinking. Decide with what you have and keep your reasoning short.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
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

        private static ScriptedResponse CutOff()
        {
            return ScriptedResponse.Fixture("max-tokens-tool.sse");
        }

        private void Start(MissionControl mission, decimal cap = 25m)
        {
            mission.Send(new StartMission(Goal, cap, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
        }

        private static JsonElement Body(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body)) return doc.RootElement.Clone();
        }

        private static string[] Efforts(HttpRequestSpec request)
        {
            return Body(request).GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).ToArray();
        }

        private static int NoteCount(HttpRequestSpec request)
        {
            return request.Body.Split(new[] { Note }, StringSplitOptions.None).Length - 1;
        }

        [Fact]
        public void ACutOffReplyIsRetriedOnceAtMediumWithTheNoteAndNeverCommitted()
        {
            var transport = new StreamingFakeTransport(CutOff(), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(new[] { "high" }, Efforts(transport.Requests[0]));
            Assert.Equal(new[] { "medium" }, Efforts(transport.Requests[1]));
            Assert.Equal(0, NoteCount(transport.Requests[0]));
            Assert.Equal(1, NoteCount(transport.Requests[1]));
            // Same turn: the retry is the first request's messages in the reduced form, not a new turn.
            Assert.Equal(Body(transport.Requests[0]).GetProperty("messages").GetArrayLength(), Body(transport.Requests[1]).GetProperty("messages").GetArrayLength());
            // max_tokens stays at 32,000 on both.
            foreach (var request in transport.Requests) Assert.Equal(32000, Body(request).GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public void TheReducedFormIsWhatLaterTurnsBuildOn()
        {
            var transport = new StreamingFakeTransport(CutOff(), ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && transport.Requests.Count == 3);

            // The cut-off reply never joined the conversation, and the reduced turn's medium effort is the conversation's.
            var messages = Body(transport.Requests[2]).GetProperty("messages").EnumerateArray().ToList();
            Assert.DoesNotContain("max_tokens", string.Join("", messages.Select(m => m.GetRawText())));
            Assert.Equal(new[] { "medium", "low" }, Efforts(transport.Requests[2]));
        }

        [Fact]
        public void ASecondCutOffFailsTheTurnAsBeforeAndRetrySendsTheReducedFormNeverTheIdenticalRequest()
        {
            var transport = new StreamingFakeTransport(CutOff(), CutOff(), CutOff(), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("Claudia's reply was cut short.", mission.View.PauseReason);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.Paused && transport.Requests.Count == 3);
            // The player's Retry is not retried again by the mod; it sends the second request's form exactly.
            Assert.Equal(transport.Requests[1].Body, transport.Requests[2].Body);
            Assert.Equal(1, NoteCount(transport.Requests[2]));
            Assert.NotEqual(transport.Requests[0].Body, transport.Requests[2].Body);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(transport.Requests[1].Body, transport.Requests[3].Body);
        }

        [Fact]
        public void RetryWithANoteStaysAvailableAfterACutOffAndSendsTheNoteInTheReducedForm()
        {
            var transport = new StreamingFakeTransport(CutOff(), CutOff(), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.EndMission }, mission.View.QuickReplies.ToArray());

            mission.Send(new RetryWithNote("Keep it to one sequence."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var third = transport.Requests[2];
            Assert.Contains("Keep it to one sequence.", third.Body);
            Assert.Equal(1, NoteCount(third));
            Assert.Equal(new[] { "medium" }, Efforts(third));
        }

        [Fact]
        public void WithNoSpendingCapRoomForTheRetryTheTurnFailsAsBeforeAndRetryStillSendsTheReducedForm()
        {
            // The first request's worst case fits $0.40 (Sonnet's $10 per million output tokens); after its real cost the retry's worst case no longer does.
            var transport = new StreamingFakeTransport(CutOff(), ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            Start(mission, 0.41m); // raised for #348 Physics warp on Coasts setting (human decision 2026-10-27)
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Single(transport.Requests);
            Assert.Equal("Claudia's reply was cut short.", mission.View.PauseReason);
            Assert.DoesNotContain(QuickReply.RaiseCap, mission.View.QuickReplies);

            mission.Send(new RaiseCap(25m));
            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(new[] { "medium" }, Efforts(transport.Requests[1]));
            Assert.Equal(1, NoteCount(transport.Requests[1]));
        }

        [Fact]
        public void ACutOffPauseAndItsReducedFormSurviveAQuitAndRetrySendsTheSameRequest()
        {
            var transport = new StreamingFakeTransport(CutOff(), CutOff());
            var mission = Control(transport);
            Start(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);
            var saved = mission.ExportStates();
            saved = new List<MissionSaveState> { MissionSaveState.FromValues(saved[0].ToValues()) };
            Assert.Equal("CutOff", saved[0].PauseCause);

            var after = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var resumed = Control(after);
            resumed.Send(new MissionsRestored(_save.Path, saved));
            Assert.Equal(MissionStage.Paused, resumed.View.Stage);
            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.EndMission }, resumed.View.QuickReplies.ToArray());
            Assert.Empty(after.Requests);

            resumed.Send(new Retry());
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Equal(new[] { "medium" }, Efforts(after.Requests[0]));
            Assert.Equal(1, NoteCount(after.Requests[0]));
        }
    }
}
