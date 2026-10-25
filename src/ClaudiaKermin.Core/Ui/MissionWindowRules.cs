// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>The colours a chip takes (the visual spec's info, success, attention and danger).</summary>
    public enum ChipTone
    {
        Info,
        Success,
        Attention,
        Danger,
    }

    /// <summary>The status chip in the Mission window's title bar.</summary>
    public sealed class StatusChip
    {
        public readonly string Text;
        public readonly ChipTone Tone;

        public StatusChip(string text, ChipTone tone)
        {
            Text = text;
            Tone = tone;
        }
    }

    /// <summary>What the Mission window's prompt writes.</summary>
    public enum PromptFor
    {
        /// <summary>Before a Mission: Send pins the text as the Mission Goal.</summary>
        Goal,
        /// <summary>A reply to a plan awaiting Approval (classified by Claude).</summary>
        Reply,
        /// <summary>Change: what should change in the plan awaiting Approval.</summary>
        Change,
        /// <summary>A question for Claudia; what Reply becomes when no plan awaits Approval.</summary>
        Question,
        /// <summary>The note for Retry with a note, after "Couldn't produce a safe script".</summary>
        Note,
    }

    /// <summary>The prompt as drawn: what it writes, its caption and hint, and the placeholder that explains it when it is empty or disabled.</summary>
    public sealed class PromptState
    {
        public PromptFor For;
        public string Caption = "";
        public string Hint = "";
        public string Placeholder = "";
        public bool Enabled;
    }

    /// <summary>
    /// What the compact Mission window shows for a <see cref="MissionView"/>, kept apart from the drawing so it can be
    /// tested: the status chip, the prompt's mode and what it sends, the action row's labels and primary action, and the
    /// control bar's setup summary and spend meter. Presentation only: it decides no Mission behaviour.
    /// </summary>
    public static class MissionWindowRules
    {
        /// <summary>The title bar's chip; null when there is nothing to say (no Mission, and no Craft Check result to report).</summary>
        public static StatusChip Status(MissionView view)
        {
            if (view.StartForm.CanStart)
            {
                var check = CheckChip(view.CraftCheck);
                if (check != null) return check;
            }
            return StageChip(view);
        }

        public const string KeysHint = "Enter sends · Shift+Enter adds a line";

        /// <summary>
        /// The prompt for <paramref name="chosen"/> (what Change, Ask or Retry with a note last picked), settled against the view:
        /// before a Mission it writes the Mission Goal until one is pinned (<paramref name="goalPinned"/>, false while it is being edited);
        /// during one it is a reply only while a plan awaits Approval, a note only after an unsafe script, and otherwise a question.
        /// </summary>
        public static PromptState Prompt(MissionView view, PromptFor chosen, bool goalPinned)
        {
            if (view.StartForm.CanStart)
            {
                if (goalPinned) return new PromptState { For = PromptFor.Goal, Caption = "Ask Claudia", Hint = KeysHint, Placeholder = "You can ask Claudia once the Mission starts" };
                return new PromptState { For = PromptFor.Goal, Caption = "Mission Goal", Hint = KeysHint, Placeholder = "Describe the Mission Goal: what should Claudia achieve?", Enabled = true };
            }
            if (!view.CanEndMission && view.Stage == MissionStage.None)
                return new PromptState { For = PromptFor.Goal, Caption = "Mission Goal", Hint = KeysHint, Placeholder = "Load a save to set up a Mission" };

            var awaiting = view.Stage == MissionStage.AwaitingPlanApproval || view.Stage == MissionStage.AwaitingSequenceApproval;
            var unsafeScript = view.Stage == MissionStage.UnsafeScript;
            var mode = chosen;
            if (mode == PromptFor.Goal || (mode == PromptFor.Change && !awaiting) || (mode == PromptFor.Note && !unsafeScript)) mode = PromptFor.Reply;
            if (mode == PromptFor.Reply && !awaiting) mode = PromptFor.Question;

            var manual = view.Stage == MissionStage.ManualControl;
            return new PromptState
            {
                For = mode,
                Caption = manual ? "Ask Claudia, or say what you did"
                    : mode == PromptFor.Change ? "What should change?"
                    : mode == PromptFor.Note ? "Note for Claudia's retry:"
                    : mode == PromptFor.Question ? "Ask Claudia" : "Reply to Claudia",
                Hint = manual ? "Hand back sends what you typed with it" : KeysHint,
                Placeholder = view.CanType ? "" : Unavailable(view),
                Enabled = view.CanType,
            };
        }

        private static string Unavailable(MissionView view)
        {
            if (view.Busy) return "Claudia is " + (view.Activity.Length > 0 ? view.Activity : "thinking") + "…";
            switch (view.Stage)
            {
                case MissionStage.WaitingForFlight: return "Claudia plans once the craft is in Flight";
                case MissionStage.Paused: return "The Mission is Paused: use the buttons below";
                case MissionStage.Lost: return "The Mission is over";
                default: return "Claudia can't take a message right now";
            }
        }

        /// <summary>What Send sends for the prompt's settled mode; null for the Mission Goal, which the window pins itself.</summary>
        public static MissionInput Send(PromptFor mode, string text)
        {
            switch (mode)
            {
                case PromptFor.Reply: return new Reply(text);
                case PromptFor.Change: return new ChangeSomething(text);
                case PromptFor.Question: return new AskQuestion(text);
                case PromptFor.Note: return new RetryWithNote(text);
                default: return null;
            }
        }

        /// <summary>The label with the view's names in it: Go to <i>vessel</i>.</summary>
        public static string Label(QuickReply reply, MissionView view)
        {
            if (reply == QuickReply.GoToVessel && view.DueTime.VesselName.Length > 0) return "Go to " + view.DueTime.VesselName;
            switch (reply)
            {
                case QuickReply.Approve: return "Approve";
                case QuickReply.ChangeSomething: return "Change";
                case QuickReply.AskQuestion: return "Ask";
                case QuickReply.ConfirmApproval: return "Yes, approve";
                case QuickReply.NotYet: return "Not yet";
                case QuickReply.RaiseCap: return "Raise cap";
                case QuickReply.EndMission: return "End Mission";
                case QuickReply.Resume: return "Resume";
                case QuickReply.RetryWithNote: return "Retry with a note";
                case QuickReply.Replan: return "Re-plan";
                case QuickReply.WarpToDueTime: return "Warp to Due Time";
                case QuickReply.GoToVessel: return "Go to vessel";
                case QuickReply.ManualControl: return "Take controls";
                case QuickReply.HandBack: return "Hand back to Claudia";
                default: return "Retry";
            }
        }

        /// <summary>The action the row fills with the accent and stretches; at most one is offered at a time.</summary>
        public static bool IsPrimary(QuickReply reply)
        {
            return reply == QuickReply.Approve || reply == QuickReply.ConfirmApproval || reply == QuickReply.HandBack;
        }

        /// <summary>Start Mission's label: what holds it back when a current Craft Check does.</summary>
        public static string StartLabel(MissionView view)
        {
            var check = view.CraftCheck;
            if (check.State == CraftCheckState.Done && !check.Stale && !check.StartAllowed)
            {
                if (check.Verdict == CraftVerdict.Fail) return "Start Mission · fix the Blockers";
                var open = ToAcknowledge(check);
                if (open > 0) return "Start Mission · " + open.ToString(CultureInfo.InvariantCulture) + " to acknowledge";
            }
            return "Start Mission";
        }

        /// <summary>The pending setup in one line, e.g. "Crewed · sonnet-5-5 · est. ~$2.40"; the estimate is left out when there is none.</summary>
        public static string SetupSummary(MissionView view, bool remote, string model)
        {
            var text = (remote ? "Remote" : "Crewed") + " · " + model;
            decimal estimate;
            if (view.StartForm.Estimates.TryGetValue(model, out estimate)) text += " · est. ~$" + Dollars(estimate);
            return text;
        }

        /// <summary>What counts against the Spending Cap over the cap, e.g. "$1.42 / $10.00".</summary>
        public static string SpendMeter(MissionView view)
        {
            return "$" + Dollars(view.Spent + view.HeldCharges) + " / $" + Dollars(view.SpendingCap);
        }

        /// <summary>How full the spend meter's bar is, 0 to 1.</summary>
        public static float SpendFraction(MissionView view)
        {
            if (view.SpendingCap <= 0) return 0;
            return (float)System.Math.Min(1m, System.Math.Max(0m, (view.Spent + view.HeldCharges) / view.SpendingCap));
        }

        /// <summary>"step 2 of 3": the Maneuver Sequence in hand in the Mission Plan; "" before the first.</summary>
        public static string Step(MissionView view)
        {
            if (view.PlanSequences.Count == 0 || view.CurrentSequence < 0) return "";
            return "step " + (view.CurrentSequence + 1).ToString(CultureInfo.InvariantCulture) + " of " + view.PlanSequences.Count.ToString(CultureInfo.InvariantCulture);
        }

        private static string Dollars(decimal amount)
        {
            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static StatusChip CheckChip(CraftCheckView check)
        {
            switch (check.State)
            {
                case CraftCheckState.Running: return new StatusChip("Checking the craft", ChipTone.Info);
                case CraftCheckState.NeedsSimulator:
                case CraftCheckState.Error: return new StatusChip("Craft Check needs you", ChipTone.Attention);
                case CraftCheckState.Done:
                    if (check.Stale) return null;
                    if (check.Verdict == CraftVerdict.Fail) return new StatusChip("Craft Check · fix the Blockers", ChipTone.Danger);
                    var open = ToAcknowledge(check);
                    if (open > 0) return new StatusChip("Craft Check · " + open.ToString(CultureInfo.InvariantCulture) + " to acknowledge", ChipTone.Attention);
                    return check.StartAllowed ? new StatusChip("Ready to start", ChipTone.Success) : null;
                default: return null;
            }
        }

        /// <summary>The Warnings of the Craft Check not yet acknowledged.</summary>
        public static int ToAcknowledge(CraftCheckView check)
        {
            return check.Findings.Count(f => f.Severity == CraftFindingSeverity.Warning && !f.Acknowledged);
        }

        private static StatusChip StageChip(MissionView view)
        {
            if (view.ConfirmPrompt != null) return new StatusChip("Confirm Approval", ChipTone.Attention);
            switch (view.Stage)
            {
                case MissionStage.WaitingForFlight: return new StatusChip("Waiting for Flight", ChipTone.Info);
                case MissionStage.Planning: return new StatusChip("Planning", ChipTone.Info);
                case MissionStage.Sequencing: return new StatusChip("Planning the next sequence", ChipTone.Info);
                case MissionStage.Running: return new StatusChip("Running", ChipTone.Info);
                case MissionStage.Debriefing: return new StatusChip("Debriefing", ChipTone.Info);
                case MissionStage.AwaitingPlanApproval:
                case MissionStage.AwaitingSequenceApproval: return new StatusChip("Plan awaiting Approval", ChipTone.Info);
                case MissionStage.Paused: return new StatusChip("Paused", ChipTone.Attention);
                case MissionStage.UnsafeScript: return new StatusChip("No safe script", ChipTone.Attention);
                case MissionStage.ManualControl: return new StatusChip("Manual Control · you fly", ChipTone.Attention);
                case MissionStage.Succeeded: return new StatusChip("Mission Goal met", ChipTone.Success);
                case MissionStage.Ended: return new StatusChip("Mission ended", ChipTone.Danger);
                case MissionStage.Lost: return new StatusChip("Mission Lost", ChipTone.Danger);
                default: return null;
            }
        }
    }
}
