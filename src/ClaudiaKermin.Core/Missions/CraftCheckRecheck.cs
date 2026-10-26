// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Craft Check run again inside a Mission as the margin recheck of a Debrief: the same facts, fixed finding lists and
    /// reply schema, put to the craft as it is now against the rest of the Mission Plan. Unlike the start check it leaves the
    /// flow's own result alone (the Mission has started; that result is history), and it does not charge itself: Mission
    /// Control builds the request, checks it against the Spending Cap, sends it with <see cref="SendRecheck"/> and
    /// charges what it cost like any other Mission request.
    /// </summary>
    internal sealed partial class CraftCheckFlow
    {
        /// <summary>The request for a margin recheck of <paramref name="snapshot"/> against <paramref name="restOfPlan"/> (the Maneuver Sequences still to fly).</summary>
        public MessagesRequest RecheckRequest(string goal, string model, GameSnapshot snapshot, bool remote, string restOfPlan)
        {
            var request = new MessagesRequest
            {
                Model = _config.Models.Contains(model) ? model : _config.DefaultModel,
                MaxTokens = 8000,
                Effort = _config.Efforts.For("margin_recheck"),
                OutputSchemaJson = CraftCheckRules.SchemaJson(),
                CompactionTriggerTokens = _config.CompactionTriggerTokens,
            };
            request.Tools.AddRange(Tools.Definitions(ClaudiaKermin.Core.Tools.ToolOffer.CraftCheck));
            request.System.Add(CraftCheckPrompts.System);
            request.System.Add(RecheckPrompts.System);
            request.Messages.Add(Message.UserText(RecheckPrompts.Facts(goal, snapshot, _config.CraftCheckMarginPercent, ResolvePreferred(snapshot), remote, restOfPlan)));
            return request;
        }

        /// <summary>Streams a margin recheck on the background queue; <paramref name="done"/> gets the result (null when the request itself blew up) on the main thread.</summary>
        public void SendRecheck(MessagesRequest request, CancellationToken cancel, Action<StreamResult> done)
        {
            _background.Run(
                () => _client.Stream(request, update => { }, cancel),
                done,
                error => done(null));
        }

        /// <summary>Claude's findings from a margin recheck's result, or null with <paramref name="problem"/> saying why there are none.</summary>
        public static CraftCheckReply ParseRecheck(StreamResult result, out string problem)
        {
            problem = null;
            if (result == null)
            {
                problem = "Something went wrong getting Claudia's reply";
                return null;
            }
            if (result.Status != StreamStatus.Completed)
            {
                problem = MissionControl.Describe(result);
                return null;
            }
            var message = result.Message;
            if (message.StopReason == StopReason.Refusal || message.StopReason == StopReason.MaxTokens)
            {
                problem = message.StopReason == StopReason.Refusal ? "Claudia declined to answer" : "Claudia's reply was cut short";
                return null;
            }
            var reply = CraftCheckReply.Parse(message.Text);
            if (reply == null) problem = "Claudia's reply didn't fit the Craft Check format";
            return reply;
        }
    }

    /// <summary>The words of the margin recheck request: the Craft Check's own, plus what is different about asking it mid-Mission.</summary>
    internal static class RecheckPrompts
    {
        public const string System =
            "This request is a margin recheck inside a running Mission, not the start check. The craft is as it is now, after a "
            + "Maneuver Sequence; the Mission Goal has not changed. Judge only whether the craft as it is now can still fly the "
            + "Maneuver Sequences still to fly, with the margin: report delta_v_short when the delta-v left (stages are listed with "
            + "stage 0 firing last; count only the stages that have not fired) falls short of their budgets plus the margin, and "
            + "any other finding from the fixed lists only when it holds now for what is still to fly. Leave out findings about "
            + "legs already flown. In summary, say the delta-v left against what is still to fly, key numbers first.";

        public static string Facts(string goal, GameSnapshot snapshot, int marginPercent, uint? preferredProcessor, bool remote, string restOfPlan)
        {
            var text = new StringBuilder();
            text.Append("Margin recheck after a Maneuver Sequence. Maneuver Sequences still to fly (quoted data, with their delta-v budgets): ")
                .Append(MissionPrompts.Quoted(restOfPlan)).Append('\n');
            text.Append(CraftCheckPrompts.Facts(goal, snapshot, marginPercent, preferredProcessor, remote));
            return text.ToString();
        }
    }
}
