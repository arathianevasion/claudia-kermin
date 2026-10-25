// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    public enum MarginLevel
    {
        /// <summary>The craft has the rest of the plan's delta-v with the wanted margin to spare.</summary>
        Enough,
        /// <summary>It has the rest of the plan's delta-v, but less than the wanted margin.</summary>
        Tight,
        /// <summary>It does not have the rest of the plan's delta-v.</summary>
        Short,
    }

    /// <summary>
    /// The Debrief's margin bar: the delta-v the craft has left against what the rest of the Mission Plan needs. The mod
    /// works it out from the Game Snapshot and the Mission Plan, so it does not depend on what Claudia says.
    /// </summary>
    public sealed class MarginBar
    {
        /// <summary>The craft's vacuum delta-v now, m/s.</summary>
        public double DeltaVLeft;
        /// <summary>What the Maneuver Sequences still to fly budget, m/s.</summary>
        public double RestOfPlan;
        /// <summary>The margin the Craft Check wants over a budget, percent.</summary>
        public int WantedPercent;
        public MarginLevel Level;

        /// <summary>The delta-v with the wanted margin on top, m/s.</summary>
        public double Needed
        {
            get { return RestOfPlan * (1 + WantedPercent / 100.0); }
        }

        /// <summary>The margin the craft really has over the rest of the plan, percent; null when nothing is left to fly.</summary>
        public double? ActualPercent
        {
            get { return RestOfPlan > 0 ? (double?)(100 * (DeltaVLeft / RestOfPlan - 1)) : null; }
        }

        /// <summary>The bar's fill, 0 to 1: delta-v left over the delta-v needed with the margin (full once there is enough).</summary>
        public double Fraction
        {
            get { return Needed <= 0 ? 1 : Math.Max(0, Math.Min(1, DeltaVLeft / Needed)); }
        }

        /// <summary>The bar, or null when no figure is known: the stock simulator gave none, or the Mission Plan has nothing left to fly.</summary>
        public static MarginBar Compute(double? deltaVLeft, double restOfPlan, int wantedPercent)
        {
            if (!deltaVLeft.HasValue || restOfPlan <= 0) return null;
            var bar = new MarginBar { DeltaVLeft = deltaVLeft.Value, RestOfPlan = restOfPlan, WantedPercent = wantedPercent };
            bar.Level = deltaVLeft.Value >= bar.Needed ? MarginLevel.Enough : deltaVLeft.Value >= restOfPlan ? MarginLevel.Tight : MarginLevel.Short;
            return bar;
        }

        /// <summary>"1,240 m/s left against 900 m/s still to fly (+38%; 10% wanted): enough."</summary>
        public string Describe()
        {
            var actual = ActualPercent;
            return Metres(DeltaVLeft) + " left against " + Metres(RestOfPlan) + " still to fly ("
                + (actual.HasValue ? (actual.Value >= 0 ? "+" : "") + actual.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "n/a")
                + "; " + WantedPercent.ToString(CultureInfo.InvariantCulture) + "% wanted): "
                + (Level == MarginLevel.Enough ? "enough" : Level == MarginLevel.Tight ? "tight" : "short") + ".";
        }

        private static string Metres(double value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s";
        }
    }

    /// <summary>Approve the reduced Mission Goal a Failed Debrief offered; Claudia then revises the Mission Plan for it, which needs Approval in turn.</summary>
    public sealed class AcceptReducedGoal : MissionInput
    {
    }

    /// <summary>One step of the Mission Goal checklist.</summary>
    public sealed class GoalItem
    {
        public string Item = "";
        public bool Done;
    }

    /// <summary>
    /// What a Debrief card shows beyond Claudia's account: the rating, the outcome against the target, the margin recheck,
    /// science, the mod's events, the Mission Goal checklist, the follow-up on offer and, for a failed run, the script's
    /// error text. Hung on the Debrief's <see cref="LogEntry"/> for the log drawer.
    /// </summary>
    public sealed class DebriefDetail
    {
        /// <summary>Nominal, Off-Nominal or Failed.</summary>
        public string Rating = "";
        /// <summary>Claudia's read of the outcome against the Sequence Plan's target, from the script's report and what the mod saw.</summary>
        public string OutcomeVsTarget = "";
        /// <summary>The delta-v left against the rest of the plan; null when it isn't known.</summary>
        public MarginBar Margin;
        /// <summary>Resources left, one per line ("ElectricCharge 64%").</summary>
        public IList<string> ResourcesLeft = new List<string>();
        /// <summary>The RCS switch as the craft was left ("RCS on" or "RCS off"); "" when it isn't known. Monopropellant shows in <see cref="ResourcesLeft"/>.</summary>
        public string RcsState = "";
        /// <summary>Claudia's reading of what is left against the rest of the plan.</summary>
        public string PlanMargin = "";
        /// <summary>The margin recheck (a Craft Check on the craft as it is now): its summary, and one line per finding; empty when it didn't run.</summary>
        public string RecheckSummary = "";
        public IList<string> RecheckFindings = new List<string>();
        /// <summary>Why the margin recheck didn't run ("" when it did).</summary>
        public string RecheckSkipped = "";
        /// <summary>Claudia's account of the science, and the lines the mod saw collected.</summary>
        public string Science = "";
        public IList<string> ScienceSeen = new List<string>();
        /// <summary>What the mod logged during the run other than science.</summary>
        public IList<string> Events = new List<string>();
        public IList<GoalItem> Checklist = new List<GoalItem>();
        /// <summary>What happens next, as an offer: a corrective sequence, a revised Mission Plan, a reduced goal for Approval or ending the Mission.</summary>
        public string FollowUp = "";
        /// <summary>The script's error text (the kOS terminal after a failed run); "" when it reported.</summary>
        public string ScriptError = "";

        public bool HasChecklist
        {
            get { return Checklist.Count > 0; }
        }

        public int ChecklistDone
        {
            get { return Checklist.Count(i => i.Done); }
        }
    }
}
