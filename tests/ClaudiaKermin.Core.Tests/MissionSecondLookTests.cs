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
    /// The Second look (#272) through <see cref="MissionControl"/> with the streaming fake: an unsure Debrief, Craft Check or
    /// Margin recheck is dropped and sent again at high effort with the reason; once per turn, never with a cut-off retry, skipped
    /// without Spending Cap room, and ignored for kinds that never get one.
    /// </summary>
    public sealed class MissionSecondLookTests : IDisposable
    {
        private const string Goal = "Prove the kOS link from the launch pad.";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Reason = "the fuel figure may be stale";
        private const string Line = "A first look was unsure because: the fuel figure may be stale. Think it through carefully.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        // ---- A Debrief ----

        [Fact]
        public void AnUnsureMediumDebriefIsDroppedAndSentAgainAtHighWithTheReasonAndNeverCommitted()
        {
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("considered draft", ""), unsure: Reason, mediums: "debrief");
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && rig.Transport.Requests.Count == 5);

            // Mission Plan, Sequence Plan, the unsure Debrief, the same Debrief again, then the next Sequence Plan.
            var first = rig.Transport.Requests[2];
            var second = rig.Transport.Requests[3];
            Assert.Equal(new[] { "high", "medium" }, Efforts(first));
            Assert.DoesNotContain("A first look was unsure", first.Body);
            // High is the conversation's own effort (the plan's), so the resend needs no effort message of its own.
            Assert.Equal(new[] { "high" }, Efforts(second));
            Assert.Equal(1, second.Body.Split(new[] { Line }, StringSplitOptions.None).Length - 1);
            // The same turn: the margin recheck's facts and the run's report ride along unchanged.
            Assert.Equal(Messages(first).Count - 1, Messages(second).Count);
            Assert.Contains("The mod's margin recheck", LastUserText(second));
            // The unsure reply never joined the conversation; the considered one did.
            var later = string.Join("", Messages(rig.Transport.Requests[4]).Select(m => m.GetRawText()));
            Assert.DoesNotContain("hasty draft", later);
            Assert.Contains("considered draft", later);
        }

        [Fact]
        public void TheCapcomStripSaysClaudiaIsTakingASecondLookAndTheMissionLogHasAnEntry()
        {
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("considered draft", ""), unsure: Reason, mediums: "debrief", hold: true);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Activity == "taking a second look");

            Assert.True(rig.Mission.View.Busy);
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("taking a second look") && e.Text.Contains(Reason));
            rig.Second.Gate.Set();
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && rig.Transport.Requests.Count == 5);
            Assert.Equal("", rig.Mission.View.Activity);
        }

        [Fact]
        public void ASecondReplyThatIsUnsureToIsKeptAndOnlyLoggedSoATurnNeverLoops()
        {
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("considered draft", "still unsure about the burn"), unsure: Reason, mediums: "debrief");
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && rig.Transport.Requests.Count == 5);

            Assert.Equal(5, rig.Transport.Requests.Count);
            Assert.Contains("considered draft", string.Join("", Messages(rig.Transport.Requests[4]).Select(m => m.GetRawText())));
            Assert.Contains("still unsure about the burn", EventLog(rig));
        }

        [Fact]
        public void ADebriefAlreadyAtHighHasNothingHarderToTry()
        {
            var rig = new Rig(this, Debrief("only look"), null, unsure: Reason, mediums: "");
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(4, rig.Transport.Requests.Count);
            Assert.DoesNotContain(rig.Transport.Requests, r => r.Body.Contains("A first look was unsure"));
        }

        [Fact]
        public void AReplyOfAKindThatNeverGetsOneIgnoresTheField()
        {
            // The Sequence Plan runs at medium here, and says it is unsure; the mod ignores it until it joins (#287).
            var rig = new Rig(this, Debrief("a debrief", ""), null, unsure: "", mediums: "sequence_plan", sequenceUnsure: Reason);
            var start = rig.RunToRunning();

            Assert.Equal(MissionStage.Running, rig.Mission.View.Stage);
            Assert.Equal(2, rig.Transport.Requests.Count);
            Assert.DoesNotContain(rig.Transport.Requests, r => r.Body.Contains("A first look was unsure"));
            Assert.NotNull(start);
        }

        [Fact]
        public void WithNoSpendingCapRoomTheFirstReplyIsKeptAndTheSkipIsNoted()
        {
            // Room for the plan, sequence and Debrief, but not for the Debrief again after the first one's real cost.
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("never sent", ""), unsure: Reason, mediums: "debrief", cap: 0.6m, firstDebriefOutputTokens: 30000);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage != MissionStage.Running && !v.Busy);

            Assert.DoesNotContain(rig.Transport.Requests, r => r.Body.Contains("A first look was unsure"));
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("A second look was skipped: it could pass the Spending Cap") && e.Text.Contains(Reason));
            Assert.Contains(rig.CostLog(), c => c.Event == "blocked" && c.Outcome == "over_cap");
            // The first reply stands: its Debrief card is in the log. (The Mission then Pauses for the next Sequence Plan, which the cap cannot cover either.)
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Debrief);
            Assert.Equal(3, rig.Transport.Requests.Count);
        }

        [Fact]
        public void ACutOffSecondLookIsNotRetriedAutomaticallyAndRetrySendsTheReducedForm()
        {
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("never", ""), unsure: Reason, mediums: "debrief", secondLook: ScriptedResponse.Fixture("max-tokens-tool.sse"));
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.Paused);

            // Mission Plan, Sequence Plan, the unsure Debrief, its Second look: no cut-off retry stacked on top.
            Assert.Equal(4, rig.Transport.Requests.Count);
            Assert.Equal("Claudia's reply was cut short.", rig.Mission.View.PauseReason);
            rig.Transport.Add(Reply(Debrief("third", "")));
            rig.Mission.Send(new Retry());
            rig.DrainUntil(v => rig.Transport.Requests.Count == 5);
            var retry = rig.Transport.Requests[4].Body;
            Assert.Contains("Your last attempt ran out of room while thinking.", retry);
            Assert.DoesNotContain("A first look was unsure", retry);
        }

        [Fact]
        public void ASecondLookInFlightWhenTheMissionIsSavedAndLoadedIsSentAgainInItsHighForm()
        {
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("considered draft", ""), unsure: Reason, mediums: "debrief", hold: true);
            var start = rig.RunToRunning();
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Activity == "taking a second look");

            var unanswered = MissionStore.ReadUnanswered(Path.Combine(_save.Path, "ClaudiaKermin", "missions", rig.Mission.View.MissionId));
            Assert.Equal(Reason, unanswered.SecondLookReason);
            Assert.Equal("high", unanswered.Effort);
            rig.Second.Gate.Set();
        }

        // ---- The Margin recheck ----

        [Fact]
        public void AnUnsureMediumMarginRecheckIsSentAgainAtHighWithTheReasonAndOnlyTheSecondAnswerReachesTheDebrief()
        {
            var rig = new Rig(this, Debrief("debrief", ""), null, unsure: "", mediums: "margin_recheck");
            rig.Router.Rechecks.Enqueue(CheckReply("The margin looks thin.", Reason));
            rig.Router.Rechecks.Enqueue(CheckReply("3,200 m/s left against 1,000 m/s to fly.", ""));
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval || v.Stage == MissionStage.Sequencing);

            Assert.Equal(checksBefore + 2, rig.Router.CheckRequests.Count);
            var first = rig.Router.CheckRequests[checksBefore].Body;
            var second = rig.Router.CheckRequests[checksBefore + 1].Body;
            Assert.Contains("\"effort\":\"medium\"", first);
            Assert.Contains("\"effort\":\"high\"", second);
            Assert.DoesNotContain("A first look was unsure", first);
            Assert.Contains(Line, second.Replace("\\n", " "));
            var debrief = LastUserText(rig.Transport.Requests[2]);
            Assert.Contains("3,200 m/s left against 1,000 m/s to fly.", debrief);
            Assert.DoesNotContain("The margin looks thin.", debrief);
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("margin recheck") && e.Text.Contains("taking a second look"));
            // Both recheck requests are charged to the Mission.
            Assert.Equal(2, rig.CostLog().Count(c => c.Outcome == "margin_recheck"));
        }

        [Fact]
        public void ADebriefAndItsMarginRecheckEachGetOneSecondLook()
        {
            var rig = new Rig(this, Debrief("hasty draft"), Debrief("considered draft", ""), unsure: Reason, mediums: "debrief,margin_recheck");
            rig.Router.Rechecks.Enqueue(CheckReply("thin", Reason));
            rig.Router.Rechecks.Enqueue(CheckReply("considered margin", ""));
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval && rig.Transport.Requests.Count == 5);

            Assert.Equal(checksBefore + 2, rig.Router.CheckRequests.Count);
            Assert.DoesNotContain(Line, rig.Transport.Requests[2].Body);
            Assert.Contains(Line, rig.Transport.Requests[3].Body);
            Assert.Contains("considered margin", LastUserText(rig.Transport.Requests[3]));
        }

        [Fact]
        public void ASecondMarginRecheckThatIsUnsureToIsKeptAndOnlyLogged()
        {
            var rig = new Rig(this, Debrief("debrief", ""), null, unsure: "", mediums: "margin_recheck");
            rig.Router.Rechecks.Enqueue(CheckReply("thin", Reason));
            rig.Router.Rechecks.Enqueue(CheckReply("considered margin", "still not sure"));
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval || v.Stage == MissionStage.Sequencing);

            Assert.Equal(checksBefore + 2, rig.Router.CheckRequests.Count);
            Assert.Contains("considered margin", LastUserText(rig.Transport.Requests[2]));
            Assert.Contains("still not sure", EventLog(rig));
        }

        [Fact]
        public void WithNoRoomForAMarginRecheckSecondLookTheFirstAnswerIsKeptAndTheSkipNoted()
        {
            var rig = new Rig(this, Debrief("debrief", ""), null, unsure: "", mediums: "margin_recheck", cap: 0.6m);
            rig.Router.Rechecks.Enqueue(CheckReply("thin but usable", Reason, outputTokens: 15000));
            var start = rig.RunToRunning();
            var checksBefore = rig.Router.CheckRequests.Count;
            rig.Mission.Send(new KosRunEnded(Finished(), start.RunId));
            rig.DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval || v.Stage == MissionStage.Sequencing);

            Assert.Equal(checksBefore + 1, rig.Router.CheckRequests.Count);
            Assert.Contains("thin but usable", LastUserText(rig.Transport.Requests[2]));
            Assert.Contains(rig.Mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("A second look at the margin recheck was skipped"));
        }

        // ---- The Craft Check ----

        [Fact]
        public void AnUnsureMediumCraftCheckIsSentAgainAtHighWithTheReasonAndOnlyTheSecondVerdictCounts()
        {
            var transport = new StreamingFakeTransport(CheckReply("Thin but possible.", Reason), CheckReply("Plenty of delta-v.", ""));
            var mission = CheckControl(transport, "craft_check");
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, 10m, Sonnet));
            DrainCheck(mission);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Contains("\"effort\":\"medium\"", transport.Requests[0].Body);
            Assert.Contains("\"effort\":\"high\"", transport.Requests[1].Body);
            Assert.DoesNotContain("A first look was unsure", transport.Requests[0].Body);
            Assert.Contains(Line, transport.Requests[1].Body);
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
            Assert.Equal("Plenty of delta-v.", mission.View.CraftCheck.Summary);
        }

        [Fact]
        public void ACraftChecksSecondLookReplyThatIsUnsureToStandsAndThereIsNoThird()
        {
            var transport = new StreamingFakeTransport(CheckReply("Thin.", Reason), CheckReply("Still thin.", "still unsure"), CheckReply("never", ""));
            var mission = CheckControl(transport, "craft_check");
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, 10m, Sonnet));
            DrainCheck(mission);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("Still thin.", mission.View.CraftCheck.Summary);
        }

        [Fact]
        public void ACraftCheckWithNoCapRoomForASecondLookKeepsTheFirstReply()
        {
            // The cap allows one check (worst case about $0.1) but not two.
            var transport = new StreamingFakeTransport(CheckReply("Thin but usable.", Reason, outputTokens: 8000), CheckReply("never", ""));
            var mission = CheckControl(transport, "craft_check");
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, 0.15m, Sonnet));
            DrainCheck(mission);

            Assert.Single(transport.Requests);
            Assert.Equal("Thin but usable.", mission.View.CraftCheck.Summary);
        }

        [Fact]
        public void ACraftCheckAtHighIsLeftAlone()
        {
            var transport = new StreamingFakeTransport(CheckReply("Thin.", Reason), CheckReply("never", ""));
            var mission = CheckControl(transport);
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, 10m, Sonnet));
            DrainCheck(mission);

            Assert.Single(transport.Requests);
        }

        [Fact]
        public void TheCapcomStripSaysSecondLookWhileTheCraftCheckRunsAgain()
        {
            var second = CheckReply("Plenty.", "");
            second.PauseAt = 0;
            var transport = new StreamingFakeTransport(CheckReply("Thin.", Reason), second);
            var mission = CheckControl(transport, "craft_check");
            mission.Send(OnPad());
            mission.Send(new RunCraftCheck(Goal, 10m, Sonnet));
            DrainUntil(mission, v => v.Activity == "taking a second look");
            second.Gate.Set();
            DrainCheck(mission);
        }

        // ---- The schemas ----

        [Fact]
        public void EveryMissionSchemaAndTheCraftCheckSchemaRequireATopLevelUnsureBecauseString()
        {
            foreach (var schema in new[] { MissionReply.PlanningSchemaJson, MissionReply.FlightSchemaJson, CraftCheckRules.SchemaJson() })
                using (var doc = JsonDocument.Parse(schema))
                {
                    Assert.Contains("unsure_because", doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
                    Assert.Equal("string", doc.RootElement.GetProperty("properties").GetProperty("unsure_because").GetProperty("type").GetString());
                }
        }

        // ---- Helpers ----

        private MissionControl CheckControl(StreamingFakeTransport transport, string medium = null)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var config = new PlayerConfig();
            if (medium != null) config.Efforts.Read(EffortTable.Prefix + medium, "medium");
            var mission = new MissionControl(client, _background, config, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            return mission;
        }

        private void DrainCheck(MissionControl mission)
        {
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason + " check " + mission.View.CraftCheck.State);
        }

        private static string EventLog(Rig rig)
        {
            var path = Path.Combine(rig.Save, "ClaudiaKermin", "missions", rig.Mission.View.MissionId);
            return string.Concat(Directory.GetFiles(path, "*.jsonl").Select(File.ReadAllText));
        }

        private static string Debrief(string say, string unsure = null)
        {
            // An unsure first reply is built by the rig (unsure: ...); a reply that is sure or has its own doubt names it here.
            return "{\"kind\":\"debrief\",\"say\":\"" + say + "\",\"reply_intent\":\"none\",\"unsure_because\":\"" + (unsure ?? "@@") + "\","
                + "\"mission_plan\":null,\"sequence_plan\":null,\"alert\":null,"
                + "\"debrief\":{\"outcome\":\"Nominal\",\"why\":\"It went.\",\"what_happened\":\"It went.\",\"goal_reached\":false,\"next\":\"Fly on.\",\"eva_science\":\"\"}}";
        }

        private static ScriptedResponse CheckReply(string summary, string unsure, int outputTokens = 90)
        {
            return Reply("{\"summary\":\"" + summary + "\",\"findings\":[],\"child_vessel_stages\":[],\"unsure_because\":\"" + unsure + "\"}", outputTokens);
        }

        private static ScriptedResponse Reply(string json, int outputTokens = 90)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = new StringBuilder();
            sse.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3900,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n");
            sse.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            sse.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n");
            sse.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            sse.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":" + outputTokens + ",\"iterations\":[{\"type\":\"message\",\"input_tokens\":3900,\"output_tokens\":" + outputTokens + "}]}}\n\n");
            sse.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static List<JsonElement> Messages(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
                return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();
        }

        private static string[] Efforts(HttpRequestSpec request)
        {
            return Messages(request)
                .Where(m => m.GetProperty("role").GetString() == "system" && m.TryGetProperty("output_config", out _))
                .Select(m => m.GetProperty("output_config").GetProperty("effort").GetString()).ToArray();
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            var user = Messages(request).Last(m => m.GetProperty("role").GetString() == "user");
            var content = user.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        }

        private sealed class CostLine
        {
            public string Event;
            public string Outcome;
        }

        /// <summary>A Mission with a two-sequence plan, one Sequence Plan, the Debrief under test and the replies after it.</summary>
        private sealed class Rig
        {
            private readonly MissionSecondLookTests _tests;
            private readonly decimal _cap;
            public readonly StreamingFakeTransport Transport;
            public readonly CraftCheckRouter Router;
            public readonly MissionControl Mission;

            /// <param name="firstDebrief">The Debrief reply; "@@" in its unsure_because is replaced by <paramref name="unsure"/>.</param>
            /// <param name="after">The reply to the resent Debrief, when the first was unsure; null for none.</param>
            /// <param name="mediums">Effort kinds, comma separated, set to medium.</param>
            public Rig(MissionSecondLookTests tests, string firstDebrief, string after, string unsure, string mediums, decimal cap = 10m,
                int firstDebriefOutputTokens = 90, ScriptedResponse secondLook = null, string sequenceUnsure = "", bool hold = false)
            {
                _tests = tests;
                _cap = cap;
                Transport = new StreamingFakeTransport(
                    PlanReply(),
                    SequenceReply(sequenceUnsure),
                    Reply(firstDebrief.Replace("@@", unsure), firstDebriefOutputTokens));
                if (after != null)
                {
                    Second = secondLook ?? Reply(after.Replace("@@", ""));
                    if (hold) Second.PauseAt = 0;
                    Transport.Add(Second);
                }
                // The next Sequence Plan follows the Debrief.
                Transport.Add(SequenceReply(""));
                Router = new CraftCheckRouter(Transport);
                var client = new ClaudeClient(Router, () => "sk-ant-test", l => { });
                client.Timing.Wait = (d, c) => { };
                var config = new PlayerConfig { CheckDurationPercent = 1000 };
                foreach (var kind in mediums.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) config.Efforts.Read(EffortTable.Prefix + kind, "medium");
                Mission = new MissionControl(client, tests._background, config, () => true, new PassingScriptCheck());
                Mission.Send(new SaveLoaded(tests._save.Path));
                Mission.Send(new SceneChanged(GameScene.Flight));
                Mission.Send(OnPad());
            }

            public string Save
            {
                get { return _tests._save.Path; }
            }

            /// <summary>The reply to the resent Debrief; with <c>hold</c> it pauses until its Gate is set.</summary>
            public ScriptedResponse Second;

            private static ScriptedResponse PlanReply()
            {
                return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                    + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                    + "{\"name\":\"Link check\",\"intent\":\"Prove the link.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                    + "{\"name\":\"Second leg\",\"intent\":\"Go on.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                    + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1}}");
            }

            private static ScriptedResponse SequenceReply(string unsure)
            {
                var fixture = Fixtures.Text("mission-sequence-plan.sse");
                return new ScriptedResponse
                {
                    Body = Encoding.UTF8.GetBytes(unsure.Length == 0 ? fixture
                        : fixture.Replace("\\\"reply_intent\\\":\\\"none\\\",", "\\\"reply_intent\\\":\\\"none\\\",\\\"unsure_because\\\":\\\"" + unsure + "\\\","))
                };
            }

            public StartScript RunToRunning()
            {
                CraftCheckRouter.StartChecked(Mission, _tests._background, OnPad(), new StartMission(Goal, _cap, Sonnet));
                DrainUntil(v => v.Stage == MissionStage.AwaitingPlanApproval);
                Mission.Send(new Approve());
                DrainUntil(v => v.Stage == MissionStage.AwaitingSequenceApproval);
                Mission.Send(new Approve());
                return Mission.TakeCommands().OfType<StartScript>().Single();
            }

            public void DrainUntil(Func<MissionView, bool> condition)
            {
                _tests.DrainUntil(Mission, condition);
            }

            public IList<CostLine> CostLog()
            {
                var path = Path.Combine(_tests._save.Path, "ClaudiaKermin", "missions", Mission.View.MissionId, "cost-log.jsonl");
                return File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line =>
                {
                    var root = JsonDocument.Parse(line).RootElement;
                    JsonElement outcome;
                    return new CostLine
                    {
                        Event = root.GetProperty("event").GetString(),
                        Outcome = root.TryGetProperty("outcome", out outcome) ? outcome.GetString() : "",
                    };
                }).ToList();
            }
        }
    }
}
