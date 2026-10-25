// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Child Vessels (spec stories 56 and 112 to 115) as plain rules. <see cref="Staging"/> works out, from the part tree
    /// in a Game Snapshot, what leaves the craft at each separation stage and that vessel's own figures, so the Craft
    /// Check can judge a planned Child Vessel itself (#100) rather than rely on Claude to notice.
    /// </summary>
    public static class ChildVessels
    {
        /// <summary>Standard gravity, for Isp to exhaust velocity.</summary>
        private const double G0 = 9.80665;

        /// <summary>
        /// Every separation in the staging plan, first to fire first. The craft is the side Claudia stays with: aboard, the
        /// crew part nearest the root (where she sits); remote, her kOS processor (<paramref name="processorPartId"/>). A
        /// decoupler takes its own side of the tree away unless the craft is on that side, when the rest goes instead.
        /// </summary>
        public static StagingPlan Staging(GameSnapshot snapshot, bool remote, uint? processorPartId)
        {
            var plan = new StagingPlan();
            var parts = snapshot.Craft.Parts;
            if (parts.Count == 0) return plan;
            var byId = new Dictionary<uint, SnapshotPart>();
            foreach (var part in parts) byId[part.Id] = part;
            var anchor = Anchor(parts, byId, remote, processorPartId);
            var attached = new HashSet<uint>(parts.Select(p => p.Id));

            // Higher stages fire first; decouplers in the same stage go in part order.
            var decouplers = parts.Where(p => p.Tags.Contains("decoupler")).Select((p, i) => new { Part = p, Order = i })
                .OrderByDescending(d => d.Part.Stage).ThenBy(d => d.Order).Select(d => d.Part).ToList();
            foreach (var decoupler in decouplers)
            {
                if (!attached.Contains(decoupler.Id)) continue;
                var side = Subtree(parts, decoupler.Id, attached);
                var leaving = side.Contains(anchor.Id) ? parts.Where(p => attached.Contains(p.Id) && !side.Contains(p.Id)).Select(p => p.Id).ToList() : side;
                if (leaving.Count == 0) continue;
                var hadProcessor = attached.Any(id => byId[id].HasKosProcessor);
                foreach (var id in leaving) attached.Remove(id);
                var separation = Describe(decoupler.Stage, leaving.Select(id => byId[id]).ToList());
                plan.Separations.Add(separation);
                if (!remote && plan.Strands.Length == 0 && hadProcessor && !attached.Any(id => byId[id].HasKosProcessor))
                    plan.Strands = "stage " + decoupler.Stage + " separates " + separation.Name + " with Claudia's kOS processor, leaving "
                        + anchor.Title + " (where she sits) without one";
            }
            return plan;
        }

        /// <summary>The part the craft keeps: aboard, the crew part nearest the root; remote (or with no crew part), the processor; else the root.</summary>
        private static SnapshotPart Anchor(IList<SnapshotPart> parts, Dictionary<uint, SnapshotPart> byId, bool remote, uint? processorPartId)
        {
            if (!remote)
            {
                var seat = parts.Where(p => p.CrewCapacity > 0).OrderBy(p => Depth(p, byId)).FirstOrDefault();
                if (seat != null) return seat;
            }
            SnapshotPart processor;
            if (processorPartId.HasValue && byId.TryGetValue(processorPartId.Value, out processor)) return processor;
            return parts.FirstOrDefault(p => !p.ParentId.HasValue || !byId.ContainsKey(p.ParentId.Value)) ?? parts[0];
        }

        private static int Depth(SnapshotPart part, Dictionary<uint, SnapshotPart> byId)
        {
            var depth = 0;
            SnapshotPart parent;
            while (part.ParentId.HasValue && byId.TryGetValue(part.ParentId.Value, out parent) && depth < byId.Count)
            {
                part = parent;
                depth++;
            }
            return depth;
        }

        /// <summary><paramref name="root"/> and the attached parts below it, in part order.</summary>
        private static List<uint> Subtree(IList<SnapshotPart> parts, uint root, HashSet<uint> attached)
        {
            var inside = new HashSet<uint> { root };
            bool grew;
            do
            {
                grew = false;
                foreach (var part in parts)
                    if (attached.Contains(part.Id) && !inside.Contains(part.Id) && part.ParentId.HasValue && inside.Contains(part.ParentId.Value))
                        grew = inside.Add(part.Id) || grew;
            } while (grew);
            return parts.Where(p => inside.Contains(p.Id)).Select(p => p.Id).ToList();
        }

        /// <summary>
        /// A separated vessel's figures. Delta-v is one burn of all its engines on all the propellant it carries (its wet
        /// mass less its dry mass), at their combined vacuum Isp: rough, but enough to see a probe that can't manoeuvre.
        /// </summary>
        private static Separation Describe(int stage, List<SnapshotPart> parts)
        {
            var separation = new Separation
            {
                Stage = stage,
                PartIds = parts.Select(p => p.Id).ToList(),
                ProcessorPartIds = parts.Where(p => p.HasKosProcessor).Select(p => p.Id).ToList(),
                HasCommand = parts.Any(p => p.Tags.Contains("command")),
                AntennaPower = parts.Count == 0 ? 0 : parts.Max(p => p.AntennaPower),
                ChargeMax = parts.Sum(p => p.ChargeMax),
                Generates = parts.Any(p => p.Tags.Contains("electric_generator")),
                MassT = parts.Sum(p => p.MassT),
                ThrustVacKn = parts.Sum(p => p.ThrustVacKn),
                ThrustAslKn = parts.Sum(p => p.ThrustAslKn),
            };
            var named = parts.FirstOrDefault(p => p.Tags.Contains("command")) ?? parts.FirstOrDefault(p => !p.Tags.Contains("decoupler")) ?? parts[0];
            separation.Name = named.Title.Length > 0 ? named.Title : named.Name;
            var flow = parts.Where(p => p.ThrustVacKn > 0 && p.IspVac > 0).Sum(p => p.ThrustVacKn / p.IspVac);
            var dry = parts.Sum(p => p.DryMassT > 0 ? p.DryMassT : p.MassT);
            if (flow > 0 && dry > 0 && separation.MassT > dry)
                separation.DeltaVVac = separation.ThrustVacKn / flow * G0 * Math.Log(separation.MassT / dry);
            return separation;
        }
    }

    /// <summary>What the staging plan separates from the craft, and whether it strands Claudia.</summary>
    public sealed class StagingPlan
    {
        /// <summary>In firing order.</summary>
        public readonly List<Separation> Separations = new List<Separation>();
        /// <summary>How the staging plan leaves an aboard Claudia with no kOS processor, or "" when it doesn't.</summary>
        public string Strands = "";

        /// <summary>The vessels at <paramref name="stage"/> that could be Child Vessels.</summary>
        public IEnumerable<Separation> CandidatesAt(int stage)
        {
            return Separations.Where(s => s.Stage == stage && s.Candidate);
        }
    }

    /// <summary>One vessel that leaves the craft at a separation stage, with its own figures.</summary>
    public sealed class Separation
    {
        /// <summary>The decoupler's stage.</summary>
        public int Stage;
        /// <summary>Its command part's title (or its first part's).</summary>
        public string Name = "";
        public List<uint> PartIds = new List<uint>();
        public List<uint> ProcessorPartIds = new List<uint>();
        public bool HasCommand;
        /// <summary>Its strongest antenna's power; 0 for none.</summary>
        public double AntennaPower;
        public double ChargeMax;
        public bool Generates;
        public double MassT;
        public double DeltaVVac;
        public double ThrustVacKn;
        public double ThrustAslKn;

        /// <summary>It has something to fly it by (a kOS processor or a command part), so it could be a Child Vessel; the rest is debris.</summary>
        public bool Candidate
        {
            get { return ProcessorPartIds.Count > 0 || HasCommand; }
        }

        /// <summary>What stops Claudia flying it, whatever the Mission Goal: the Blockers the mod judges itself.</summary>
        public IList<string> Problems()
        {
            var problems = new List<string>();
            if (ProcessorPartIds.Count == 0) problems.Add("no kOS processor");
            if (AntennaPower <= 0) problems.Add("no antenna to reach Claudia");
            if (ChargeMax <= 0 && !Generates) problems.Add("no electric charge (no battery and nothing generating)");
            return problems;
        }
    }
}
