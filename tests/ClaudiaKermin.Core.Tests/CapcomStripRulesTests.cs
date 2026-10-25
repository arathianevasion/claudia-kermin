// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>What the collapsed Capcom strip shows for a <see cref="MissionView"/>: the status chip, the segment track, the expand tab and the lock line.</summary>
    public sealed class CapcomStripRulesTests
    {
        /// <summary>A Mission at <paramref name="stage"/>; a sequence name ending in "!" is a manual stretch.</summary>
        private static MissionView During(MissionStage stage, int current = -1, params string[] sequences)
        {
            var view = new MissionView { Stage = stage, MissionId = "m1", CanEndMission = true, CurrentSequence = current };
            view.PlanSequencesManual = sequences.Select(s => s.EndsWith("!")).ToList();
            view.PlanSequences = sequences.Select(s => s.TrimEnd('!')).ToList();
            return view;
        }

        // ---- Status chip ----

        [Fact]
        public void RunningIsFlyingTheCurrentSequenceInBlue()
        {
            var chip = MissionWindowRules.StripStatus(During(MissionStage.Running, 1, "Launch", "Circularize"));
            Assert.Equal("Flying · Circularize", chip.Text);
            Assert.Equal(ChipTone.Info, chip.Tone);
        }

        [Theory]
        [InlineData(MissionStage.AwaitingPlanApproval)]
        [InlineData(MissionStage.AwaitingSequenceApproval)]
        [InlineData(MissionStage.Paused)]
        [InlineData(MissionStage.UnsafeScript)]
        public void WaitingOnThePlayerIsAmber(MissionStage stage)
        {
            var chip = MissionWindowRules.StripStatus(During(stage));
            Assert.Equal("Waiting for you", chip.Text);
            Assert.Equal(ChipTone.Attention, chip.Tone);
        }

        [Fact]
        public void InABlackoutTheChipSaysBlackoutAndOnlyAPauseWaitsForYou()
        {
            var running = During(MissionStage.Running, 1, "Launch", "Circularize");
            running.Blackout = true;
            Assert.Equal("Blackout · flying Circularize", MissionWindowRules.StripStatus(running).Text);

            var waiting = During(MissionStage.AwaitingSequenceApproval);
            waiting.Blackout = true;
            waiting.QueuedMessages = 2;
            waiting.HeldMessages = 1;
            waiting.LatestLine = "No link to Kerbin. 2 of Claudia's messages wait for the link.";
            Assert.Equal("Blackout", MissionWindowRules.StripStatus(waiting).Text);
            Assert.Equal("", MissionWindowRules.NeedsYou(waiting));
            Assert.Equal(waiting.LatestLine, MissionWindowRules.StripLine(waiting));
            Assert.Equal("No link to Kerbin · Claudia flies the approved Mission Plan · 2 of her messages wait for the link · 1 of yours held",
                MissionWindowRules.BlackoutLine(waiting));

            var paused = During(MissionStage.Paused);
            paused.Blackout = true;
            Assert.Equal("Waiting for you", MissionWindowRules.StripStatus(paused).Text);
            Assert.Equal("", MissionWindowRules.BlackoutLine(During(MissionStage.Running)));
        }

        [Fact]
        public void ATypedApprovalToConfirmIsWaitingForYou()
        {
            var view = During(MissionStage.Sequencing);
            view.ConfirmPrompt = "Approve?";
            Assert.Equal("Waiting for you", MissionWindowRules.StripStatus(view).Text);
        }

        [Fact]
        public void ManualControlIsAmber()
        {
            var chip = MissionWindowRules.StripStatus(During(MissionStage.ManualControl));
            Assert.Equal("Manual Control · you fly", chip.Text);
            Assert.Equal(ChipTone.Attention, chip.Tone);
        }

        [Theory]
        [InlineData(MissionStage.Planning, "Planning")]
        [InlineData(MissionStage.Debriefing, "Debriefing")]
        [InlineData(MissionStage.Succeeded, "Mission Goal met")]
        [InlineData(MissionStage.Lost, "Mission Lost")]
        public void TheRestAreBlue(MissionStage stage, string text)
        {
            var chip = MissionWindowRules.StripStatus(During(stage));
            Assert.Equal(text, chip.Text);
            Assert.Equal(ChipTone.Info, chip.Tone);
        }

        [Fact]
        public void TheLineIsClaudiasLatestOrWhatSheIsDoing()
        {
            var view = During(MissionStage.Running);
            Assert.Equal("Standing by.", MissionWindowRules.StripLine(view));
            view.LatestLine = "Gravity turn started.";
            Assert.Equal("Gravity turn started.", MissionWindowRules.StripLine(view));
            view.Busy = true;
            view.Activity = "writing";
            Assert.Equal("Claudia is writing…", MissionWindowRules.StripLine(view));
        }

        // ---- Segment track ----

        [Fact]
        public void NoMissionPlanHasNoSegments()
        {
            Assert.Empty(MissionWindowRules.Segments(During(MissionStage.Planning)));
        }

        [Fact]
        public void SegmentsAreNumberedAndDoneCurrentOrUpcoming()
        {
            var segments = MissionWindowRules.Segments(During(MissionStage.Running, 1, "Launch", "Circularize", "Return"));
            Assert.Equal(new[] { "1 · Launch", "2 · Circularize", "3 · Return" }, segments.Select(s => s.Label));
            Assert.Equal(new[] { SegmentState.Done, SegmentState.Current, SegmentState.Upcoming }, segments.Select(s => s.State));
            Assert.Equal(new[] { SegmentBar.Accent, SegmentBar.Claudia, SegmentBar.Grey }, segments.Select(s => s.Bar));
        }

        [Theory]
        [InlineData(MissionStage.Running, SegmentBar.Claudia)]
        [InlineData(MissionStage.AwaitingSequenceApproval, SegmentBar.AttentionOutline)]
        [InlineData(MissionStage.ManualControl, SegmentBar.AttentionFill)]
        [InlineData(MissionStage.Paused, SegmentBar.AttentionOutline)]
        [InlineData(MissionStage.Debriefing, SegmentBar.ClaudiaOutline)]
        public void TheCurrentBarFollowsTheStage(MissionStage stage, SegmentBar bar)
        {
            Assert.Equal(bar, MissionWindowRules.Segments(During(stage, 0, "Launch", "Return"))[0].Bar);
        }

        [Fact]
        public void AManualStretchNotStartedIsDashedAndMarkedYou()
        {
            var segments = MissionWindowRules.Segments(During(MissionStage.Running, 0, "Launch", "Docking!"));
            Assert.Equal(SegmentBar.ManualDashed, segments[1].Bar);
            Assert.True(segments[1].You);
            Assert.False(segments[0].You);
        }

        [Fact]
        public void AManualStretchInManualControlIsAmberFilledAndStillYoursButNotOnceDone()
        {
            var flying = MissionWindowRules.Segments(During(MissionStage.ManualControl, 1, "Launch", "Docking!", "Return"));
            Assert.Equal(SegmentBar.AttentionFill, flying[1].Bar);
            Assert.True(flying[1].You);

            var done = MissionWindowRules.Segments(During(MissionStage.Running, 2, "Launch", "Docking!", "Return"));
            Assert.Equal(SegmentBar.Accent, done[1].Bar);
            Assert.False(done[1].You);
        }

        [Fact]
        public void BeforeTheFirstSequenceEverySegmentIsUpcoming()
        {
            var segments = MissionWindowRules.Segments(During(MissionStage.AwaitingPlanApproval, -1, "Launch", "Return"));
            Assert.All(segments, s => Assert.Equal(SegmentState.Upcoming, s.State));
        }

        [Fact]
        public void AMetGoalMarksTheWholePlanDone()
        {
            var segments = MissionWindowRules.Segments(During(MissionStage.Succeeded, 1, "Launch", "Return"));
            Assert.All(segments, s => Assert.Equal(SegmentState.Done, s.State));
        }

        [Fact]
        public void AShortManualListCountsAsAutomatic()
        {
            var view = During(MissionStage.Running, 0, "Launch", "Return");
            view.PlanSequencesManual = new List<bool>();
            Assert.Equal(SegmentBar.Grey, MissionWindowRules.Segments(view)[1].Bar);
        }

        // ---- Expand tab ----

        [Fact]
        public void TheTabSaysChatWhenNothingIsNeededAndCollapseWhenExpanded()
        {
            var view = During(MissionStage.Running);
            Assert.Equal("", MissionWindowRules.NeedsYou(view));
            Assert.Equal("Chat", MissionWindowRules.ExpandTab(view, false));
            Assert.Equal("Collapse", MissionWindowRules.ExpandTab(view, true));
        }

        [Fact]
        public void APlanAwaitingApprovalSaysWhichPlan()
        {
            var plan = During(MissionStage.AwaitingPlanApproval);
            plan.QuickReplies = new List<QuickReply> { QuickReply.Approve, QuickReply.ChangeSomething };
            Assert.Equal("Approve the Mission Plan?", MissionWindowRules.NeedsYou(plan));
            Assert.Equal("Approve the Mission Plan?", MissionWindowRules.ExpandTab(plan, false));

            var sequence = During(MissionStage.AwaitingSequenceApproval);
            sequence.QuickReplies = new List<QuickReply> { QuickReply.Approve };
            Assert.Equal("Sequence Plan awaiting Approval", MissionWindowRules.NeedsYou(sequence));
        }

        [Fact]
        public void ASequencePlanWithADueTimeSaysWhen()
        {
            var view = During(MissionStage.AwaitingSequenceApproval);
            view.QuickReplies = new List<QuickReply> { QuickReply.Approve, QuickReply.WarpToDueTime };
            view.DueTime = new DueTimeView { HasDueTime = true, SecondsLeft = 3600 };
            Assert.StartsWith("Sequence Plan awaiting Approval · Due Time in ", MissionWindowRules.NeedsYou(view));
            view.DueTime.Reached = true;
            Assert.Equal("Sequence Plan awaiting Approval · Due Time reached", MissionWindowRules.NeedsYou(view));
        }

        [Fact]
        public void AnAnswerInProgressOrADeclinedGoalIsNotAnApprovalToAskFor()
        {
            var busy = During(MissionStage.AwaitingPlanApproval);
            busy.Busy = true;
            Assert.Equal("", MissionWindowRules.NeedsYou(busy));
            Assert.Equal("", MissionWindowRules.NeedsYou(During(MissionStage.AwaitingPlanApproval)));
        }

        [Fact]
        public void TheOtherCallsToActionSayWhatIsNeeded()
        {
            var confirm = During(MissionStage.AwaitingSequenceApproval);
            confirm.ConfirmPrompt = "Approve?";
            Assert.Equal("Confirm your Approval", MissionWindowRules.NeedsYou(confirm));

            var stale = During(MissionStage.AwaitingSequenceApproval);
            stale.QuickReplies = new List<QuickReply> { QuickReply.Replan };
            stale.PlanStale = "a stage was removed";
            Assert.Equal("The craft has changed: Re-plan?", MissionWindowRules.NeedsYou(stale));

            var cap = During(MissionStage.Paused);
            cap.QuickReplies = new List<QuickReply> { QuickReply.RaiseCap, QuickReply.EndMission };
            Assert.Equal("Paused at the Spending Cap", MissionWindowRules.NeedsYou(cap));

            var paused = During(MissionStage.Paused);
            paused.PauseReason = "the API is overloaded";
            Assert.Equal("Paused: the API is overloaded", MissionWindowRules.NeedsYou(paused));

            Assert.Equal("Couldn't produce a safe script", MissionWindowRules.NeedsYou(During(MissionStage.UnsafeScript)));
        }

        // ---- Lock line ----

        [Fact]
        public void NothingLockedHasNoLockLine()
        {
            Assert.Equal("", MissionWindowRules.LockLine(During(MissionStage.ManualControl)));
        }

        [Fact]
        public void WhileRunningTheCraftsControlsAreLockedWhileClaudiaFlies()
        {
            var view = During(MissionStage.Running);
            view.Locks = ControlLocks.Craft | ControlLocks.Warp;
            Assert.Equal("Staging, action groups, SAS, RCS, gear, brakes and throttle locked while Claudia flies", MissionWindowRules.LockLine(view));
        }

        [Fact]
        public void AwaitingApprovalTheControlsStayLocked()
        {
            var view = During(MissionStage.AwaitingSequenceApproval);
            view.Locks = ControlLocks.Craft;
            Assert.Equal("Controls stay locked until you approve or take the controls", MissionWindowRules.LockLine(view));
        }

        [Theory]
        [InlineData(MissionStage.Planning)]
        [InlineData(MissionStage.Debriefing)]
        [InlineData(MissionStage.Paused)]
        public void WithNothingToApproveTheLockLineSaysClaudiaHasTheCraft(MissionStage stage)
        {
            var view = During(stage);
            view.Locks = ControlLocks.Craft;
            Assert.Equal("Staging, action groups, SAS, RCS, gear, brakes and throttle locked while Claudia has the craft", MissionWindowRules.LockLine(view));
        }

        [Fact]
        public void AConfirmPromptIsWaitingForYouWhateverTheStage()
        {
            var view = During(MissionStage.Running, 0, "Launch");
            view.ConfirmPrompt = "Approve?";
            Assert.Equal("Waiting for you", MissionWindowRules.StripStatus(view).Text);
        }

        [Fact]
        public void ManualControlUnavailableSaysWhy()
        {
            var view = During(MissionStage.Running);
            view.Locks = ControlLocks.Craft | ControlLocks.Warp | ControlLocks.Attitude;
            view.ManualControlUnavailable = "no link, no other crew aboard";
            Assert.Equal("Manual Control unavailable: no link, no other crew aboard", MissionWindowRules.LockLine(view));
        }

        [Fact]
        public void LinkLossInManualControlCountsDown()
        {
            var view = During(MissionStage.ManualControl);
            view.LinkLossTakeBackIn = 7.2;
            Assert.Equal("No link to Kerbin: Claudia takes the craft back in 8 s unless it returns", MissionWindowRules.LockLine(view));
        }

        [Fact]
        public void AStalePlanAsksForARePlanFirst()
        {
            var view = During(MissionStage.AwaitingSequenceApproval);
            view.Locks = ControlLocks.Craft;
            view.PlanStale = "a stage was removed";
            Assert.Equal("The craft has changed (a stage was removed): Re-plan", MissionWindowRules.LockLine(view));
        }

        // ---- Live progress ----

        [Fact]
        public void WhileRunningTheProgressLineShowsTheBlockTimeAndKeyNumbers()
        {
            var view = During(MissionStage.Running, 0, "Launch");
            view.RunProgress = new RunProgressView
            {
                Running = true, Sequence = "Launch", Block = "launch", ElapsedSeconds = 63.4, PlannedSeconds = 240, Fraction = 63.4 / 240,
                DeltaVUsed = 1210, PlannedDeltaV = 3400, Apoapsis = 41250, Periapsis = -540000, Altitude = 12000, ElectricChargePercent = 96,
            };
            Assert.Equal("launch · 63 s of 240 s · Δv 1,210 of 3,400 m/s · Alt 12.0 km · Ap 41.3 km · Pe -540.0 km · EC 96%",
                MissionWindowRules.ProgressLine(view));

            // Between blocks, with no simulator and no orbit: only what is known.
            view.RunProgress = new RunProgressView { Running = true, Sequence = "Launch", ElapsedSeconds = 5, ChecksCrossed = 1 };
            Assert.Equal("5 s · 1 Telemetry Check crossed", MissionWindowRules.ProgressLine(view));

            Assert.Equal("", MissionWindowRules.ProgressLine(During(MissionStage.AwaitingSequenceApproval, 0, "Launch")));
        }

        [Fact]
        public void AnAbortRecommendationOrAWaitingCallNeedsThePlayerEvenWhileRunning()
        {
            var view = During(MissionStage.Running, 0, "Launch");
            Assert.Equal("", MissionWindowRules.NeedsYou(view));

            view.CapWait = "Claudia's next request waits.";
            Assert.Equal("Raise cap: Claudia's request is waiting", MissionWindowRules.NeedsYou(view));

            // An Abort recommendation comes first, even while she answers something else.
            view.AbortRecommended = "Claudia recommends Abort: delta-v used 100 m/s over 50 m/s.";
            view.Busy = true;
            Assert.Equal("Claudia recommends Abort", MissionWindowRules.NeedsYou(view));
            Assert.Equal("Abort", MissionWindowRules.Label(QuickReply.Abort, view));
        }
    }
}
