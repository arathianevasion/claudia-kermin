// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
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

        // ---- Craft Check card and approval checkbox ----

        private static CraftCheckView Checked(CraftVerdict verdict, params CraftFinding[] findings)
        {
            return new CraftCheckView { State = CraftCheckState.Done, Verdict = verdict, Findings = new List<CraftFinding>(findings), CraftName = "Duna Probe", MarginPercent = 15 };
        }

        [Fact]
        public void TheNoticeNamesTheCraftWhereItIs()
        {
            var check = Checked(CraftVerdict.Pass);
            Assert.Equal("Duna Probe on the pad · Mission Goal set", MissionWindowRules.CraftNotice(check, true, true));
            Assert.Equal("Duna Probe on the floor · Mission Goal set", MissionWindowRules.CraftNotice(check, false, true));
            Assert.Equal("Duna Probe on the floor", MissionWindowRules.CraftNotice(check, false, false));
            Assert.Equal("", MissionWindowRules.CraftNotice(new CraftCheckView(), true, true));
        }

        [Theory]
        [InlineData(CraftVerdict.Pass, "Craft Check: Pass")]
        [InlineData(CraftVerdict.PassWithWarnings, "Craft Check: Pass with Warnings")]
        [InlineData(CraftVerdict.Fail, "Craft Check: Fail")]
        public void TheCardHeaderNamesTheVerdict(CraftVerdict verdict, string text)
        {
            Assert.Equal(text, MissionWindowRules.CardTitle(Checked(verdict)));
        }

        [Fact]
        public void TheCollapsedHeaderCountsTheFindings()
        {
            var two = Checked(CraftVerdict.PassWithWarnings, Warning(), Warning());
            Assert.Equal("Craft Check: Pass with Warnings (2)", MissionWindowRules.CollapsedTitle(two));
            two.Stale = true;
            Assert.Equal("Craft Check: Pass with Warnings (2) - the craft changed since", MissionWindowRules.CollapsedTitle(two));
            Assert.Equal("Craft Check: Pass", MissionWindowRules.CollapsedTitle(Checked(CraftVerdict.Pass)));
            Assert.Equal("Craft Check: not checked yet", MissionWindowRules.CollapsedTitle(new CraftCheckView()));
            Assert.Equal("Craft Check", MissionWindowRules.CollapsedTitle(new CraftCheckView { State = CraftCheckState.Running }));
        }

        [Fact]
        public void TheVerdictIconIsAmberRedOrGreen()
        {
            Assert.Equal(ChipTone.Attention, MissionWindowRules.VerdictTone(CraftVerdict.PassWithWarnings));
            Assert.Equal(ChipTone.Danger, MissionWindowRules.VerdictTone(CraftVerdict.Fail));
            Assert.Equal(ChipTone.Success, MissionWindowRules.VerdictTone(CraftVerdict.Pass));
        }

        [Fact]
        public void TheFactsRowShowsDeltaVMarginVerdictAndCost()
        {
            var check = Checked(CraftVerdict.Pass);
            check.CraftDeltaV = 4058.4;
            var facts = MissionWindowRules.Facts(check, 2.4m);
            Assert.Equal(new[] { "Duna Probe", "Δv 4,058 m/s", "Margin wanted 15%", "Enough for this goal", "est. ~$2.40 for the Mission" }, facts.Select(f => f.Text).ToArray());
            Assert.Equal(ChipTone.Success, facts[3].Tone);
            Assert.Equal(ChipTone.Info, facts[1].Tone);
        }

        [Fact]
        public void TheFactsRowSaysWhenTheCraftIsShortOrUnjudged()
        {
            var check = Checked(CraftVerdict.Fail);
            check.CraftDeltaV = 900;
            check.DeltaVShort = true;
            var facts = MissionWindowRules.Facts(check, null);
            Assert.Equal("Short of the delta-v this goal needs", facts[3].Text);
            Assert.Equal(ChipTone.Danger, facts[3].Tone);
            Assert.Equal(4, facts.Count);

            check.Stale = true;
            Assert.Equal("Check the craft to judge", MissionWindowRules.Facts(check, null)[3].Text);
        }

        [Fact]
        public void AnUnknownDeltaVAsksForTheSimulator()
        {
            var facts = MissionWindowRules.Facts(Checked(CraftVerdict.Pass), null);
            Assert.Equal("Δv unknown: turn on the delta-v simulator", facts[1].Text);
            Assert.Equal(ChipTone.Attention, facts[1].Tone);
            Assert.DoesNotContain(facts, f => f.Text == "Enough for this goal");
        }

        [Fact]
        public void TheFooterFollowsTheWarningsAndBlockers()
        {
            var open = Checked(CraftVerdict.PassWithWarnings, Warning(), Warning(true));
            var footer = MissionWindowRules.Footer(open);
            Assert.Equal("Start Mission opens once every Warning is acknowledged.", footer.Text);
            Assert.Null(footer.Tone);

            var done = Checked(CraftVerdict.PassWithWarnings, Warning(true));
            done.StartAllowed = true;
            footer = MissionWindowRules.Footer(done);
            Assert.Equal("All Warnings acknowledged. Start Mission is open.", footer.Text);
            Assert.Equal(ChipTone.Success, footer.Tone);

            var fail = Checked(CraftVerdict.Fail, new CraftFinding { Severity = CraftFindingSeverity.Blocker });
            Assert.Equal("Start Mission is closed: fix every Blocker in the craft and check again.", MissionWindowRules.Footer(fail).Text);
        }

        [Fact]
        public void AStaleCheckSaysStartMissionChecksAgain()
        {
            var stale = Checked(CraftVerdict.PassWithWarnings, Warning());
            stale.Stale = true;
            Assert.Equal("(the craft changed since; Start Mission checks again)", MissionWindowRules.Footer(stale).Text);
        }

        [Fact]
        public void APassNeedsNoFooterUntilItIsOpen()
        {
            var pass = Checked(CraftVerdict.Pass);
            Assert.Equal("", MissionWindowRules.Footer(pass).Text);
        }

        [Fact]
        public void TheOtherStatesReadAsLines()
        {
            Assert.Equal("Not checked yet. Start Mission checks the craft first, or press Check again.", MissionWindowRules.StateLine(new CraftCheckView()));
            Assert.Equal("Checking the craft against the Mission Goal…", MissionWindowRules.StateLine(new CraftCheckView { State = CraftCheckState.Running }));
            Assert.Equal("Turn on the simulator", MissionWindowRules.StateLine(new CraftCheckView { State = CraftCheckState.NeedsSimulator, Message = "Turn on the simulator" }));
            Assert.Equal("It broke", MissionWindowRules.StateLine(new CraftCheckView { State = CraftCheckState.Error, Message = "It broke" }));
            Assert.True(MissionWindowRules.OffersCheckAgain(CraftCheckState.NeedsSimulator));
            Assert.True(MissionWindowRules.OffersCheckAgain(CraftCheckState.Error));
            Assert.False(MissionWindowRules.OffersCheckAgain(CraftCheckState.Running));
        }

        [Fact]
        public void TheClaudiaLineLeadsWithTheSummaryOnlyWhenThereIsOne()
        {
            var check = Checked(CraftVerdict.Pass);
            Assert.Equal("", MissionWindowRules.ClaudiaLine(check));
            check.Summary = "Two Warnings need your OK.";
            Assert.Equal("Two Warnings need your OK.", MissionWindowRules.ClaudiaLine(check));
        }

        [Fact]
        public void AFindingIsLabelledWarningOrBlocker()
        {
            Assert.Equal("WARNING", MissionWindowRules.FindingLabel(CraftFindingSeverity.Warning));
            Assert.Equal("BLOCKER", MissionWindowRules.FindingLabel(CraftFindingSeverity.Blocker));
        }

        [Fact]
        public void TheApprovalCheckboxNamesThePlan()
        {
            var view = During(MissionStage.AwaitingPlanApproval);
            Assert.Equal("Yes, approve the Mission Plan", MissionWindowRules.ApprovalLabel(view));
            view.Stage = MissionStage.AwaitingSequenceApproval;
            Assert.Equal("Yes, approve the Sequence Plan", MissionWindowRules.ApprovalLabel(view));
        }

        [Fact]
        public void ConfirmApprovalAndNotYetLeaveTheActionRow()
        {
            Assert.False(MissionWindowRules.InActionRow(QuickReply.ConfirmApproval));
            Assert.False(MissionWindowRules.InActionRow(QuickReply.NotYet));
            Assert.True(MissionWindowRules.InActionRow(QuickReply.Approve));
            Assert.False(MissionWindowRules.IsPrimary(QuickReply.ConfirmApproval));
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
