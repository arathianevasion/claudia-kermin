// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Ui;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The text of the Mission log's cards, shared by the loop (which builds a card when a plan or Debrief arrives)
    /// and by <see cref="MissionLogRebuild"/> (which builds the same cards again from the saved transcript).
    /// </summary>
    internal static class MissionCards
    {
        public static string MetresPerSecond(double value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s";
        }

        /// <summary>A Sequence Plan's vessel is the craft (named <paramref name="craftName"/>): "", "craft" or the craft's own name.</summary>
        public static bool FliesCraft(string craftName, string vessel)
        {
            vessel = (vessel ?? "").Trim();
            return vessel.Length == 0 || string.Equals(vessel, "craft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(vessel, craftName, StringComparison.OrdinalIgnoreCase);
        }

        /// <param name="craftName">The Mission's craft, so a sequence that flies a Child Vessel is marked with that vessel's name.</param>
        public static string MissionPlanBody(MissionPlan plan, string craftName)
        {
            var body = new StringBuilder();
            for (var i = 0; i < plan.Sequences.Count; i++)
            {
                var s = plan.Sequences[i];
                body.Append(i + 1).Append(". ").Append(s.Name).Append(s.Manual ? " [you fly this by hand]" : "")
                    .Append(FliesCraft(craftName, s.Vessel) ? "" : " [" + s.Vessel + "]").Append(": ").Append(s.Intent)
                    .Append(" (").Append(MetresPerSecond(s.DeltaV)).Append(", ").Append(s.Timing);
                if (s.DueTimeUt.HasValue) body.Append("; Due Time ").Append(KerbinTime.Date(s.DueTimeUt.Value));
                body.Append(")\n");
            }
            body.Append(ChildVesselLines(plan));
            body.Append("Total ").Append(MetresPerSecond(plan.TotalDeltaV)).Append(", ").Append(plan.ExpectedDuration).Append('.');
            if (plan.MissionTimeSeconds.HasValue) body.Append(" Total Mission time ").Append(KerbinTime.Span(plan.MissionTimeSeconds.Value)).Append('.');
            return body.ToString();
        }

        /// <summary>The Mission Plan card's lines about Child Vessels.</summary>
        private static string ChildVesselLines(MissionPlan plan)
        {
            if (plan.ChildVessels.Count == 0) return "";
            return "Child Vessels: " + string.Join(", ", plan.ChildVessels.Select(c => c.Name + (c.SeparationStage.HasValue
                ? " (separates at stage " + c.SeparationStage.Value.ToString(CultureInfo.InvariantCulture) + ")" : " (already separated)")).ToArray()) + "\n";
        }

        /// <param name="craftName">The Mission's craft, so a plan that flies a Child Vessel says so.</param>
        /// <param name="ut">The game time to say a Due Time is "in" or "reached" against; null leaves it out (a card rebuilt from the transcript has no "now").</param>
        public static string SequencePlanBody(SequencePlan plan, string craftName, double? ut, PlayerConfig config)
        {
            var body = new StringBuilder();
            if (plan.DueTimeUt.HasValue)
            {
                body.Append("Due Time: ").Append(KerbinTime.Date(plan.DueTimeUt.Value));
                if (ut.HasValue)
                    body.Append(plan.DueTimeUt.Value > ut.Value ? " (in " + KerbinTime.Span(plan.DueTimeUt.Value - ut.Value) + ")" : " (reached)");
                body.Append('\n');
            }
            if (plan.Manual) body.Append("Manual stretch: you fly this one by hand; Approve gives you the controls.\n");
            if (!FliesCraft(craftName, plan.Vessel)) body.Append("Flies the Child Vessel ").Append(plan.Vessel).Append('\n');
            body.Append(plan.Intent).Append('\n')
                .Append("Target: ").Append(plan.Target).Append(" (tolerances: ").Append(plan.Tolerances).Append(")\n")
                .Append("Expected: ").Append(MetresPerSecond(plan.ExpectedDeltaV)).Append(", ")
                .Append(plan.ExpectedDurationSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(" s");
            if (plan.AbortConditions.Count > 0) body.Append("\nAbort if: ").Append(string.Join("; ", plan.AbortConditions.ToArray()));
            // The checks the run will watch, as Approval shows them: Claudia's, then the config defaults filling the gaps.
            if (!plan.Manual)
                body.Append("\nTelemetry Checks: ").Append(string.Join("; ", TelemetryDefaults.Fill(plan, config)
                    .Select(c => c.Describe() + (c.Default ? " (default)" : "")).ToArray()));
            if (plan.Risks.Count > 0) body.Append("\nRisks: ").Append(string.Join("; ", plan.Risks.ToArray()));
            return body.ToString();
        }

        public const string LostTitle = "Final Debrief: Lost";
        public const string LostWhy = "Claudia was killed aboard the craft.";

        /// <summary>The final Debrief from Mission Control for a Lost Mission; <paramref name="doing"/> is what it was in the middle of, or "".</summary>
        public static string LostBody(string goal, string doing, decimal spent, decimal cap, bool spentUnknown = false)
        {
            return "Claudia Kermin was killed aboard the craft" + (doing.Length > 0 ? " " + doing : "") + ".\n"
                + "Mission Goal not met: " + goal + "\n"
                + (spentUnknown ? "Spent an unknown amount (the cost log can't be read) of the $" : "Spent $" + spent.ToString("0.00", CultureInfo.InvariantCulture) + " of the $") + cap.ToString("0.00", CultureInfo.InvariantCulture)
                + " Spending Cap. The Mission is Lost.";
        }

        public static string DebriefBody(Debrief debrief, string crossings)
        {
            return debrief.WhatHappened + (debrief.Next.Length > 0 ? "\nNext: " + debrief.Next : "")
                + (crossings.Length > 0 ? "\nTelemetry Checks crossed: " + crossings : "");
        }

        /// <summary>
        /// The part of a Debrief card's detail that is Claudia's (rating, outcome against target, margin, science, checklist, follow-up),
        /// so a card rebuilt from the transcript keeps it; what the mod saw during the run is added by the loop.
        /// </summary>
        public static DebriefDetail DebriefDetail(Debrief debrief)
        {
            return new DebriefDetail
            {
                Rating = debrief.Outcome,
                OutcomeVsTarget = debrief.OutcomeVsTarget,
                PlanMargin = debrief.PlanMargin,
                Science = debrief.Science,
                FollowUp = FollowUpOffer(debrief),
                Checklist = debrief.Checklist.ToList(),
            };
        }

        /// <summary>The next step as an offer, in the mod's words: a corrective sequence or revised plan, a reduced goal, or the end.</summary>
        private static string FollowUpOffer(Debrief debrief)
        {
            if (debrief.GoalReached) return "Mission Goal met.";
            var text = debrief.FollowUpText.Length > 0 ? debrief.FollowUpText : debrief.Next;
            switch (debrief.Outcome)
            {
                case "Failed":
                    return debrief.FollowUp == FollowUps.ReducedGoal && debrief.FollowUpText.Length > 0
                        ? "The Mission Goal can't be reached as stated. Reduced goal, for your Approval: " + debrief.FollowUpText + " Approve it, or End Mission."
                        : "The Mission Goal can't be reached as stated, and no reduced goal is on offer: the Mission ends.";
                case "Off-Nominal":
                    return debrief.FollowUp == FollowUps.RevisedPlan
                        ? "Revised Mission Plan next" + (text.Length > 0 ? ": " + text : ".")
                        : "Corrective sequence next" + (text.Length > 0 ? ": " + text : ".");
                default:
                    return text.Length > 0 ? "Next: " + text : "";
            }
        }
    }
}
