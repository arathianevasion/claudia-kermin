// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Blackout (spec: Mission state, Blackout and Manual Control; stories 99 to 105; ADR 0002's amendment). Claudia's
    /// link to Kerbin is a Core fact: the craft's link home (aboard, the vessel she is in; remote, the probe's link to
    /// KSC), from its Game Snapshots and, with the player in another vessel, from <see cref="CraftLinkSeen"/>; CommNet off
    /// in the save counts as linked. With no link the Mission is in a Blackout, a flag over whatever stage it is in, never
    /// a Pause. The player's Approval of the Mission Plan stands for each sequence, so the mod runs her next Sequence Plan
    /// and a revised Mission Plan without asking (a manual stretch or a Due Time still waits for the player, and the very
    /// first Mission Plan was never approved). Her messages are queued and the player's words held; Alerts are recorded
    /// without a Claude call; Abort is hidden; every request waits at the Spending Cap rather than Pausing (the waiting
    /// call). A Failed Debrief makes the craft safe and waits for the link. When the link returns, her queued messages
    /// show, each revision is listed with her reasons, and the player's held words go to her.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>No link to Kerbin: the Mission is in a Blackout (not saved: the next link fact after a load decides).</summary>
            public bool Blackout;
            /// <summary>The Blackout came back from a save and the craft's link has not been seen since: it neither ends nor goes on a guess.</summary>
            public bool BlackoutRestored;
            /// <summary>What Claudia decided without Approval in this Blackout, with her reasons, for the player when the link returns.</summary>
            public readonly List<string> BlackoutRevisions = new List<string>();
            /// <summary>The player's words typed in this Blackout, held until the link returns.</summary>
            public readonly List<string> HeldWords = new List<string>();
            /// <summary>The plan in hand waits for the player even in a Blackout (a manual stretch, a Due Time), and the log says so once.</summary>
            public bool BlackoutWaitNoted;
        }

        /// <summary>
        /// The Mission's craft has a link to Kerbin (or the save has no CommNet), as last seen; null when unknown, after a
        /// load or a restore until the craft is seen again, so nothing that waits for the link goes on a guess.
        /// </summary>
        private bool? _craftLink;

        private bool InBlackout
        {
            get { return _mission != null && _mission.Blackout; }
        }

        /// <summary>The craft's link, seen from another vessel (the adapter reads it from CommNet).</summary>
        private void CraftLinkReported(CraftLinkSeen seen)
        {
            var mission = _mission;
            if (mission == null || mission.VesselId.Length == 0 || seen.VesselId != mission.VesselId) return;
            _craftLink = seen.Linked;
        }

        /// <summary>After every input: a Blackout begins or ends with the link, and a plan in hand flies under the approved Mission Plan.</summary>
        private void CheckBlackout()
        {
            var mission = _mission;
            if (mission == null) return;
            if (mission.BlackoutRestored && !MissionOver)
            {
                if (_craftLink == null) return;
                mission.BlackoutRestored = false;
            }
            var dark = !MissionOver && FollowsCraft(mission) && _craftLink == false;
            if (dark && !mission.Blackout) BlackoutBegins(mission);
            else if (!dark && mission.Blackout && _craftLink != false) BlackoutEnds(mission);
            FlyInBlackout(mission);
        }

        private void BlackoutBegins(Mission mission)
        {
            mission.Blackout = true;
            mission.BlackoutWaitNoted = false;
            const string text = "No link to Kerbin: Blackout. Claudia flies the approved Mission Plan; her messages wait for the link, and yours are held.";
            Note(LogKind.Notice, text);
            // No screen message of its own: the warp drop (ADR 0004) and the strip's "No link" show the Blackout begin.
            Event(mission, "blackout", "the link to Kerbin was lost");
            // Abort is disabled: a recommendation on show goes, and Alerts not yet sent are recorded instead.
            mission.AbortRecommendation = "";
            foreach (var alert in mission.Queue.Where(t => t.Purpose == Purpose.Alert).ToList())
            {
                mission.Queue.Remove(alert);
                if (alert.Crossing != null && alert.Crossing.Verdict.Length == 0) alert.Crossing.Verdict = BlackoutVerdict;
            }
        }

        private void BlackoutEnds(Mission mission)
        {
            mission.Blackout = false;
            var news = mission.Log.Count(e => e.Queued) > 0 || mission.BlackoutRevisions.Count > 0;
            foreach (var entry in mission.Log) entry.Queued = false;
            Note(LogKind.Notice, "The link to Kerbin is back: the Blackout is over.");
            Event(mission, "link_back", mission.BlackoutRevisions.Count + " revisions");
            if (news) NeedsPlayer("The link to Kerbin is back: see what Claudia did in the Blackout.");
            if (mission.BlackoutRevisions.Count > 0)
            {
                AddCard(mission, LogKind.Notice, "While there was no link", "Claudia went on without your Approval:\n- "
                    + string.Join("\n- ", mission.BlackoutRevisions.ToArray()), "The approved Mission Plan stood for each sequence in the Blackout.");
                mission.BlackoutRevisions.Clear();
            }
            DeliverHeldWords(mission);
        }

        /// <summary>The player's words held in the Blackout go to Claudia now, as one question; a Paused Mission says them with its next turn.</summary>
        private void DeliverHeldWords(Mission mission)
        {
            if (mission.HeldWords.Count == 0) return;
            var said = "While the link to Kerbin was down the player said (quoted data): "
                + string.Join(" / ", mission.HeldWords.Select(MissionPrompts.Quoted).ToArray());
            mission.HeldWords.Clear();
            if (mission.Stage == MissionStage.Paused || mission.Stage == MissionStage.WaitingForFlight)
            {
                mission.LoadNotice = (mission.LoadNotice != null ? mission.LoadNotice + "\n\n" : "") + said;
                return;
            }
            Begin(new Turn
            {
                Purpose = Purpose.Question,
                UserText = said,
                Notice = MissionPrompts.QuestionNotice,
                Waits = true,
            });
        }

        /// <summary>
        /// In a Blackout, a plan in hand needs no Approval: the player approved the Mission Plan. A Sequence Plan runs and a
        /// revised Mission Plan is taken, each recorded for the player with her reasons. The first Mission Plan (no sequence
        /// asked for yet), a manual stretch and a plan with a Due Time wait for the player.
        /// </summary>
        private void FlyInBlackout(Mission mission)
        {
            if (!mission.Blackout || MissionOver || !CanAnswerPlan || mission.Declined) return;
            mission.ConfirmPrompt = null;
            var stale = StaleBecause(mission);
            if (mission.Stage == MissionStage.AwaitingPlanApproval)
            {
                if (mission.SequencesProposed == 0)
                {
                    WaitsForLink(mission, "The Mission Plan waits for your Approval when the link returns: you haven't approved one yet.");
                    return;
                }
                if (stale.Length > 0)
                {
                    Replanned();
                    return;
                }
                mission.BlackoutRevisions.Add("Mission Plan revised: " + mission.Plan.Why);
                Approval();
                return;
            }
            var plan = mission.PendingSequence;
            if (plan == null) return;
            if (plan.Manual)
            {
                WaitsForLink(mission, "The manual stretch " + MissionPrompts.Quoted(plan.Name) + " waits for you when the link returns.");
                return;
            }
            // A Due Time is a wait that needs the player (ADR 0004), so the plan waits for the link even once it is reached.
            if (plan.DueTimeUt.HasValue)
            {
                WaitsForLink(mission, "The Sequence Plan " + MissionPrompts.Quoted(plan.Name) + " has a Due Time for you, so it waits for the link.");
                return;
            }
            if (stale.Length > 0)
            {
                Replanned();
                return;
            }
            mission.BlackoutRevisions.Add("Sequence Plan " + MissionPrompts.Quoted(plan.Name) + " flown without Approval: " + plan.Why);
            Approval();
        }

        private void WaitsForLink(Mission mission, string text)
        {
            if (mission.BlackoutWaitNoted) return;
            mission.BlackoutWaitNoted = true;
            Note(LogKind.Notice, text);
        }

        /// <summary>
        /// In a Blackout the player's words are held until the link returns, and their answers to Claudia (Approve, Re-plan,
        /// a reduced goal, Abort, a script retry) wait for it. True: the input was taken here.
        /// </summary>
        private bool HeldForLink(MissionInput input)
        {
            var mission = _mission;
            if (MissionOver || !mission.Blackout) return false;
            var words = input is Reply ? ((Reply)input).Text
                : input is ChangeSomething ? ((ChangeSomething)input).Text
                : input is AskQuestion ? ((AskQuestion)input).Text
                : input is RetryWithNote ? ((RetryWithNote)input).Note
                : null;
            if (words != null)
            {
                words = words.Trim();
                if (words.Length == 0) return true;
                mission.HeldWords.Add(words);
                Note(LogKind.Player, words);
                Note(LogKind.Notice, "Held: there is no link to Kerbin. Claudia gets it when the link returns.");
                return true;
            }
            var answer = input is Approve || input is ConfirmApproval || input is Replan || input is AbortSequence || input is AcceptReducedGoal
                || (input is Retry && mission.Stage == MissionStage.UnsafeScript);
            if (!answer) return false;
            Note(LogKind.Notice, "No link to Kerbin: that waits for the link.");
            return true;
        }

        /// <summary>The verdict on a crossing that was recorded in a Blackout instead of being sent as an Alert.</summary>
        private const string BlackoutVerdict = "recorded in a Blackout, not judged";
        /// <summary>The verdict on an Alert whose Abort recommendation came back after the link dropped.</summary>
        private const string AbortNotOffered = "Abort recommended, not offered: no link to Kerbin";

        /// <summary>A Telemetry Check crossed in a Blackout: recorded, with no Claude call.</summary>
        private void RecordedInBlackout(Crossing crossing)
        {
            crossing.Verdict = BlackoutVerdict;
            Note(LogKind.Notice, "Telemetry Check crossed: " + crossing.Describe() + ". No link to Kerbin: recorded without a call to Claudia.");
        }

        /// <summary>Her lines and cards made in a Blackout wait for the link before the player sees them.</summary>
        private static void QueueIfBlackout(Mission mission, LogEntry entry)
        {
            if (mission == null || !mission.Blackout || entry.Kind == LogKind.Notice || entry.Kind == LogKind.Player) return;
            entry.Queued = true;
        }

        /// <summary>Claudia's news for the player on screen (a plan, a Debrief); in a Blackout it waits with her messages.</summary>
        private void HerNews(string text)
        {
            if (!InBlackout) NeedsPlayer(text);
        }

        /// <summary>Said to Claudia with every turn sent in a Blackout.</summary>
        private static string BlackoutNotice(Mission mission)
        {
            return mission.Blackout ? "\n\n" + BlackoutPrompts.Notice : "";
        }

        /// <summary>What the view shows of a Blackout: no link, her queued messages hidden, the answers that wait for the link gone.</summary>
        private static void ShowBlackout(MissionView view, Mission mission)
        {
            view.Log = mission.Log.Where(e => !e.Queued).ToList();
            if (!mission.Blackout) return;
            view.Blackout = true;
            view.QueuedMessages = mission.Log.Count(e => e.Queued);
            view.HeldMessages = mission.HeldWords.Count;
            view.LatestLine = "No link to Kerbin. " + (view.QueuedMessages > 0 ? view.QueuedMessages + " of Claudia's messages wait for the link." : "Claudia flies on.");
            view.AbortRecommended = "";
            view.ConfirmPrompt = null;
            view.QuickReplies = view.QuickReplies.Where(r => !WaitsForLinkReply(r, mission)).ToList();
            if (mission.AwaitingApproval && !mission.InFlight && !view.QuickReplies.Contains(QuickReply.AskQuestion))
                view.QuickReplies.Add(QuickReply.AskQuestion);
        }

        private static bool WaitsForLinkReply(QuickReply reply, Mission mission)
        {
            switch (reply)
            {
                case QuickReply.Approve:
                case QuickReply.ConfirmApproval:
                case QuickReply.NotYet:
                case QuickReply.ChangeSomething:
                case QuickReply.Replan:
                case QuickReply.Abort:
                case QuickReply.AcceptReducedGoal:
                case QuickReply.RetryWithNote:
                    return true;
                case QuickReply.Retry:
                    return mission.Stage == MissionStage.UnsafeScript;
                default:
                    return false;
            }
        }
    }

    /// <summary>The craft's link to Kerbin, read by the adapter while the player is in another vessel (CommNet off counts as linked).</summary>
    public sealed class CraftLinkSeen : MissionInput
    {
        public readonly string VesselId;
        public readonly bool Linked;
        public CraftLinkSeen(string vesselId, bool linked)
        {
            VesselId = vesselId ?? "";
            Linked = linked;
        }
    }

    internal static class BlackoutPrompts
    {
        public const string Notice =
            "Blackout: there is no link to Kerbin, so the player can't see or answer you until it returns. Their Approval of the "
            + "Mission Plan stands for each Maneuver Sequence: the mod runs your next Sequence Plan, and takes a revised Mission Plan, "
            + "without asking (a manual stretch or a Due Time still waits for the player). You may adjust Sequence Plans and revise "
            + "the Mission Plan, but only within the Mission Goal and the Spending Cap, and Custom Scripts pass the same Script Check. "
            + "Give your reasons in why: the player reads every revision when the link returns. A Failed Debrief makes the craft safe "
            + "and waits for the link; never end the Mission or reduce the goal yourself.";
    }
}
