// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Ui
{
    /// <summary>Why Claudia needs the player.</summary>
    public enum AttentionKind
    {
        /// <summary>A Debrief has come in.</summary>
        Debrief,
        /// <summary>A Mission Plan or Sequence Plan waits for Approval.</summary>
        Approval,
        /// <summary>The Mission is Paused, or couldn't produce a safe script: the player must act.</summary>
        Pause,
        /// <summary>Claudia judged an Alert and recommends Abort: the window comes up with its Abort button.</summary>
        Abort,
    }

    public sealed class Attention
    {
        public readonly AttentionKind Kind;
        public readonly string Text;

        public Attention(AttentionKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>
    /// Watches successive <see cref="MissionView"/>s and reports each new moment Claudia needs the player
    /// (a Debrief, an Approval, a Pause) exactly once. The adapter turns a report into the closed-window
    /// alerts (toolbar flash, screen message, warp to 1x). The first view of a Mission only sets the baseline,
    /// so loading a save with a plan already waiting doesn't alert; the Mission board shows it.
    /// </summary>
    public sealed class AttentionWatch
    {
        private string _missionId = "";
        private int _debriefs;
        private string _waiting = "";
        private string _abort = "";

        public Attention Observe(MissionView view)
        {
            if (view == null || view.MissionId.Length == 0)
            {
                _missionId = "";
                _debriefs = 0;
                _waiting = "";
                _abort = "";
                return null;
            }

            // An Abort recommendation is the most urgent moment there is: it goes before everything else.
            var newAbort = view.MissionId == _missionId && view.AbortRecommended.Length > 0 && view.AbortRecommended != _abort;
            _abort = view.AbortRecommended;
            if (newAbort) return new Attention(AttentionKind.Abort, "Claudia recommends Abort.");

            var debriefs = view.Log.Count(e => e.Kind == LogKind.Debrief);
            var waiting = Waiting(view);
            var first = view.MissionId != _missionId;
            var lastDebrief = view.Log.LastOrDefault(e => e.Kind == LogKind.Debrief);
            var newDebrief = !first && debriefs > _debriefs;
            var newWaiting = !first && waiting.Length > 0 && waiting != _waiting;

            _missionId = view.MissionId;
            _debriefs = debriefs;
            _waiting = waiting;

            // A Debrief is the headline when it arrives together with what it leads to.
            if (newDebrief) return new Attention(AttentionKind.Debrief, "Claudia's " + (lastDebrief.Title.Length > 0 ? lastDebrief.Title : "Debrief") + " is ready.");
            if (!newWaiting) return null;
            switch (view.Stage)
            {
                case MissionStage.Paused: return new Attention(AttentionKind.Pause, "Claudia's Mission is Paused.");
                case MissionStage.UnsafeScript: return new Attention(AttentionKind.Pause, "Claudia couldn't produce a safe script.");
                default: return new Attention(AttentionKind.Approval, "Claudia has a plan for your Approval.");
            }
        }

        /// <summary>A key for what the player is being waited on for, "" when nothing: it changes with each new plan or pause.</summary>
        private string Waiting(MissionView view)
        {
            switch (view.Stage)
            {
                case MissionStage.AwaitingPlanApproval:
                case MissionStage.AwaitingSequenceApproval:
                    // A question asked meanwhile hides the quick replies; it is the same wait, so no new alert.
                    if (view.Busy) return _waiting.StartsWith("approval:", System.StringComparison.Ordinal) ? _waiting : "";
                    // A declined goal has no plan to approve; the card count tells one plan from the next.
                    return view.QuickReplies.Contains(QuickReply.Approve) || view.QuickReplies.Contains(QuickReply.ConfirmApproval)
                        ? "approval:" + view.Log.Count(e => e.Kind == LogKind.MissionPlan || e.Kind == LogKind.SequencePlan) + ":" + view.Stage
                        : "";
                case MissionStage.Paused:
                case MissionStage.UnsafeScript:
                    // Leaving the Paused stage (a Retry) clears the key, so a Pause that comes back alerts again.
                    return "pause:" + view.Stage + ":" + view.PauseReason;
                default:
                    return "";
            }
        }
    }
}
