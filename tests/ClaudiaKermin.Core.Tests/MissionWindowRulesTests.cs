// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>What the compact Mission window shows for a <see cref="MissionView"/>: the status chip, the prompt, the action row and the control bar.</summary>
    public sealed class MissionWindowRulesTests
    {
        private static MissionView SettingUp(CraftCheckState state = CraftCheckState.None, CraftVerdict verdict = CraftVerdict.None, params CraftFinding[] findings)
        {
            var view = new MissionView();
            view.StartForm.CanStart = true;
            view.CraftCheck.State = state;
            view.CraftCheck.Verdict = verdict;
            view.CraftCheck.Findings = new List<CraftFinding>(findings);
            return view;
        }

        private static CraftFinding Warning(bool acknowledged = false)
        {
            return new CraftFinding { Code = "w", Severity = CraftFindingSeverity.Warning, Acknowledged = acknowledged };
        }

        private static MissionView During(MissionStage stage)
        {
            return new MissionView { Stage = stage, MissionId = "m1", CanEndMission = true };
        }

        [Fact]
        public void NoMissionAndNoCraftCheckHasNoChip()
        {
            Assert.Null(MissionWindowRules.Status(SettingUp()));
            Assert.Null(MissionWindowRules.Status(new MissionView()));
        }

        [Fact]
        public void UnacknowledgedWarningsAreCountedInAmber()
        {
            var view = SettingUp(CraftCheckState.Done, CraftVerdict.PassWithWarnings, Warning(), Warning(true), Warning());
            var chip = MissionWindowRules.Status(view);
            Assert.Equal("Craft Check · 2 to acknowledge", chip.Text);
            Assert.Equal(ChipTone.Attention, chip.Tone);
        }

        [Fact]
        public void ACheckedCraftThatMayStartIsReady()
        {
            var view = SettingUp(CraftCheckState.Done, CraftVerdict.PassWithWarnings, Warning(true));
            view.CraftCheck.StartAllowed = true;
            var chip = MissionWindowRules.Status(view);
            Assert.Equal("Ready to start", chip.Text);
            Assert.Equal(ChipTone.Success, chip.Tone);
        }

        [Fact]
        public void BlockersAreDanger()
        {
            var view = SettingUp(CraftCheckState.Done, CraftVerdict.Fail, new CraftFinding { Severity = CraftFindingSeverity.Blocker });
            Assert.Equal(ChipTone.Danger, MissionWindowRules.Status(view).Tone);
        }

        [Fact]
        public void AStaleResultIsNotReady()
        {
            var view = SettingUp(CraftCheckState.Done, CraftVerdict.Pass);
            view.CraftCheck.StartAllowed = true;
            view.CraftCheck.Stale = true;
            Assert.Null(MissionWindowRules.Status(view));
        }

        [Theory]
        [InlineData(MissionStage.Planning, "Planning", ChipTone.Info)]
        [InlineData(MissionStage.Running, "Running", ChipTone.Info)]
        [InlineData(MissionStage.AwaitingPlanApproval, "Plan awaiting Approval", ChipTone.Info)]
        [InlineData(MissionStage.AwaitingSequenceApproval, "Plan awaiting Approval", ChipTone.Info)]
        [InlineData(MissionStage.Paused, "Paused", ChipTone.Attention)]
        [InlineData(MissionStage.ManualControl, "Manual Control · you fly", ChipTone.Attention)]
        [InlineData(MissionStage.Succeeded, "Mission Goal met", ChipTone.Success)]
        [InlineData(MissionStage.Lost, "Mission Lost", ChipTone.Danger)]
        [InlineData(MissionStage.Ended, "Mission ended", ChipTone.Danger)]
        public void TheStageNamesTheChip(MissionStage stage, string text, ChipTone tone)
        {
            var chip = MissionWindowRules.Status(During(stage));
            Assert.Equal(text, chip.Text);
            Assert.Equal(tone, chip.Tone);
        }

        [Fact]
        public void AConfirmPromptNeedsThePlayer()
        {
            var view = During(MissionStage.AwaitingPlanApproval);
            view.ConfirmPrompt = "Approve the plan?";
            Assert.Equal(ChipTone.Attention, MissionWindowRules.Status(view).Tone);
        }

        [Fact]
        public void AFinishedMissionKeepsItsChipUntilTheNextCheck()
        {
            var view = During(MissionStage.Succeeded);
            view.CanEndMission = false;
            view.StartForm.CanStart = true;
            Assert.Equal("Mission Goal met", MissionWindowRules.Status(view).Text);
            view.CraftCheck.State = CraftCheckState.Running;
            Assert.Equal("Checking the craft", MissionWindowRules.Status(view).Text);
        }

        // ---- Prompt ----

        private static MissionView Typing(MissionStage stage)
        {
            var view = During(stage);
            view.CanType = true;
            return view;
        }

        [Fact]
        public void BeforeAMissionThePromptWritesTheMissionGoal()
        {
            var prompt = MissionWindowRules.Prompt(SettingUp(), PromptFor.Reply, false);
            Assert.Equal(PromptFor.Goal, prompt.For);
            Assert.Equal("Mission Goal", prompt.Caption);
            Assert.True(prompt.Enabled);
            Assert.Equal("Enter sends · Shift+Enter adds a line", prompt.Hint);
        }

        [Fact]
        public void APinnedGoalLeavesThePromptWaitingForTheMission()
        {
            var prompt = MissionWindowRules.Prompt(SettingUp(), PromptFor.Goal, true);
            Assert.False(prompt.Enabled);
            Assert.Equal("You can ask Claudia once the Mission starts", prompt.Placeholder);
        }

        [Fact]
        public void APlanAwaitingApprovalTakesAReply()
        {
            var prompt = MissionWindowRules.Prompt(Typing(MissionStage.AwaitingPlanApproval), PromptFor.Reply, false);
            Assert.Equal(PromptFor.Reply, prompt.For);
            Assert.Equal("Reply to Claudia", prompt.Caption);
        }

        [Fact]
        public void ChangeAndAskPickTheCaption()
        {
            var view = Typing(MissionStage.AwaitingSequenceApproval);
            Assert.Equal("What should change?", MissionWindowRules.Prompt(view, PromptFor.Change, false).Caption);
            Assert.Equal("Ask Claudia", MissionWindowRules.Prompt(view, PromptFor.Question, false).Caption);
        }

        [Fact]
        public void WithNothingPendingTheReplyIsAQuestion()
        {
            var prompt = MissionWindowRules.Prompt(Typing(MissionStage.Running), PromptFor.Change, false);
            Assert.Equal(PromptFor.Question, prompt.For);
            Assert.Equal("Ask Claudia", prompt.Caption);
        }

        [Fact]
        public void TheRetryNoteOnlyAppliesToAnUnsafeScript()
        {
            Assert.Equal("Note for Claudia's retry:", MissionWindowRules.Prompt(Typing(MissionStage.UnsafeScript), PromptFor.Note, false).Caption);
            Assert.Equal(PromptFor.Question, MissionWindowRules.Prompt(Typing(MissionStage.Running), PromptFor.Note, false).For);
        }

        [Fact]
        public void ManualControlAsksOrSaysWhatYouDid()
        {
            var prompt = MissionWindowRules.Prompt(Typing(MissionStage.ManualControl), PromptFor.Reply, false);
            Assert.Equal("Ask Claudia, or say what you did", prompt.Caption);
            Assert.Equal("Hand back sends what you typed with it", prompt.Hint);
        }

        [Fact]
        public void APromptThatCantTypeExplainsWhy()
        {
            var view = During(MissionStage.Planning);
            view.Busy = true;
            var prompt = MissionWindowRules.Prompt(view, PromptFor.Reply, false);
            Assert.False(prompt.Enabled);
            Assert.NotEqual("", prompt.Placeholder);
        }

        [Fact]
        public void SendingTurnsTheTextIntoTheInputForItsMode()
        {
            Assert.IsType<Reply>(MissionWindowRules.Send(PromptFor.Reply, "go"));
            Assert.IsType<ChangeSomething>(MissionWindowRules.Send(PromptFor.Change, "go"));
            Assert.IsType<AskQuestion>(MissionWindowRules.Send(PromptFor.Question, "go"));
            Assert.Equal("slower", ((RetryWithNote)MissionWindowRules.Send(PromptFor.Note, "slower")).Note);
            Assert.Null(MissionWindowRules.Send(PromptFor.Goal, "Land on the Mun"));
        }

        // ---- Action row and control bar ----

        [Theory]
        [InlineData(QuickReply.Approve, "Approve", true)]
        [InlineData(QuickReply.ChangeSomething, "Change", false)]
        [InlineData(QuickReply.AskQuestion, "Ask", false)]
        [InlineData(QuickReply.ManualControl, "Take controls", false)]
        [InlineData(QuickReply.HandBack, "Hand back to Claudia", true)]
        [InlineData(QuickReply.RetryWithNote, "Retry with a note", false)]
        [InlineData(QuickReply.WarpToDueTime, "Warp to Due Time", false)]
        public void QuickRepliesHaveShortLabelsAndOnePrimary(QuickReply reply, string label, bool primary)
        {
            Assert.Equal(label, MissionWindowRules.Label(reply, new MissionView()));
            Assert.Equal(primary, MissionWindowRules.IsPrimary(reply));
        }

        [Fact]
        public void GoToNamesTheVessel()
        {
            var view = new MissionView();
            view.DueTime.VesselName = "Mun Lander";
            Assert.Equal("Go to Mun Lander", MissionWindowRules.Label(QuickReply.GoToVessel, view));
        }

        [Fact]
        public void StartMissionSaysWhatHoldsItBack()
        {
            var warnings = SettingUp(CraftCheckState.Done, CraftVerdict.PassWithWarnings, Warning(), Warning());
            Assert.Equal("Start Mission · 2 to acknowledge", MissionWindowRules.StartLabel(warnings));
            var blocked = SettingUp(CraftCheckState.Done, CraftVerdict.Fail, new CraftFinding { Severity = CraftFindingSeverity.Blocker });
            Assert.Equal("Start Mission · fix the Blockers", MissionWindowRules.StartLabel(blocked));
            var allowed = SettingUp(CraftCheckState.Done, CraftVerdict.Pass);
            allowed.CraftCheck.StartAllowed = true;
            Assert.Equal("Start Mission", MissionWindowRules.StartLabel(allowed));
        }

        [Fact]
        public void TheSetupSummaryNamesTypeModelAndEstimate()
        {
            var view = SettingUp();
            view.StartForm.Estimates["sonnet-5-5"] = 2.4m;
            Assert.Equal("Crewed · sonnet-5-5 · est. ~$2.40", MissionWindowRules.SetupSummary(view, false, "sonnet-5-5"));
            Assert.Equal("Remote · opus-5-5", MissionWindowRules.SetupSummary(view, true, "opus-5-5"));
        }

        [Fact]
        public void TheSpendMeterCountsHeldChargesAgainstTheCap()
        {
            var view = During(MissionStage.Running);
            view.Spent = 1.2m;
            view.HeldCharges = 0.22m;
            view.SpendingCap = 10m;
            Assert.Equal("$1.42 / $10.00", MissionWindowRules.SpendMeter(view));
            Assert.Equal(0.142f, MissionWindowRules.SpendFraction(view), 3);
            view.Spent = 20m;
            Assert.Equal(1f, MissionWindowRules.SpendFraction(view));
        }

        [Fact]
        public void TheStepCountsTheSequenceInHand()
        {
            var view = During(MissionStage.Running);
            Assert.Equal("", MissionWindowRules.Step(view));
            view.PlanSequences = new List<string> { "Launch", "Transfer", "Land" };
            view.CurrentSequence = 1;
            Assert.Equal("step 2 of 3", MissionWindowRules.Step(view));
        }
    }
}
