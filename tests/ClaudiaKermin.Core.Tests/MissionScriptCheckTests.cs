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
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Script Check in the Mission loop (ADR 0003), through the core's seam with a fake check: no script
    /// reaches Approval unless it passes. A failed script goes back to Claudia for one automatic rewrite; a
    /// second failure shows "Couldn't produce a safe script" with Retry, Retry with a note or Re-plan.
    /// </summary>
    public sealed class MissionScriptCheckTests : IDisposable
    {
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly FakeScriptCheck _check = new FakeScriptCheck();

        private readonly List<ScriptedResponse> _gates = new List<ScriptedResponse>();

        public void Dispose()
        {
            // Let any request left in flight by a "crashed" Mission finish into a queue nobody drains.
            foreach (var response in _gates) response.Gate.Set();
            _save.Dispose();
        }

        /// <summary>Fails any script that quicksaves, the way the real check would (or, with <see cref="FailEverything"/>, any script).</summary>
        private sealed class FakeScriptCheck : IScriptCheck
        {
            public readonly List<string> Checked = new List<string>();
            public bool FailEverything;

            public ScriptCheckResult Check(string script)
            {
                Checked.Add(script);
                return FailEverything || script.ToUpperInvariant().Contains("QUICKSAVE")
                    ? new ScriptCheckResult(null, new List<string> { "line 1: :QUICKSAVE (quicksave)" })
                    : ScriptCheckResult.Pass();
            }
        }

        /// <param name="background">A queue of its own for a "crashed" game, drained only by the test.</param>
        private MissionControl Control(StreamingFakeTransport transport, Background background = null)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, background ?? _background, new PlayerConfig(), () => true, _check);
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition, Background background = null)
        {
            background = background ?? _background;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                background.Drain();
                Thread.Sleep(2);
            }
            background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        /// <summary>Starts a Mission in Flight and approves its Mission Plan, so the next reply is a Sequence Plan.</summary>
        private void ApproveMissionPlan(MissionControl mission, decimal cap = 10m, Background background = null)
        {
            StartInFlight(mission, cap, background);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval, background);
            mission.Send(new Approve());
        }

        /// <summary>Start Mission in Flight, through the Craft Check (which passes).</summary>
        private void StartInFlight(MissionControl mission, decimal cap = 10m, Background background = null)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, background ?? _background, OnPad(), new StartMission("Prove the kOS link from the launch pad.", cap, "claude-sonnet-5-5"));
        }

        /// <summary>A load of a save holding <paramref name="saved"/>, into a fresh game (after a quit or crash), through the save file's plain values.</summary>
        private MissionControl Resumed(StreamingFakeTransport transport, IList<MissionSaveState> saved)
        {
            var resumed = Control(transport);
            resumed.Send(new SceneChanged(GameScene.Flight));
            resumed.Send(new MissionsRestored(_save.Path, saved.Select(s => MissionSaveState.FromValues(s.ToValues())).ToList()));
            resumed.Send(OnPad());
            return resumed;
        }

        private string[] CostEvents(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKermin", "missions", mission.View.MissionId, "cost-log.jsonl");
            return File.ReadAllLines(path).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToArray();
        }

        private static GameSnapshot OnPad()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
        }

        private static StreamingFakeTransport Replies(params string[] fixtures)
        {
            return new StreamingFakeTransport(fixtures.Select(f => ScriptedResponse.Fixture(f)).ToArray());
        }

        /// <summary>The text of the last user message and system notices of a request, as Claude reads them.</summary>
        private static string LastTurnText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
                var lastAssistant = messages.FindLastIndex(m => m.GetProperty("role").GetString() == "assistant");
                return string.Join("\n", messages.Skip(lastAssistant + 1).Select(m => m.GetRawText()));
            }
        }

        private static IEnumerable<LogEntry> SequenceCards(MissionView view)
        {
            return view.Log.Where(e => e.Kind == LogKind.SequencePlan);
        }

        [Fact]
        public void AFailedScriptIsRewrittenOnceAutomaticallyAndThePlayerNeverSeesIt()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            // One billed rewrite: the failing reply went back to Claudia with what the Script Check found.
            Assert.Equal(3, transport.Requests.Count);
            var rewrite = LastTurnText(transport.Requests[2]);
            Assert.Contains("line 1: :QUICKSAVE (quicksave)", rewrite);
            Assert.Contains("Script Check", rewrite);

            // The player sees only the rewritten plan: no card, line or script from the rejected one.
            var card = Assert.Single(SequenceCards(mission.View));
            Assert.DoesNotContain("QUICKSAVE", card.Script);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("quicksave first") || e.Script.Contains("QUICKSAVE"));
            Assert.Equal("Sequence Plan: link check on the pad, no save, 0 m/s, 5 s.", mission.View.LatestLine);
            Assert.Equal(0, mission.View.CurrentSequence);

            // The script that starts is the one that passed, bundled with the Core Library, and the bundle is what was checked.
            mission.Send(new Approve());
            var start = Assert.Single(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(CoreLibrary.Bundle(card.Script, 1, 10800), start.Source);
            Assert.Contains(start.Source, _check.Checked);
        }

        [Fact]
        public void ASecondFailureShowsCouldntProduceASafeScriptAndNothingCanBeApproved()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-unsafe.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);

            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan }, mission.View.QuickReplies);
            var card = Assert.Single(SequenceCards(mission.View));
            Assert.Contains("Couldn't produce a safe script", card.Text);
            Assert.Contains("quicksave", card.Text);
            Assert.Equal("", card.Script);
            Assert.DoesNotContain(mission.View.Log, e => e.Script.Contains("QUICKSAVE"));

            mission.Send(new Approve());
            mission.Send(new ConfirmApproval(true));
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.UnsafeScript, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count);
        }

        [Fact]
        public void RetryAsksForTheScriptAgainAndAPassingOneCanBeApproved()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-unsafe.sse",
                "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Contains("line 1: :QUICKSAVE (quicksave)", LastTurnText(transport.Requests[3]));
            Assert.Equal(0, mission.View.CurrentSequence);

            mission.Send(new Approve());
            Assert.DoesNotContain("QUICKSAVE", Assert.Single(mission.TakeCommands().OfType<StartScript>()).Source);
        }

        [Fact]
        public void ARetryThatFailsAgainGoesStraightBackWithNoFurtherAutomaticRewrite()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-unsafe.sse",
                "mission-sequence-plan-unsafe.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);

            mission.Send(new Retry());
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript && !v.Busy);
            Assert.Equal(4, transport.Requests.Count);
        }

        [Fact]
        public void RetryWithANoteSendsThePlayersNoteAsQuotedData()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-unsafe.sse",
                "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);
            Assert.True(mission.View.CanType);

            mission.Send(new RetryWithNote("Skip the save; I'll quicksave myself."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var retry = LastTurnText(transport.Requests[3]);
            Assert.Contains("Skip the save; I'll quicksave myself.", retry);
            Assert.Contains("quoted data", retry);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Player && e.Text.Contains("Skip the save"));
        }

        [Fact]
        public void ReplanAsksForADifferentSequencePlanWhichGetsItsOwnAutomaticRewrite()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-unsafe.sse",
                "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);

            mission.Send(new Replan());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(5, transport.Requests.Count);
            Assert.Contains("different Sequence Plan", LastTurnText(transport.Requests[3]));
            Assert.Equal(0, mission.View.CurrentSequence);
        }

        [Fact]
        public void ARevisedSequencePlanIsCheckedToo()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse", "mission-sequence-plan-unsafe.sse",
                "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            mission.Send(new ChangeSomething("Quicksave first, please."));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval && !v.Busy && transport.Requests.Count == 4);
            Assert.Contains("line 1: :QUICKSAVE (quicksave)", LastTurnText(transport.Requests[3]));
            Assert.DoesNotContain(mission.View.Log, e => e.Script.Contains("QUICKSAVE"));
            Assert.Equal(0, mission.View.CurrentSequence);
        }

        [Fact]
        public void APlanUsingACustomScriptShowsTheCustomScriptBadge()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var card = Assert.Single(SequenceCards(mission.View));
            Assert.True(card.CustomScript);
            Assert.Equal("No Building Block reports a status from the pad.", card.CustomScriptReason);
        }

        [Fact]
        public void APlanBuiltFromBuildingBlocksHasNoCustomScriptBadge()
        {
            // mission-sequence-plan.sse leaves custom_script out, which reads as false.
            var transport = Replies("mission-plan.sse", "mission-sequence-plan.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.False(Assert.Single(SequenceCards(mission.View)).CustomScript);
        }

        [Fact]
        public void TheReplySchemaAsksWhetherTheScriptIsACustomScript()
        {
            var transport = Replies("mission-plan.sse");
            var mission = Control(transport);
            StartInFlight(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains("custom_script", transport.Requests[0].Body);
            Assert.Contains("custom_script_reason", transport.Requests[0].Body);
        }

        // ---- What is checked: what runs ----

        [Fact]
        public void TheCheckSeesTheScriptBundledWithTheCoreLibraryNotTheScriptAlone()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var card = Assert.Single(SequenceCards(mission.View));
            Assert.Equal(new[] { CoreLibrary.Bundle(card.Script, 1, 10800) }, _check.Checked);
        }

        // ---- The Spending Cap: the rewrite, Retry and Re-plan are Mission requests ----

        [Fact]
        public void TheAutomaticRewriteIsNotSentWhenItCouldPassTheSpendingCapAndRaisingTheCapSendsIt()
        {
            // The plan and the failing Sequence Plan cost $0.54 each: $1.08 of $1.20 leaves too little for the rewrite's worst case.
            var transport = Replies("mission-plan-priced.sse", "mission-sequence-plan-unsafe-priced.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission, 1.20m);
            DrainUntil(mission, v => v.Stage == MissionStage.Paused);

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(1.08m, mission.View.Spent);
            Assert.Equal(0m, mission.View.PendingCharge);
            Assert.Contains("Spending Cap", mission.View.PauseReason);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal(new[] { "pending", "paid", "pending", "paid", "blocked" }, CostEvents(mission));
            // The failing plan is still never shown.
            Assert.Empty(SequenceCards(mission.View));
            Assert.DoesNotContain(mission.View.Log, e => e.Script.Contains("QUICKSAVE"));

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains("line 1: :QUICKSAVE (quicksave)", LastTurnText(transport.Requests[2]));
            Assert.Equal(new[] { "pending", "paid", "pending", "paid", "blocked", "cap_raised", "pending", "paid" }, CostEvents(mission));
        }

        [Fact]
        public void RetryAndReplanAfterCouldntProduceASafeScriptAreUnderTheSpendingCapToo()
        {
            // Three failing replies at $0.54: $1.62 of $1.95 leaves too little for a Retry's worst case.
            var transport = Replies("mission-plan-priced.sse", "mission-sequence-plan-unsafe-priced.sse", "mission-sequence-plan-unsafe-priced.sse",
                "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission, 1.95m);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);
            Assert.Equal(3, transport.Requests.Count);

            mission.Send(new Retry());
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(new[] { QuickReply.RaiseCap, QuickReply.EndMission }, mission.View.QuickReplies);
            Assert.Equal("blocked", CostEvents(mission).Last());

            mission.Send(new RaiseCap(10m));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.Equal(4, transport.Requests.Count);
            Assert.Equal(new[] { "cap_raised", "pending", "paid" }, CostEvents(mission).Skip(7).ToArray());
        }

        [Fact]
        public void ReplanIsBlockedByTheSpendingCapWhenItCouldPassIt()
        {
            var transport = Replies("mission-plan-priced.sse", "mission-sequence-plan-unsafe-priced.sse", "mission-sequence-plan-unsafe-priced.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission, 1.95m);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);

            mission.Send(new Replan());
            Assert.Equal(MissionStage.Paused, mission.View.Stage);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal("blocked", CostEvents(mission).Last());
        }

        // ---- Across saves ----

        [Fact]
        public void ASequencePlanRestoredFromASaveIsScriptCheckedBeforeItCanBeApproved()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var saved = mission.ExportStates();
            var script = saved[0].SequencePlan.Script;
            _check.Checked.Clear();

            var resumed = Resumed(Replies(), saved);
            Assert.Equal(MissionStage.AwaitingSequenceApproval, resumed.View.Stage);
            Assert.Contains(CoreLibrary.Bundle(script, 1, 10800), _check.Checked);
            resumed.Send(new Approve());
            var start = Assert.Single(resumed.TakeCommands().OfType<StartScript>());
            Assert.Equal(CoreLibrary.Bundle(script, 1, 10800), start.Source);
            // The checked script keeps the run id (#89) and the chosen processor (#55).
            Assert.Equal(3102u, start.ProcessorPartId);
            Assert.True(start.RunId > 0);
            resumed.Send(new KosRunEnded(new RunOutcome(RunOutcomeKind.Finished, null, "", "", false), start.RunId + 1));
            Assert.Equal(MissionStage.Running, resumed.View.Stage);
        }

        [Fact]
        public void ARestoredSequencePlanThatFailsTheCheckIsCouldntProduceASafeScriptAndCanNeverBeApproved()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            // The save file isn't trusted: an edited save (or a stricter check since) brings back a script that fails.
            var saved = mission.ExportStates();
            saved[0].SequencePlan.Script = "kuniverse:quicksave().";

            var resumed = Resumed(Replies(), saved);
            Assert.Equal(MissionStage.UnsafeScript, resumed.View.Stage);
            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan }, resumed.View.QuickReplies);
            Assert.Contains(SequenceCards(resumed.View), c => c.Text.Contains("Couldn't produce a safe script") && c.Text.Contains("quicksave"));
            Assert.DoesNotContain(resumed.View.Log, e => e.Script.Contains("QUICKSAVE"));
            resumed.Send(new Approve());
            resumed.Send(new ConfirmApproval(true));
            Assert.Empty(resumed.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void APausedRestoreOfAFailingSequencePlanComesBackAsCouldntProduceASafeScriptOnRetry()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var saved = mission.ExportStates();
            saved[0].StageBeforePause = saved[0].Stage;
            saved[0].Stage = MissionStage.Paused;
            saved[0].SequencePlan.Script = "kuniverse:quicksave().";

            var resumed = Resumed(Replies(), saved);
            Assert.Equal(MissionStage.Paused, resumed.View.Stage);
            resumed.Send(new Retry());
            Assert.Equal(MissionStage.UnsafeScript, resumed.View.Stage);
            resumed.Send(new Approve());
            Assert.Empty(resumed.TakeCommands().OfType<StartScript>());
        }

        [Fact]
        public void TheApprovedScriptIsCheckedAgainAtApprovalWhateverBroughtItThere()
        {
            // A check that passes a script once and then fails it (the Core Library or the check changed under it).
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            _check.FailEverything = true;

            mission.Send(new Approve());
            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal(MissionStage.UnsafeScript, mission.View.Stage);
        }

        [Fact]
        public void CouldntProduceASafeScriptSurvivesASaveAndLoadAndRetryCarriesOn()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-unsafe.sse", "mission-sequence-plan-unsafe.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.UnsafeScript);
            var saved = mission.ExportStates();
            Assert.Equal(MissionStage.UnsafeScript, saved[0].Stage);

            var after = Replies("mission-sequence-plan-rewritten.sse");
            var resumed = Resumed(after, saved);
            Assert.Equal(MissionStage.UnsafeScript, resumed.View.Stage);
            Assert.Equal(new[] { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan }, resumed.View.QuickReplies);
            Assert.Contains(SequenceCards(resumed.View), c => c.Text.Contains("Couldn't produce a safe script") && c.Text.Contains("line 1: :QUICKSAVE (quicksave)"));
            Assert.Empty(after.Requests);

            resumed.Send(new Retry());
            DrainUntil(resumed, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            // What the Script Check found came back with the save, so Claudia hears it again.
            Assert.Contains("line 1: :QUICKSAVE (quicksave)", LastTurnText(Assert.Single(after.Requests)));
        }

        [Fact]
        public void ThePerSequenceFlagsAndTheCustomScriptBadgeRoundTripThroughTheSaveValues()
        {
            var transport = Replies("mission-plan.sse", "mission-sequence-plan-rewritten.sse");
            var mission = Control(transport);
            ApproveMissionPlan(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            var state = Assert.Single(mission.ExportStates());
            Assert.True(state.SequenceCounted);
            state.UnsafeProblems.Add("line 3: :QUICKSAVE (quicksave)");
            state.UnsafeProblems.Add("doesn't compile: a {brace} // here\nand a second line");

            var again = MissionSaveState.FromValues(state.ToValues());
            Assert.True(again.SequenceCounted);
            Assert.Equal(state.UnsafeProblems, again.UnsafeProblems);
            Assert.True(again.SequencePlan.CustomScript);
            Assert.Equal("No Building Block reports a status from the pad.", again.SequencePlan.CustomScriptReason);
        }

        [Fact]
        public void ARewriteResentAfterACrashGetsNoSecondAutomaticRewrite()
        {
            var held = ScriptedResponse.Fixture("mission-sequence-plan-unsafe.sse", 64);
            held.PauseAt = 100;
            _gates.Add(held);
            var before = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"), ScriptedResponse.Fixture("mission-sequence-plan-unsafe.sse"), held);
            var crashedQueue = new Background();
            var crashed = Control(before, crashedQueue);
            ApproveMissionPlan(crashed, 10m, crashedQueue);
            // The rewrite is out when the game crashes.
            DrainUntil(crashed, v => before.Requests.Count == 3 && v.Busy, crashedQueue);
            var saved = crashed.ExportStates();

            var after = Replies("mission-sequence-plan-unsafe.sse");
            var resumed = Resumed(after, saved);
            DrainUntil(resumed, v => v.Stage == MissionStage.UnsafeScript && !v.Busy);
            Assert.Single(after.Requests);
            Assert.Equal(before.Requests[2].Body, after.Requests[0].Body);
        }
    }
}
