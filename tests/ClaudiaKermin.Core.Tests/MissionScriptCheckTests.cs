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

        public void Dispose()
        {
            _save.Dispose();
        }

        /// <summary>Fails any script that quicksaves, the way the real check would.</summary>
        private sealed class FakeScriptCheck : IScriptCheck
        {
            public readonly List<string> Checked = new List<string>();

            public ScriptCheckResult Check(string script)
            {
                Checked.Add(script);
                return script.Contains("QUICKSAVE")
                    ? new ScriptCheckResult(null, new List<string> { "line 1: :QUICKSAVE (quicksave)" })
                    : ScriptCheckResult.Pass();
            }
        }

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, _check);
            mission.Send(new SaveLoaded(_save.Path));
            return mission;
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

        /// <summary>Starts a Mission in Flight and approves its Mission Plan, so the next reply is a Sequence Plan.</summary>
        private void ApproveMissionPlan(MissionControl mission)
        {
            StartInFlight(mission);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
        }

        /// <summary>Start Mission in Flight, through the Craft Check (which passes).</summary>
        private void StartInFlight(MissionControl mission)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
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
    }
}
