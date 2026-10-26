// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Missions
{
    public sealed partial class MissionControl
    {
        /// <summary>
        /// A Mission started from a cleared Craft Check carries that check's cost: it joins Spent (and Held, for a charge
        /// kept at its worst case) and opens the cost log as Craft Check entries. After that it is spend like any other.
        /// </summary>
        private void FoldCraftCheckCost(Mission mission, CraftCheckCharge cost)
        {
            if (cost == null) return;
            // A check that called tools was several requests: each joins the cost log as its own.
            foreach (var step in cost.Earlier)
            {
                LogCraftCheckCost(mission, step.Model, "craft_check_pending", step.Pending, "");
                mission.Spent += step.Settlement.Spent;
                mission.Held += step.Settlement.Held;
                LogCraftCheckCost(mission, step.Model, "craft_check_" + step.Settlement.Kind, step.Settlement.Amount, step.Settlement.Outcome ?? "tool_use");
            }
            var settlement = cost.Settlement;
            LogCraftCheckCost(mission, cost.Model, "craft_check_pending", cost.Pending, "");
            mission.Spent += settlement.Spent;
            mission.Held += settlement.Held;
            LogCraftCheckCost(mission, cost.Model, "craft_check_" + settlement.Kind, settlement.Amount, settlement.Outcome ?? "completed");
            AfterCharge(mission);
        }

        private void LogCraftCheckCost(Mission mission, string model, string kind, decimal amount, string outcome)
        {
            try
            {
                MissionFolder.AppendCostEvent(mission.Folder, kind, 0, model, amount, outcome,
                    mission.Spent, mission.Held, mission.SpendingCap);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The charge still counts in memory; only the log line is missing.
            }
        }
    }
}
