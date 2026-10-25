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
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Runs the mod stops itself (ADR 0003): the runaway hard stop, timed in UT against the Sequence Plan's planned
    /// duration, and a runtime gate trip reported by the kOS bridge. Both end in a Debrief that gives the reason.
    /// </summary>
    public sealed class MissionStopTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        /// <summary>The UT in the on-pad Game Snapshot, so the UT at Approval.</summary>
        private const double ApprovedAt = 1203.48;
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- The deadline ----

        [Fact]
        public void TheDeadlineIsTheMultiplierTimesThePlannedDuration()
        {
            Assert.Equal(1000 + 1800, RunawayStop.Deadline(1000, 600, 3));
            Assert.Equal(1000 + 1200, RunawayStop.Deadline(1000, 600, 2));
        }

        [Fact]
        public void APlanWithNoRealDurationIsHeldToAMinimum()
        {
            Assert.Equal(1000 + 180, RunawayStop.Deadline(1000, 0, 3));
            Assert.Equal(1000 + 180, RunawayStop.Deadline(1000, 5, 3));
            Assert.Equal(1000 + 180, RunawayStop.Deadline(1000, double.NaN, 3));
        }

        // ---- The hard stop in a Mission ----

        [Fact]
        public void ARunStillGoingAtThreeTimesItsPlannedDurationIsAbortedAndDebriefedAsTimedOut()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);

            mission.Send(new UtAdvanced(ApprovedAt + 1799));
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            mission.Send(new UtAdvanced(ApprovedAt + 1800));
            var abort = mission.TakeCommands().OfType<AbortScript>().Single();
            Assert.Equal(start.ProcessorPartId, abort.ProcessorPartId);
            Assert.Equal(start.RunId, abort.RunId);
            Assert.Equal("timed out", abort.Reason);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("300% of its planned 600 s"));
            // The Mission stays Running until the program has actually ended.
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            // Only once.
            mission.Send(new UtAdvanced(ApprovedAt + 5000));
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());

            // The bridge stopped the program (BreakExecution, make-safe) and reports the abort.
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Aborted, null, "Program aborted.", "timed out", true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("kOS run ended: Aborted (timed out).", LastUserText(transport.Requests[2]));
            Assert.Equal("Debrief: Nominal (timed out)", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Title);
        }

        [Fact]
        public void TheMultiplierComesFromThePlayerConfig()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig { RunawayMultiplier = 2 });
            RunToRunning(mission);

            mission.Send(new UtAdvanced(ApprovedAt + 1199));
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());
            mission.Send(new UtAdvanced(ApprovedAt + 1200));
            Assert.Single(mission.TakeCommands().OfType<AbortScript>());
            Assert.Contains(mission.View.Log, e => e.Text.Contains("200% of its planned 600 s"));
        }

        [Fact]
        public void ARunThatEndsInTimeIsNeverStopped()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);

            mission.Send(new KosRunEnded(Finished(), start.RunId));
            mission.Send(new UtAdvanced(ApprovedAt + 100000));

            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("kOS run ended: Finished.", LastUserText(transport.Requests[2]));
            Assert.Equal("Debrief: Nominal", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Title);
        }

        // ---- A runtime gate trip ----

        [Fact]
        public void AGateTripIsDebriefedAsBlockedWithWhatItTried()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);

            mission.Send(new KosRunBlocked("KUNIVERSE:QUICKSAVE", start.RunId));
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("blocked: KUNIVERSE:QUICKSAVE"));
            // The bridge stops the program and makes the craft safe itself: no second abort from here.
            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());
            Assert.Equal(MissionStage.Running, mission.View.Stage);

            // kOS printed the refusal as the program's error; whatever the bridge called the end, the Debrief says blocked.
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.NoReport, null, "Claudia Kermin refused KUNIVERSE:QUICKSAVE", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            var report = LastUserText(transport.Requests[2]);
            Assert.Contains("kOS run ended: Aborted (blocked: KUNIVERSE:QUICKSAVE).", report);
            Assert.Contains("Claudia Kermin refused KUNIVERSE:QUICKSAVE", report);
            Assert.Equal("Debrief: Nominal (blocked: KUNIVERSE:QUICKSAVE)", mission.View.Log.Last(e => e.Kind == LogKind.Debrief).Title);
        }

        [Fact]
        public void ATripFromAnEarlierRunIsIgnored()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);

            mission.Send(new KosRunBlocked("KUNIVERSE:QUICKSAVE", start.RunId - 1));
            mission.Send(new KosRunEnded(Finished(), start.RunId));

            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);
            Assert.Contains("kOS run ended: Finished.", LastUserText(transport.Requests[2]));
        }

        [Fact]
        public void AGateTripBeforeTheDeadlineIsNotAlsoATimeOut()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);

            mission.Send(new KosRunBlocked("physics warp (warp mode PHYSICS)", start.RunId));
            mission.Send(new UtAdvanced(ApprovedAt + 1800));

            Assert.Empty(mission.TakeCommands().OfType<AbortScript>());
        }

        [Fact]
        public void TheStopReasonSurvivesASaveBeforeTheDebrief()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);
            mission.Send(new KosRunBlocked("SET CONFIG:IPU", start.RunId));

            var saved = mission.ExportStates().Single();
            Assert.Equal("blocked: SET CONFIG:IPU", saved.StopReason);
            Assert.Equal("blocked: SET CONFIG:IPU", MissionSaveState.FromValues(saved.ToValues()).StopReason);
        }

        // ---- The stop reason after a load (#202) ----

        [Fact]
        public void AnAbortedRunsDebriefKeepsItsStopReasonInTheRebuiltLog()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);
            mission.Send(new AbortSequence());
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Aborted, null, "Program aborted.", "the player pressed Abort", true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            AssertSameDebriefAfterALoad(mission, "Debrief: Nominal (aborted by the player)");
        }

        [Fact]
        public void ABlockedRunsDebriefKeepsItsStopReasonInTheRebuiltLog()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);
            mission.Send(new KosRunBlocked("KUNIVERSE:QUICKSAVE", start.RunId));
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.NoReport, null, "Claudia Kermin refused KUNIVERSE:QUICKSAVE", "", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            AssertSameDebriefAfterALoad(mission, "Debrief: Nominal (blocked: KUNIVERSE:QUICKSAVE)");
        }

        [Fact]
        public void ATimedOutRunsDebriefKeepsItsStopReasonInTheRebuiltLog()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);
            mission.Send(new UtAdvanced(ApprovedAt + 1800));
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Aborted, null, "Program aborted.", "timed out", true), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            AssertSameDebriefAfterALoad(mission, "Debrief: Nominal (timed out)");
        }

        [Fact]
        public void ARunThatEndedWithAReasonOfItsOwnAddsNoSuffixAfterALoad()
        {
            var transport = Transport(plannedSeconds: 600);
            var mission = InFlight(transport, new PlayerConfig());
            var start = RunToRunning(mission);
            mission.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Interrupted, null, "", "the game was loaded, quit or crashed while it ran", false), start.RunId));
            DrainUntil(mission, v => v.Stage == MissionStage.Succeeded);

            AssertSameDebriefAfterALoad(mission, "Debrief: Nominal");
        }

        private void AssertSameDebriefAfterALoad(MissionControl mission, string title)
        {
            var live = mission.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal(title, live.Title);
            var saved = mission.ExportStates();

            var client = new ClaudeClient(new CraftCheckRouter(new StreamingFakeTransport()), () => "sk-ant-test", l => { });
            var later = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            later.Send(new MissionsRestored(_save.Path, saved));
            var rebuilt = later.View.Log.Last(e => e.Kind == LogKind.Debrief);
            Assert.Equal(live.Title, rebuilt.Title);
            Assert.Equal(live.Text, rebuilt.Text);
            Assert.Equal(live.Why, rebuilt.Why);
            Assert.Equal(live.Outcome, rebuilt.Outcome);
        }

        // ---- Helpers ----

        /// <summary>The Mission Plan, a Sequence Plan planning <paramref name="plannedSeconds"/>, and a Nominal Debrief.</summary>
        private static StreamingFakeTransport Transport(double plannedSeconds)
        {
            var sequence = Encoding.UTF8.GetString(Fixtures.Bytes("mission-sequence-plan.sse"))
                .Replace("expected_duration_s\\\":5,", "expected_duration_s\\\":" + plannedSeconds + ",");
            Assert.Contains("expected_duration_s\\\":" + plannedSeconds + ",", sequence);
            return new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sequence), ChunkSize = 4096 },
                ScriptedResponse.Fixture("mission-debrief.sse"));
        }

        private MissionControl InFlight(StreamingFakeTransport transport, PlayerConfig config)
        {
            // These runs are about the hard stop: the default duration Telemetry Check (150%) would send an Alert first.
            config.CheckDurationPercent = 1000;
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private StartScript RunToRunning(MissionControl mission)
        {
            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5"));
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
            return new RunOutcome(RunOutcomeKind.Finished, new System.Collections.Generic.Dictionary<string, object> { { "success", true } }, "", "", false);
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
