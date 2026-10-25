// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Which Building Blocks' full docs go in the per-Mission layer. Every block keeps its Summary in the base
    /// layer, so a block left out here can still be named and its options asked for; this only decides which
    /// ones are spelled out in full. A pure decision from the Mission Goal: a goal that points at nothing
    /// in particular gets every block, and a block this table doesn't know is always included.
    /// </summary>
    internal static class BlockSelection
    {
        /// <summary>Blocks almost every flight uses, whatever the goal says.</summary>
        private static readonly string[] Always = { "coast", "exec_node", "circularize" };

        /// <summary>Each block's trigger words, matched as lower-case substrings of the Mission Goal.</summary>
        private static readonly KeyValuePair<string, string[]>[] Triggers =
        {
            Rule("launch", "launch", "lift off", "liftoff", "take off", "takeoff", "orbit", "space", "apoapsis", "periapsis", "kerbin"),
            Rule("transfer_to", "mun", "minmus", "moon", "transfer", "encounter", "flyby", "fly by", "fly-by", "intercept"),
            Rule("land", "land", "touch down", "touchdown", "set down", "surface"),
            Rule("ascend", "ascend", "leave the", "lift off from", "take off from", "launch from", "back to orbit", "return", "come back", "home"),
            Rule("return", "return", "come back", "home", "back to kerbin", "bring", "recover"),
            Rule("reentry", "return", "come back", "home", "reentry", "re-entry", "splash", "parachute", "bring", "recover", "land on kerbin", "back to kerbin"),
            Rule("science", "science", "experiment", "survey", "biome", "sample", "data", "report", "goo", "thermometer"),
        };

        private static KeyValuePair<string, string[]> Rule(string block, params string[] words)
        {
            return new KeyValuePair<string, string[]>(block, words);
        }

        /// <summary>The names of the blocks whose full docs this Mission gets: the always-set, each block the goal's words point at, and any block the table doesn't know.</summary>
        public static IList<string> For(string goal, IEnumerable<string> allBlocks)
        {
            var all = allBlocks.ToList();
            var text = (goal ?? "").ToLowerInvariant();
            var picked = new HashSet<string>(Triggers.Where(t => t.Value.Any(text.Contains)).Select(t => t.Key));
            if (picked.Count == 0) return all;
            picked.UnionWith(Always);
            var known = new HashSet<string>(Always.Concat(Triggers.Select(t => t.Key)));
            return all.Where(b => picked.Contains(b) || !known.Contains(b)).ToList();
        }

        /// <summary>The names of the shipped blocks this Mission gets full docs for.</summary>
        public static IList<string> For(string goal)
        {
            return For(goal, CoreLibrary.Blocks.Select(b => b.Name));
        }
    }
}
