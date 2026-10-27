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
            /// <summary>
            /// Auto Mode re-planned an unsafe script, and no plan has passed the Script Check since: a second failure waits
            /// for the player (not saved).
            /// </summary>
            public bool AutoModeReplanned;
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
            /// <summary>Auto Mode only: the script failed the Script Check again after Auto Mode's re-plan.</summary>
            ScriptFailedAgain,
        }

        /// <summary>The Auto Mode checkbox, in any stage of a Mission in play. Ticking it lets the check below approve a plan that waits.</summary>
        private void AutoModeSet(bool on)
        {
            var mission = _mission;
            if (MissionOver || mission.AutoMode == on) return;
            mission.AutoMode = on;
            mission.AutoModeHeldFor = null;
            mission.AutoModeApprovedFor = null;
            mission.AutoModeReplanned = false;
            Event(mission, "auto_mode", on ? "on" : "off");
        }

        /// <summary>
        /// After every input and each reply: whether the plan in hand already has the player's Approval, given in a Blackout
        /// or under Auto Mode. Either way the first Mission Plan, a manual stretch and a plan with a Due Time wait for the
        /// player, and a stale plan is re-planned. Under Auto Mode (and no Blackout, whose own rules stand) an approval or
        /// re-plan also needs room in the Spending Cap (<see cref="AutoModeRoom"/>). The Approval itself is
        /// <see cref="Approval"/>, so the Script Check, the late-window and the Due Time rules hold as for the player's button.
        /// Auto Mode also answers what Blackout leaves for the player (<see cref="AutoModeAnswers"/>): an Abort
        /// recommendation, a reduced goal and an unsafe script.
        /// </summary>
        private void ApproveInAdvance(Mission mission)
        {
            if (!mission.Blackout && !mission.AutoMode) return;
            if (MissionOver) return;
            if (mission.AutoMode && AutoModeAnswers(mission)) return;
            if (!CanAnswerPlan || mission.Declined) return;
            // A plan reached Approval, so it passed the Script Check: the next unsafe script gets its own re-plan.
            mission.AutoModeReplanned = false;
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
            if (!mission.Blackout && !AutoModeRoom(mission, inHand, stale.Length > 0 ? ReplanTurn(stale, missionPlan) : null))
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
        /// What Auto Mode answers beyond Blackout's rules (spec #327, "One approve-in-advance path" and "Abort"). An Abort
        /// recommendation is taken as if the player pressed Abort, in a Blackout too and with no room check: stopping
        /// costs nothing. A reduced goal on offer is approved, and an unsafe script re-planned once per plan, each subject
        /// to the room check with the request it starts priced in (no room check in a Blackout, as for any plan there).
        /// True: the stage was Auto Mode's to answer, whether it answered or left it waiting.
        /// </summary>
        private bool AutoModeAnswers(Mission mission)
        {
            if (mission.AbortRecommendation.Length > 0) AbortTaken(byAutoMode: true);
            if (mission.InFlight) return false;
            if (mission.Stage == MissionStage.AwaitingReducedGoal && mission.ReducedGoal.Length > 0)
            {
                if (!mission.Blackout && !AutoModeRoom(mission, mission.ReducedGoal, ReducedGoalTurn(mission)))
                {
                    WaitsForPlayer(mission, AdvanceWait.NoRoom, "");
                    return true;
                }
                ReducedGoalAccepted(inAdvance: true);
                return true;
            }
            if (mission.Stage != MissionStage.UnsafeScript) return false;
            if (mission.AutoModeReplanned)
            {
                WaitsForPlayer(mission, AdvanceWait.ScriptFailedAgain, "");
                return true;
            }
            if (!mission.Blackout && !AutoModeRoom(mission, mission.UnsafeProblems, ScriptRetryTurn(mission, null, replan: true)))
            {
                WaitsForPlayer(mission, AdvanceWait.NoRoom, "");
                return true;
            }
            mission.AutoModeReplanned = true;
            Note(LogKind.Notice, AutoModeReplannedText);
            ScriptRetried(null, replan: true);
            return true;
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
        /// that (and of <paramref name="starts"/>, the request an approval or re-plan starts itself: a stale plan's re-plan,
        /// an unsafe script's, a reduced goal's Mission Plan) fits the Spending Cap. Worst cases by the formula
        /// <see cref="MaySend"/> uses, never past costs, so Auto Mode can't approve a plan whose Debrief would be refused
        /// (ADR 0009). A tool loop charges per step, so <see cref="MaySend"/> stays the backstop.
        /// </summary>
        private bool AutoModeRoom(Mission mission, object inHand, Turn starts)
        {
            var worst = AutoModeReserve(mission);
            if (starts != null) worst += RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, starts));
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
        /// <summary>Logged after the "Couldn't produce a safe script" card when Auto Mode re-plans it.</summary>
        private const string AutoModeReplannedText = "Re-planned by Auto Mode.";
    }

    /// <summary>The Auto Mode checkbox: the player's Approval given in advance for this Mission, or taken back. Accepted in any stage of a Mission in play.</summary>
    public sealed class SetAutoMode : MissionInput
    {
        public readonly bool On;
        public SetAutoMode(bool on) { On = on; }
    }
}
