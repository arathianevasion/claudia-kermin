// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Missions
{
    public sealed partial class MissionControl
    {
        // Typed approve, revise and Mission Goal change. Whatever mode the prompt was in, words typed at a pending plan
        // are read by Claudia (reply_intent) and acted on here: an approval shows the Approval confirmation (ADR 0002:
        // typed words never start anything), a revision is one more request for the same kind of plan, and a changed
        // Mission Goal comes with a revised Mission Plan whose Approval makes it the Mission's goal.

        /// <summary>
        /// A question that was asked while a plan awaited Approval, answered. Her reply_intent decides: approve shows the
        /// confirmation, revise asks once for a revised plan (through the Spending Cap path, like any request), and
        /// anything else changes nothing.
        /// </summary>
        private void QuestionAnswered(Mission mission, Turn turn, MissionReply reply)
        {
            if (turn.PlayerText == null || !mission.AwaitingApproval) return;
            if (reply.Intent == ReplyIntent.Approve)
            {
                if (!mission.Declined) mission.ConfirmPrompt = ApprovalQuestion(mission);
            }
            else if (reply.Intent == ReplyIntent.Revise)
            {
                var missionPlan = mission.Stage == MissionStage.AwaitingPlanApproval;
                Begin(new Turn
                {
                    Purpose = Purpose.Change,
                    Effort = "high",
                    UserText = "The player wants a change (quoted data): " + MissionPrompts.Quoted(turn.PlayerText),
                    Notice = MissionPrompts.ChangeNotice(missionPlan),
                });
            }
        }

        private static string ApprovalQuestion(Mission mission)
        {
            return mission.Stage == MissionStage.AwaitingPlanApproval ? "Approve the Mission Plan?" : "Approve the Sequence Plan?";
        }

        /// <summary>A revised Mission Plan with a new goal also replaces a pending Sequence Plan, which was for the old goal.</summary>
        private static bool ChangesGoal(MissionReply reply)
        {
            return reply.MissionPlan != null && reply.MissionPlan.NewGoal != null;
        }

        /// <summary>The Mission Plan being approved carries a new Mission Goal: it becomes the pinned goal, the stored goal, and an event.</summary>
        /// <returns>The new goal, or null when the plan carries none or it is the goal already.</returns>
        private string ApplyGoalChange(Mission mission)
        {
            var goal = mission.Plan == null ? null : mission.Plan.NewGoal;
            if (goal == null || string.Equals(goal, mission.Goal, StringComparison.Ordinal)) return null;
            var old = mission.Goal;
            mission.Goal = goal;
            Note(LogKind.Notice, "Mission Goal changed to: " + goal);
            try
            {
                MissionStore.WriteInfo(mission.Folder, new MissionInfo { Id = mission.Id, Goal = mission.Goal, Model = mission.Model, SpendingCap = mission.SpendingCap });
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't write the Mission's record: " + e.Message);
            }
            Event(mission, "goal_changed", goal + " (was: " + old + ")");
            return goal;
        }
    }
}
