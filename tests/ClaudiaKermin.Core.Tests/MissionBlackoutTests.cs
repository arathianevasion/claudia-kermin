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
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Blackout (spec stories 99 to 105, ADR 0002's amendment), driven through the Mission seam with hand-written streams:
    /// link loss starts it (CommNet off counts as linked), the Mission Plan's Approval stands for each sequence, her messages
    /// are queued and the player's held, Alerts are recorded without a call, Abort is hidden, a Failed Debrief makes the
    /// craft safe and waits for the link, link loss in Manual Control with nobody else aboard takes the craft back (#130),
    /// and an Abort recommendation outside Blackout is a moment the player is needed (#120).
    /// </summary>
    public sealed class MissionBlackoutTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string CraftId = "c0ffee00c0ffee00c0ffee00c0ffee00";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- Link state ----

        [Fact]
        public void LosingTheLinkStartsABlackoutAndCommNetOffCountsAsLinked()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToRunning(mission);
            Assert.False(mission.View.Blackout);

            // CommNet off in the save: no connection, but linked all the same.
            mission.Send(Snapshot(s => { s.HasLink = false; s.CommNetEnabled = false; }));
            Assert.False(mission.View.Blackout);

            mission.Send(Snapshot(s => s.HasLink = false));
            Assert.True(mission.View.Blackout);
            // A Blackout is not a Pause: the run carries on.
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains("No link to Kerbin", mission.View.LatestLine);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("No link to Kerbin: Blackout"));

            mission.Send(Snapshot(s => s.HasLink = true));
            Assert.False(mission.View.Blackout);
        }

        [Fact]
        public void TheLinkOfTheCraftSeenFromAnotherVesselCountsToo()
        {
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan.sse")));
            ToRunning(mission);

            mission.Send(new CraftLinkSeen("some-other-vessel", false));
            Assert.False(mission.View.Blackout);
            mission.Send(new CraftLinkSeen(CraftId, false));
            Assert.True(mission.View.Blackout);
            mission.Send(new CraftLinkSeen(CraftId, true));
            Assert.False(mission.View.Blackout);
        }

        // ---- Alerts and Abort ----

        [Fact]
        public void InABlackoutACrossedCheckIsRecordedWithoutAClaudeCallAndAbortIsHidden()
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                WithSequenceFields(ScriptedResponse.Fixture("mission-sequence-plan.sse"), DeltaVCheck(50)),
                ScriptedResponse.Fixture("mission-debrief.sse"));
            var mission = Control(transport);
            var start = ToRunning(mission);
            Assert.Contains(QuickReply.Abort, mission.View.QuickReplies);

            mission.Send(Snapshot(s => s.HasLink = false));
            Assert.DoesNotContain(QuickReply.Abort, mission.View.QuickReplies);

            // 100 m/s used against a 50 m/s check.
            mission.Send(Snapshot(s => { s.HasLink = false; s.Stages[0].DeltaVVac = 3100; }));
            _background.Drain();
            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("recorded without a call to Claudia"));
            Assert.Equal("", mission.View.AbortRecommended);

            // The Abort button does nothing in a Blackout.
            mission.TakeCommands();
            mission.Send(new AbortSequence());
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            // The Debrief still hears of the crossing.
            mission.Send(new KosRunEnded(Finished(), start.RunId));
            DrainUntil(mission, v => transport.Requests.Count == 3 && !v.Busy);
            Assert.Contains("delta-v used 100 m/s over 50 m/s: recorded in a Blackout, not judged", LastUserText(transport.Requests[2]));
        }

        // ---- Helpers ----

        private static string DeltaVCheck(double threshold)
        {
            return "\"telemetry_checks\":[{\"quantity\":\"delta_v_used\",\"resource\":\"\",\"direction\":\"above\",\"threshold\":" + threshold
                + ",\"why\":\"Nothing should burn.\"}],";
        }

        /// <summary>The on-pad Game Snapshot, changed.</summary>
        private static GameSnapshot Snapshot(Action<GameSnapshot> change)
        {
            var snapshot = OnPad();
            change(snapshot);
            return snapshot;
        }

        /// <summary>A Sequence Plan stream with extra fields put in before its <c>"script"</c>.</summary>
        private static ScriptedResponse WithSequenceFields(ScriptedResponse response, string fields)
        {
            return Edited(response, "\\\"script\\\":", fields.Replace("\"", "\\\"") + "\\\"script\\\":");
        }

        /// <summary>A stream with one piece of its text replaced.</summary>
        private static ScriptedResponse Edited(ScriptedResponse response, string from, string to)
        {
            var text = Encoding.UTF8.GetString(response.Body);
            var replaced = text.Replace(from, to);
            Assert.NotEqual(text, replaced);
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(replaced), ChunkSize = 4096 };
        }

        /// <summary>The Debrief fixture, Nominal but with the Mission Goal not met yet, so the loop goes on.</summary>
        private static ScriptedResponse DebriefGoalNotMet()
        {
            return Edited(ScriptedResponse.Fixture("mission-debrief.sse"), "\\\"goal_reached\\\":true", "\\\"goal_reached\\\":false");
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

        private void ToSequencePlan(MissionControl mission, decimal cap = 10m)
        {
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission(Goal, cap, Sonnet));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private StartScript ToRunning(MissionControl mission, decimal cap = 10m)
        {
            ToSequencePlan(mission, cap);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            return mission.TakeCommands().OfType<StartScript>().Single();
        }

        /// <summary>The on-pad Game Snapshot, of a craft with a vessel id (the fixture has none).</summary>
        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = CraftId;
            return snapshot;
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
