// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>When Claudia needs the player (Debrief, Approval, Pause), seen only through the <see cref="MissionView"/>.</summary>
    public sealed class PlayerAttentionTests
    {
        private static MissionView View(MissionStage stage, params LogEntry[] log)
        {
            return new MissionView { MissionId = "m1", Stage = stage, Log = new List<LogEntry>(log) };
        }

        private static LogEntry Card(LogKind kind, string title = "Plan")
        {
            return new LogEntry { Kind = kind, Title = title };
        }

        private static MissionView Awaiting(params LogEntry[] log)
        {
            var view = View(MissionStage.AwaitingSequenceApproval, log);
            view.QuickReplies = new List<QuickReply> { QuickReply.Approve, QuickReply.ChangeSomething };
            return view;
        }

        [Fact]
        public void NoMissionNeedsNobody()
        {
            Assert.Null(new AttentionWatch().Observe(new MissionView()));
            Assert.Null(new AttentionWatch().Observe(null));
        }

        [Fact]
        public void ANewPlanForApprovalNeedsThePlayerOnce()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Planning));
            var plan = Card(LogKind.MissionPlan);
            var alert = watch.Observe(Awaiting(plan));
            Assert.Equal(AttentionKind.Approval, alert.Kind);
            Assert.Null(watch.Observe(Awaiting(plan)));
        }

        [Fact]
        public void ARevisedPlanNeedsThePlayerAgain()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Planning));
            watch.Observe(Awaiting(Card(LogKind.SequencePlan)));
            watch.Observe(View(MissionStage.Sequencing, Card(LogKind.SequencePlan)));
            var alert = watch.Observe(Awaiting(Card(LogKind.SequencePlan), Card(LogKind.SequencePlan)));
            Assert.Equal(AttentionKind.Approval, alert.Kind);
        }

        [Fact]
        public void ADeclinedGoalHasNothingToApprove()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Planning));
            var view = View(MissionStage.AwaitingPlanApproval);
            view.QuickReplies = new List<QuickReply> { QuickReply.ChangeSomething, QuickReply.EndMission };
            Assert.Null(watch.Observe(view));
        }

        [Fact]
        public void ADebriefNeedsThePlayerEvenThoughTheMissionMovesStraightOn()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Running));
            var alert = watch.Observe(View(MissionStage.Sequencing, Card(LogKind.Debrief, "Debrief: Nominal")));
            Assert.Equal(AttentionKind.Debrief, alert.Kind);
            Assert.Contains("Debrief: Nominal", alert.Text);
        }

        [Fact]
        public void AFinalDebriefOnASucceededMissionNeedsThePlayer()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Debriefing));
            Assert.Equal(AttentionKind.Debrief, watch.Observe(View(MissionStage.Succeeded, Card(LogKind.Debrief))).Kind);
        }

        [Fact]
        public void ADebriefIsNotReportedTwice()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Running));
            var debrief = Card(LogKind.Debrief);
            Assert.NotNull(watch.Observe(View(MissionStage.Sequencing, debrief)));
            Assert.Null(watch.Observe(View(MissionStage.Sequencing, debrief)));
        }

        [Fact]
        public void TheDebriefWinsWhenItArrivesWithThePlanItLeadsTo()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Running));
            var alert = watch.Observe(Awaiting(Card(LogKind.Debrief), Card(LogKind.SequencePlan)));
            Assert.Equal(AttentionKind.Debrief, alert.Kind);
        }

        [Fact]
        public void APauseNeedsThePlayerOnce()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Running));
            var paused = View(MissionStage.Paused);
            paused.PauseReason = "No network";
            Assert.Equal(AttentionKind.Pause, watch.Observe(paused).Kind);
            Assert.Null(watch.Observe(paused));
        }

        [Fact]
        public void AFailedRetryPausesAgainAndAlertsAgain()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Running));
            var first = View(MissionStage.Paused, new LogEntry { Kind = LogKind.Notice, Text = "No network" });
            first.PauseReason = "No network";
            watch.Observe(first);
            watch.Observe(View(MissionStage.Sequencing));
            var second = View(MissionStage.Paused, new LogEntry { Kind = LogKind.Notice, Text = "No network" }, new LogEntry { Kind = LogKind.Notice, Text = "No network" });
            second.PauseReason = "No network";
            Assert.Equal(AttentionKind.Pause, watch.Observe(second).Kind);
        }

        [Fact]
        public void ACouldntProduceASafeScriptIsAPause()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Sequencing));
            Assert.Equal(AttentionKind.Pause, watch.Observe(View(MissionStage.UnsafeScript, Card(LogKind.SequencePlan))).Kind);
        }

        [Fact]
        public void WhileClaudiaIsWorkingNobodyIsNeeded()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Planning));
            var busy = Awaiting(Card(LogKind.MissionPlan));
            busy.Busy = true;
            Assert.Null(watch.Observe(busy));
        }

        [Fact]
        public void AMissionAlreadyWaitingWhenFirstSeenIsOnlyTheBaseline()
        {
            var watch = new AttentionWatch();
            Assert.Null(watch.Observe(Awaiting(Card(LogKind.SequencePlan), Card(LogKind.Debrief))));
            Assert.Null(watch.Observe(Awaiting(Card(LogKind.SequencePlan), Card(LogKind.Debrief))));
        }

        [Fact]
        public void ANewMissionStartsFromANewBaseline()
        {
            var watch = new AttentionWatch();
            watch.Observe(Awaiting(Card(LogKind.SequencePlan)));
            var next = Awaiting(Card(LogKind.MissionPlan));
            next.MissionId = "m2";
            Assert.Null(watch.Observe(next));
        }

        [Fact]
        public void RunningAndPlanningNeedNobody()
        {
            var watch = new AttentionWatch();
            watch.Observe(View(MissionStage.Planning));
            Assert.Null(watch.Observe(View(MissionStage.Running)));
            Assert.Null(watch.Observe(View(MissionStage.Debriefing)));
        }
    }
}
