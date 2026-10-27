// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Approval given in advance (spec #327, ADR 0009). The player gives it in a Blackout, where their Approval of the
    /// Mission Plan stands for each sequence (MissionControl.Blackout.cs), and under Auto Mode, a per-Mission switch
    /// (not a Mission Setting: flipping it asks Claudia for nothing) under which every plan after the first Mission Plan
    /// is approved the moment it arrives, while the Spending Cap has room for the Debrief and the next plan request that
    /// follow. One check decides both, so the rules for what may fly without the player live in one place.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The player's Approval is given in advance (Auto Mode); saved with the Mission.</summary>
            public bool AutoMode;
            /// <summary>The plan the Auto Mode room check last held for the player, so its blocked charge is logged once (not saved).</summary>
            public object AutoModeHeldFor;
            /// <summary>
            /// The plan Auto Mode last approved: if <see cref="Approval"/> refused it (no processor to run it on, an unsafe
            /// script), it waits for the player rather than being approved again on every input (not saved).
            /// </summary>
            public object AutoModeApprovedFor;
        }

        /// <summary>What a plan in hand waits for, though the player's Approval was given in advance.</summary>
        private enum AdvanceWait
        {
            /// <summary>The very first Mission Plan: the player never approved one, so there is nothing to stand for it.</summary>
            FirstMissionPlan,
            /// <summary>Only the player flies a manual stretch.</summary>
            ManualStretch,
            /// <summary>A Due Time is a wait that needs the player (ADR 0004), even once it is reached.</summary>
            DueTime,
            /// <summary>Auto Mode only: the Spending Cap has no room for the Debrief and next plan request that follow.</summary>
            NoRoom,
        }

        /// <summary>The Auto Mode checkbox, in any stage of a Mission in play. Ticking it lets the check below approve a plan that waits.</summary>
        private void AutoModeSet(bool on)
        {
            var mission = _mission;
            if (MissionOver || mission.AutoMode == on) return;
            mission.AutoMode = on;
            mission.AutoModeHeldFor = null;
            mission.AutoModeApprovedFor = null;
            Event(mission, "auto_mode", on ? "on" : "off");
        }

        /// <summary>
        /// After every input and each reply: whether the plan in hand already has the player's Approval, given in a Blackout
        /// or under Auto Mode. Either way the first Mission Plan, a manual stretch and a plan with a Due Time wait for the
        /// player, and a stale plan is re-planned. Under Auto Mode (and no Blackout, whose own rules stand) an approval or
        /// re-plan also needs room in the Spending Cap (<see cref="AutoModeRoom"/>). The Approval itself is
        /// <see cref="Approval"/>, so the Script Check, the late-window and the Due Time rules hold as for the player's button.
        /// </summary>
        private void ApproveInAdvance(Mission mission)
        {
            if (!mission.Blackout && !mission.AutoMode) return;
            if (MissionOver || !CanAnswerPlan || mission.Declined) return;
            // ADR 0002: typed words never start anything, so a pending "approve?" is moot once Approval stands.
            if (mission.Blackout) mission.ConfirmPrompt = null;
            var missionPlan = mission.Stage == MissionStage.AwaitingPlanApproval;
            var plan = mission.PendingSequence;
            if (missionPlan)
            {
                if (mission.SequencesProposed == 0)
                {
                    WaitsForPlayer(mission, AdvanceWait.FirstMissionPlan, "");
                    return;
                }
            }
            else
            {
                if (plan == null) return;
                if (plan.Manual)
                {
                    WaitsForPlayer(mission, AdvanceWait.ManualStretch, plan.Name);
                    return;
                }
                if (plan.DueTimeUt.HasValue)
                {
                    WaitsForPlayer(mission, AdvanceWait.DueTime, plan.Name);
                    return;
                }
            }
            object inHand = missionPlan ? (object)mission.Plan : plan;
            if (!mission.Blackout && mission.AutoModeApprovedFor == inHand) return;
            var stale = StaleBecause(mission);
            if (!mission.Blackout && !AutoModeRoom(mission, inHand, stale, missionPlan))
            {
                WaitsForPlayer(mission, AdvanceWait.NoRoom, missionPlan ? "" : plan.Name);
                return;
            }
            mission.ConfirmPrompt = null;
            if (stale.Length > 0)
            {
                Replanned(inAdvance: true);
                return;
            }
            if (mission.Blackout)
                mission.BlackoutRevisions.Add(missionPlan
                    ? "Mission Plan revised: " + mission.Plan.Why
                    : "Sequence Plan " + MissionPrompts.Quoted(plan.Name) + " flown without Approval: " + plan.Why);
            else
            {
                mission.AutoModeApprovedFor = inHand;
                Note(LogKind.Notice, AutoModeApproved);
            }
            Approval();
        }

        /// <summary>
        /// The plan waits for the player as any plan does, with the usual quick replies and attention. In a Blackout the log
        /// says so once; under Auto Mode nothing more is said yet (the stop messages are spec #327's Wording).
        /// </summary>
        private void WaitsForPlayer(Mission mission, AdvanceWait wait, string planName)
        {
            if (mission.Blackout) WaitsForLink(mission, wait, planName);
        }

        /// <summary>
        /// The room check: Committed plus the worst case of the Debrief after the plan and of the Sequence Plan request after
        /// that (and of the re-plan itself, for a stale plan) fits the Spending Cap. Worst cases by the formula
        /// <see cref="MaySend"/> uses, never past costs, so Auto Mode can't approve a plan whose Debrief would be refused
        /// (ADR 0009). A tool loop charges per step, so <see cref="MaySend"/> stays the backstop.
        /// </summary>
        private bool AutoModeRoom(Mission mission, object inHand, string stale, bool missionPlan)
        {
            var worst = AutoModeReserve(mission);
            if (stale.Length > 0) worst += RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, ReplanTurn(stale, missionPlan)));
            if (mission.Committed + worst <= mission.SpendingCap) return true;
            if (mission.AutoModeHeldFor != inHand)
            {
                mission.AutoModeHeldFor = inHand;
                LogCost(mission, "blocked", worst, "auto_mode_room");
            }
            return false;
        }

        /// <summary>
        /// What the Debrief after the plan in hand and the Sequence Plan request after it could cost at worst. The Debrief is
        /// <see cref="DebriefReserve"/>'s probe of a run that reported nothing; the next plan is a probe built as
        /// <see cref="BeginAfterDebrief"/> builds it, behind that Debrief, its full reply and the same slack.
        /// </summary>
        private decimal AutoModeReserve(Mission mission)
        {
            var ran = mission.PendingSequence ?? new SequencePlan { Name = "" };
            var debrief = new Turn
            {
                Purpose = Purpose.Debrief,
                UserText = MissionPrompts.RunReport(ran, new RunOutcome(RunOutcomeKind.Finished, null, "", "", false), "") + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.DebriefNotice,
            };
            var next = BuildRequest(mission, NextSequenceTurn(offNominal: true));
            next.Messages.Insert(next.Messages.Count - 1, Message.UserText(debrief.UserText));
            next.Messages.Insert(next.Messages.Count - 1, Message.SystemText(debrief.Notice));
            var nextInput = (next.ToJson().Length + ProbeSlack + 2) / 3 + MissionPrompts.MaxTokens;
            return DebriefReserve(mission, debrief) + _config.Prices.WorstCase(mission.Model, nextInput, next.MaxTokens);
        }

        /// <summary>Logged after a plan's card when Auto Mode approves it.</summary>
        private const string AutoModeApproved = "Approved by Auto Mode.";
    }

    /// <summary>The Auto Mode checkbox: the player's Approval given in advance for this Mission, or taken back. Accepted in any stage of a Mission in play.</summary>
    public sealed class SetAutoMode : MissionInput
    {
        public readonly bool On;
        public SetAutoMode(bool on) { On = on; }
    }
}
