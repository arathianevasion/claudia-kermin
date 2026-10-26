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
    /// The effort table (#266) through <see cref="MissionControl"/> with the streaming fake: what effort each request
    /// carries, a changed entry, a bad entry, and the request bodies saved next to the stream dumps.
    /// </summary>
    public sealed class MissionEffortTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string FakeKey = "sk-ant-fake-key-0123456789";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly TempDir _configDir = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
            _configDir.Dispose();
        }

        private PlayerConfig ConfigWith(params string[] lines)
        {
            Directory.CreateDirectory(_configDir.Path);
            File.WriteAllLines(Path.Combine(_configDir.Path, PlayerConfig.FileName), lines);
            return PlayerConfig.LoadOrCreate(_configDir.Path);
        }

        private MissionControl Control(StreamingFakeTransport transport, PlayerConfig config)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => FakeKey, l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, config, () => true, new PassingScriptCheck());
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

        private void Start(MissionControl mission, string goal = Goal)
        {
            mission.Send(new StartMission(goal, 25m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        /// <summary>Plan, then Sequence Plan, then a question: the three request kinds the table covers here.</summary>
        private StreamingFakeTransport FlyThroughAQuestion(PlayerConfig config, string goal = Goal)
        {
            var transport = new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                ScriptedResponse.Fixture("mission-sequence-plan.sse"),
                ScriptedResponse.Fixture("mission-answer.sse"));
            var mission = Control(transport, config);
            Start(mission, goal);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new AskQuestion("How long will it take?"));
            DrainUntil(mission, v => !v.Busy && v.LatestLine == "About 5 seconds, and no engines fire.");
            return transport;
        }

        private static List<JsonElement> Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();
        }

        /// <summary>The effort-only system messages in a request's conversation, in order.</summary>
        private static string[] Efforts(HttpRequestSpec request)
        {
            return Messages(request)
                .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).ToArray();
        }

        /// <summary>The Mission requests (the Craft Check's is not one: it is its own conversation).</summary>
        private static HttpRequestSpec[] MissionRequests(StreamingFakeTransport transport)
        {
            return transport.Requests.Where(r => Messages(r).Any(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))).ToArray();
        }

        [Fact]
        public void WithNoConfigRequestsCarryTodaysEfforts()
        {
            var transport = FlyThroughAQuestion(new PlayerConfig());

            var requests = MissionRequests(transport);
            Assert.Equal(3, requests.Length);
            Assert.Equal(new[] { "high" }, Efforts(requests[0]));
            Assert.Equal(new[] { "high" }, Efforts(requests[1]));
            Assert.Equal(new[] { "high", "low" }, Efforts(requests[2]));
        }

        [Fact]
        public void ChangingOneKindsEntryChangesOnlyThatKindsRequests()
        {
            var transport = FlyThroughAQuestion(ConfigWith("effort.question = medium"));

            var requests = MissionRequests(transport);
            Assert.Equal(new[] { "high" }, Efforts(requests[0]));
            Assert.Equal(new[] { "high" }, Efforts(requests[1]));
            Assert.Equal(new[] { "high", "medium" }, Efforts(requests[2]));
        }

        [Fact]
        public void TheEffortTravelsInTheMessageSoTheCacheIsKept()
        {
            var transport = FlyThroughAQuestion(ConfigWith("effort.question = medium", "effort.sequence_plan = low"));

            var requests = MissionRequests(transport);
            var bodies = requests.Select(r => JsonDocument.Parse(r.Body).RootElement).ToArray();
            foreach (var body in bodies)
            {
                // No top-level effort: output_config holds the format only, and the cached system layers never change.
                Assert.False(body.GetProperty("output_config").TryGetProperty("effort", out _));
                Assert.Equal(bodies[0].GetProperty("system").GetRawText(), body.GetProperty("system").GetRawText());
            }
            for (var i = 1; i < bodies.Length; i++)
            {
                var before = bodies[i - 1].GetProperty("messages").GetRawText();
                Assert.StartsWith(before.Substring(0, before.Length - 1) + ",", bodies[i].GetProperty("messages").GetRawText());
            }
            Assert.Equal(new[] { "high", "low", "medium" }, Efforts(requests[2]));
        }

        [Theory]
        [InlineData("effort.question = banana")]
        [InlineData("effort.question =")]
        [InlineData("effort.question = 5")]
        public void ABadEntryKeepsTheDefaultAndIsLogged(string line)
        {
            var transport = FlyThroughAQuestion(ConfigWith(line, "effort.debrief = medium"));

            Assert.Equal(new[] { "high", "low" }, Efforts(MissionRequests(transport)[2]));
            var events = File.ReadAllText(Path.Combine(MissionFolder(), "events.jsonl"));
            Assert.Contains("effort.question", events);
            Assert.DoesNotContain("effort.debrief", events);
        }

        [Fact]
        public void AnUnknownKindIsIgnoredAndLogged()
        {
            var transport = FlyThroughAQuestion(ConfigWith("effort.sandwich = low"));

            Assert.Equal(new[] { "high", "low" }, Efforts(MissionRequests(transport)[2]));
            Assert.Contains("effort.sandwich", File.ReadAllText(Path.Combine(MissionFolder(), "events.jsonl")));
        }

        [Fact]
        public void AMissingEntryIsTheDefaultAndNothingIsLogged()
        {
            var transport = FlyThroughAQuestion(ConfigWith("debug_tools = false"));

            Assert.Equal(new[] { "high", "low" }, Efforts(MissionRequests(transport)[2]));
            Assert.DoesNotContain("effort", File.ReadAllText(Path.Combine(MissionFolder(), "events.jsonl")));
        }

        [Fact]
        public void EveryMissionRequestBodyIsSavedNextToItsStreamDumpWithTheKeyRedacted()
        {
            // The key typed into the Mission Goal by mistake lands in the request body, but never in the saved one.
            var transport = FlyThroughAQuestion(ConfigWith("debug_tools = true"), "Fly it. My key is " + FakeKey + " by the way.");
            Assert.Contains(FakeKey, transport.Requests[0].Body + transport.Requests[1].Body);

            var dumps = Path.Combine(MissionFolder(), "stream-dumps");
            var bodies = Directory.GetFiles(dumps, "request-*.json");
            var streams = Directory.GetFiles(dumps, "request-*.sse");
            Assert.Equal(transport.Requests.Count, bodies.Length);
            Assert.Equal(streams.Select(s => Path.GetFileNameWithoutExtension(s)).OrderBy(n => n),
                bodies.Select(b => Path.GetFileNameWithoutExtension(b)).OrderBy(n => n));
            foreach (var body in bodies.Select(File.ReadAllText))
            {
                Assert.DoesNotContain(FakeKey, body);
                Assert.DoesNotContain("sk-ant-", body);
                Assert.DoesNotContain("x-api-key", body, StringComparison.OrdinalIgnoreCase);
                using (JsonDocument.Parse(body)) { } // still the body as JSON
            }
            Assert.Contains(bodies.Select(File.ReadAllText), b => b.Contains("[redacted]"));
        }

        [Fact]
        public void NoBodiesAreSavedWithoutDebugTools()
        {
            FlyThroughAQuestion(new PlayerConfig());

            Assert.False(Directory.Exists(Path.Combine(MissionFolder(), "stream-dumps")));
        }

        private string MissionFolder()
        {
            return Directory.GetDirectories(Path.Combine(_save.Path, "ClaudiaKermin", "missions")).Single();
        }
    }
}
