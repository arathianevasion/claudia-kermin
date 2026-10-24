// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Puts each Mission's save-state in the save file. It only turns the core's <see cref="MissionSaveState"/>
    /// into ConfigNode values on save; the driver reads them back when a game is loaded and hands them to the
    /// core (<see cref="MissionsRestored"/>), which decides what the loop does. Loading here would also run on
    /// every scene change, which is not a load.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR, GameScenes.FLIGHT)]
    public class MissionScenario : ScenarioModule
    {
        public const string MissionNode = "MISSION";

        public override void OnSave(ConfigNode node)
        {
            var driver = MissionDriver.Current;
            if (driver == null) return;
            foreach (var state in driver.ExportStates())
            {
                var mission = node.AddNode(MissionNode);
                foreach (var pair in state.ToValues()) mission.AddValue(pair.Key, pair.Value);
            }
        }

        /// <summary>The Mission snapshots in a scenario module's node, as saved.</summary>
        public static List<MissionSaveState> Read(ConfigNode scenario)
        {
            var states = new List<MissionSaveState>();
            if (scenario == null) return states;
            foreach (var mission in scenario.GetNodes(MissionNode))
            {
                var values = new Dictionary<string, string>();
                foreach (ConfigNode.Value value in mission.values) values[value.name] = value.value;
                var state = MissionSaveState.FromValues(values);
                if (state != null) states.Add(state);
            }
            return states;
        }
    }
}
