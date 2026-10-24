// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Roster;

namespace ClaudiaKermin.Adapter.Roster
{
    /// <summary>The Prepare to uninstall button: applies the core's UninstallPlanner to the current save.</summary>
    public static class UninstallPrep
    {
        public static bool SaveIsLoaded
        {
            get { return HighLogic.CurrentGame != null && HighLogic.CurrentGame.CrewRoster != null; }
        }

        /// <summary>Returns a line for the player describing what happened.</summary>
        public static string Run()
        {
            if (!SaveIsLoaded) return "Load a save first: this only changes the save you are in.";

            var roster = HighLogic.CurrentGame.CrewRoster;
            var actions = UninstallPlanner.Plan(RosterGateway.Snapshot(roster));
            if (actions.Count == 0) return "Nothing to do: nobody in this save has the Super Intelligence role.";

            foreach (var action in actions) RosterGateway.Apply(roster, action, null);
            UnityEngine.Debug.Log(ModInfo.LogTag + " prepared to uninstall: " + actions.Count + " Kerbal(s) set to " + UninstallPlanner.ReplacementTrait);
            return "Claudia is now a " + UninstallPlanner.ReplacementTrait + " in this save. Save the game, then remove the mod.";
        }
    }
}
