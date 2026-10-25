// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Which vessel is the Mission's craft (spec stories 116 to 120), as plain rules over what the adapter saw. The
    /// craft follows Claudia on an aboard Mission and the original kOS processor on a remote one, so staging, docking
    /// and undocking never leave the Mission without an answer. The mod never switches vessels itself: these rules
    /// only say which vessel the player is in and what to do when the craft would unload.
    /// </summary>
    public static class MissionCraft
    {
        /// <summary>The warning comes this many game seconds before the craft would unload.</summary>
        public const double UnloadWarningSeconds = 10;

        /// <summary>
        /// The vessel in <paramref name="snapshot"/> is the Mission's craft: Claudia is aboard it (aboard Mission), or it
        /// carries the original processor (remote). A remote Mission with no processor on record goes by vessel id.
        /// </summary>
        public static bool Is(bool remote, uint? processorPartId, string vesselId, GameSnapshot snapshot)
        {
            if (snapshot == null) return false;
            if (!remote) return snapshot.ClaudiaAboard;
            if (processorPartId.HasValue) return snapshot.Craft.Parts.Any(p => p.Id == processorPartId.Value);
            return string.IsNullOrEmpty(vesselId) || snapshot.Craft.VesselId == vesselId;
        }

        /// <summary>
        /// The same rule over a vessel the adapter saw (loaded or not). Unlike a snapshot, with no vessel id on record this
        /// matches nothing: the vessels seen are all of them, and every one would match.
        /// </summary>
        public static bool Is(bool remote, uint? processorPartId, string vesselId, SeenVessel vessel)
        {
            if (vessel == null) return false;
            if (!remote) return vessel.ClaudiaAboard;
            if (processorPartId.HasValue) return vessel.ProcessorPartIds.Contains(processorPartId.Value);
            return !string.IsNullOrEmpty(vesselId) && vessel.Id == vesselId;
        }

        /// <summary>The Mission's craft among <paramref name="vessels"/>, or null when none of them is.</summary>
        public static SeenVessel Follow(bool remote, uint? processorPartId, string vesselId, IEnumerable<SeenVessel> vessels)
        {
            return vessels.FirstOrDefault(v => Is(remote, processorPartId, vesselId, v));
        }

        /// <summary>
        /// Why Claudia can fly no vessel, or "" when she can fly <paramref name="craft"/>: aboard, she needs to be in a
        /// vessel with a kOS processor (not on EVA); remote, the original processor must still be on a vessel.
        /// </summary>
        public static string CannotFly(bool remote, SeenVessel craft)
        {
            if (remote) return craft == null ? "the Mission's kOS processor is no longer on any vessel" : "";
            if (craft == null) return "Claudia isn't aboard any vessel";
            if (craft.Eva) return "Claudia is out on EVA";
            if (!craft.HasKosProcessor) return "Claudia is aboard " + craft.Name + ", which has no kOS processor";
            return "";
        }

        /// <summary>
        /// Game seconds until the craft unloads, with the player in another vessel <paramref name="distance"/> metres from it
        /// and the gap opening at <paramref name="rangeRate"/> m/s; null when it isn't opening (the craft stays). Past the
        /// unload distance already, 0.
        /// </summary>
        public static double? SecondsToUnload(double distance, double unloadDistance, double rangeRate)
        {
            if (distance >= unloadDistance) return 0;
            if (rangeRate <= 0) return null;
            return (unloadDistance - distance) / rangeRate;
        }

        /// <summary>The craft would unload within the warning time.</summary>
        public static bool UnloadsSoon(double distance, double unloadDistance, double rangeRate)
        {
            var left = SecondsToUnload(distance, unloadDistance, rangeRate);
            return left.HasValue && left.Value <= UnloadWarningSeconds;
        }

        /// <summary>
        /// Of two vessels that just docked, the one that merged into the craft (the other is the craft), or null when
        /// neither is the craft.
        /// </summary>
        public static SeenVessel Added(bool remote, uint? processorPartId, string vesselId, SeenVessel first, SeenVessel second)
        {
            if (Is(remote, processorPartId, vesselId, first)) return second;
            if (Is(remote, processorPartId, vesselId, second)) return first;
            return null;
        }

        /// <summary>The docking notice in the Mission log: what joined the craft.</summary>
        public static string DockingLine(SeenVessel added)
        {
            return "Docked with " + added.Name + ": the craft gained " + PartList(added) + ". Claudia rechecks the craft's facts before her next plan.";
        }

        /// <summary>The docking notice to Claudia, with her next turn.</summary>
        public static string DockingNotice(SeenVessel added)
        {
            return "Mod notice: the craft docked with another vessel, " + MissionPrompts.Quoted(added.Name) + ", and the two are now one craft. "
                + "Parts added (quoted data): " + MissionPrompts.Quoted(PartList(added)) + ". Recheck the craft's facts in the Game Snapshot "
                + "(mass, stages and delta-v, crew, resources, kOS processors) before the next plan; any plan made before docking is out of date.";
        }

        internal static string PartList(SeenVessel vessel)
        {
            if (vessel.PartTitles.Count == 0) return "no parts the mod could name";
            // Repeated parts are counted, in the order they first appear.
            var counted = vessel.PartTitles.GroupBy(t => t).Select(g => g.Count() > 1 ? g.Count().ToString(CultureInfo.InvariantCulture) + "x " + g.Key : g.Key);
            return vessel.PartTitles.Count.ToString(CultureInfo.InvariantCulture) + (vessel.PartTitles.Count == 1 ? " part (" : " parts (")
                + string.Join(", ", counted.ToArray()) + ")";
        }
    }

    /// <summary>A vessel in Flight as the adapter saw it, for <see cref="MissionCraft"/>.</summary>
    public sealed class SeenVessel
    {
        /// <summary>The vessel's persistent GUID, as in <see cref="SnapshotCraft.VesselId"/>.</summary>
        public string Id = "";
        public string Name = "";
        public bool ClaudiaAboard;
        /// <summary>A Kerbal on EVA.</summary>
        public bool Eva;
        public bool HasKosProcessor;
        /// <summary>The <c>persistentId</c>s of its parts with a kOS processor.</summary>
        public List<uint> ProcessorPartIds = new List<uint>();
        /// <summary>Its parts with their parents, for choosing a processor by distance from the root; empty when the adapter didn't read them.</summary>
        public List<VesselPart> Parts = new List<VesselPart>();
        /// <summary>Part titles, filled only for docking and separation (<see cref="VesselsDocked"/>, <see cref="VesselsSeparated"/>).</summary>
        public List<string> PartTitles = new List<string>();
        /// <summary>KSP's situation name, e.g. ORBITING or SUB_ORBITAL; "" when not read.</summary>
        public string Situation = "";
        /// <summary>It has a CommNet path to Kerbin (true for every vessel when the save has CommNet off).</summary>
        public bool LinkedHome;
        /// <summary>The ids of the other vessels it has a CommNet path to (every vessel when CommNet is off).</summary>
        public List<string> LinkedTo = new List<string>();
    }

    // ---- Game events ----

    /// <summary>
    /// The vessels in Flight changed (a switch, docking, undocking, separation, EVA or boarding, a vessel made or lost).
    /// The core finds the Mission's craft among them; if Claudia can fly none of them the Mission is Paused.
    /// </summary>
    public sealed class VesselsSeen : MissionInput
    {
        public readonly IList<SeenVessel> Vessels;
        public VesselsSeen(IList<SeenVessel> vessels) { Vessels = vessels; }
    }

    /// <summary>Two vessels docked into one; the core works out whether one of them is the craft and what the other added.</summary>
    public sealed class VesselsDocked : MissionInput
    {
        public readonly SeenVessel First;
        public readonly SeenVessel Second;
        public VesselsDocked(SeenVessel first, SeenVessel second) { First = first; Second = second; }
    }

    /// <summary>
    /// The player is in another vessel and the craft is still loaded: how far it is (m), the distance at which it
    /// unloads in its situation (m), and how fast the gap is opening (m/s, negative closing). Sent with each Game Snapshot.
    /// </summary>
    public sealed class CraftRange : MissionInput
    {
        public readonly double Distance;
        public readonly double UnloadDistance;
        public readonly double RangeRate;
        public CraftRange(double distance, double unloadDistance, double rangeRate)
        {
            Distance = distance;
            UnloadDistance = unloadDistance;
            RangeRate = rangeRate;
        }
    }

    /// <summary>The vessel with this id unloaded (it went out of physics range).</summary>
    public sealed class VesselUnloaded : MissionInput
    {
        public readonly string VesselId;
        public VesselUnloaded(string vesselId) { VesselId = vesselId; }
    }

    /// <summary>
    /// The player confirmed a switch far enough to unload the craft while Running (the adapter asked first, see
    /// <see cref="MissionView.LeavingFlightNeedsConfirmation"/>): the run is interrupted and Debriefed on return.
    /// </summary>
    public sealed class FarSwitchConfirmed : MissionInput
    {
        public readonly string VesselName;
        public FarSwitchConfirmed(string vesselName) { VesselName = vesselName; }
    }
}
