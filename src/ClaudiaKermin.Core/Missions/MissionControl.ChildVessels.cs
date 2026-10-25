// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Child Vessels (spec stories 112 to 115). The Mission Plan names them (by the separation stage that releases them, or
    /// by the id of a vessel already separated) and each Sequence Plan names the vessel it flies. A script goes to a Child
    /// Vessel only once it has separated and has a CommNet path from Claudia, and only while every other vessel of the
    /// Mission is stable; once started it keeps running whatever the link does. There is still one run per Mission: the
    /// sequence that releases a Child Vessel ends (and is Debriefed) before the Child Vessel's own starts. Separated
    /// vessels the plan doesn't name are ignored. When the Mission ends its Child Vessels are forgotten: ordinary vessels.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            public List<ChildVessel> Children = new List<ChildVessel>();
            /// <summary>The Child Vessel the current run flies; "" for the craft.</summary>
            public string RunningChild = "";
        }

        /// <summary>The vessels in Flight as last seen; empty outside Flight.</summary>
        private IList<SeenVessel> _seen = new List<SeenVessel>();

        /// <summary>A Sequence Plan's vessel is the craft: "", "craft" or the craft's own name.</summary>
        private static bool FliesCraft(Mission mission, string vessel)
        {
            return MissionCards.FliesCraft(mission.VesselName, vessel);
        }

        /// <summary>The Mission Plan's Child Vessel by this name, or null.</summary>
        private static ChildVessel PlannedChild(Mission mission, string vessel)
        {
            vessel = (vessel ?? "").Trim();
            return mission.Children.FirstOrDefault(c => string.Equals(c.Name, vessel, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The separations facts for Claudia's first Mission Plan, so she can name Child Vessels by stage.</summary>
        private string ChildVesselFacts(Mission mission)
        {
            if (_snapshot == null) return "";
            var staging = ChildVessels.Staging(_snapshot, mission.Remote, mission.ProcessorPartId);
            var candidates = staging.Separations.Where(s => s.Candidate).ToList();
            if (candidates.Count == 0) return "";
            var json = new JsonWriter().BeginArray();
            foreach (var s in candidates)
                json.BeginObject().Field("stage", s.Stage).Field("name", s.Name).Field("kos", s.ProcessorPartIds.Count > 0)
                    .Field("antenna_power", s.AntennaPower).Field("dv_vac", Math.Round(s.DeltaVVac)).EndObject();
            return "\nSeparations that could be Child Vessels (JSON data; name the ones you plan to fly in child_vessels): " + json.EndArray();
        }

        // ---- The plan names them ----

        /// <summary>
        /// A Mission Plan was offered: its Child Vessels replace the Mission's. One named by stage is known by the kOS
        /// processors the staging plan releases there; one named by vessel id, by that vessel. What is known of a Child
        /// Vessel the plan names again is kept.
        /// </summary>
        private void PlanChildVessels(Mission mission, MissionPlan plan)
        {
            var staging = _snapshot != null ? ChildVessels.Staging(_snapshot, mission.Remote, mission.ProcessorPartId) : new StagingPlan();
            var children = new List<ChildVessel>();
            foreach (var planned in plan.ChildVessels)
            {
                var child = mission.Children.FirstOrDefault(c => string.Equals(c.Name, planned.Name, StringComparison.OrdinalIgnoreCase))
                    ?? new ChildVessel { Name = planned.Name };
                if (planned.VesselId.Length > 0 && child.VesselId != planned.VesselId)
                {
                    child.VesselId = planned.VesselId;
                    var seen = _seen.FirstOrDefault(v => v.Id == planned.VesselId);
                    if (seen != null) child.ProcessorPartIds = seen.ProcessorPartIds.ToList();
                }
                else if (planned.SeparationStage.HasValue && child.ProcessorPartIds.Count == 0)
                    child.ProcessorPartIds = staging.CandidatesAt(planned.SeparationStage.Value).SelectMany(s => s.ProcessorPartIds).ToList();
                // Known neither way, it could never be flown: the player hears so now, not at Approval.
                if (child.ProcessorPartIds.Count == 0 && child.VesselId.Length == 0)
                    Note(LogKind.Notice, "Child Vessel " + child.Name + ": nothing with a kOS processor separates "
                        + (planned.SeparationStage.HasValue ? "at stage " + planned.SeparationStage.Value.ToString(CultureInfo.InvariantCulture) : "as the plan says")
                        + ", so the mod can't recognise it to fly it. Change the plan if Claudia is to fly it.");
                children.Add(child);
            }
            mission.Children = children;
            UpdateChildren(mission);
        }

        // ---- What the mod sees of them ----

        /// <summary>The vessels in Flight changed: each Child Vessel is found (a vessel other than the craft carrying its processor) or not.</summary>
        private void ChildVesselsSeen(IList<SeenVessel> vessels)
        {
            _seen = vessels ?? new List<SeenVessel>();
            var mission = _mission;
            if (mission != null && !MissionOver) UpdateChildren(mission);
        }

        private void UpdateChildren(Mission mission)
        {
            foreach (var child in mission.Children)
            {
                var seen = _seen.FirstOrDefault(v => !MissionCraft.Is(mission.Remote, mission.ProcessorPartId, mission.VesselId, v)
                    && (v.Id == child.VesselId || v.ProcessorPartIds.Any(child.ProcessorPartIds.Contains)));
                if (seen == null)
                {
                    // Not seen apart from the craft: still aboard it (or out of Flight, when nothing is known).
                    if (_seen.Count > 0) child.Separated = false;
                    child.Linked = false;
                    continue;
                }
                child.Separated = true;
                child.VesselId = seen.Id;
                child.VesselName = seen.Name;
                if (seen.ProcessorPartIds.Count > 0) child.ProcessorPartIds = seen.ProcessorPartIds.ToList();
                child.Linked = ChildVessels.LinkedToClaudia(mission.Remote, mission.VesselId, seen);
                child.Situation = seen.Situation;
            }
        }

        /// <summary>
        /// Something left the craft (a separation or undocking). A Child Vessel separating, an undocking, or any vessel with a
        /// kOS processor gets a notice in the log and to Claudia with her next turn, saying what left; debris gets none.
        /// </summary>
        private void Separated(VesselsSeparated separated)
        {
            var mission = _mission;
            if (mission == null || !FollowsCraft(mission)) return;
            var left = MissionCraft.Added(mission.Remote, mission.ProcessorPartId, mission.VesselId, separated.First, separated.Second);
            if (left == null) return;
            var child = mission.Children.FirstOrDefault(c => c.VesselId == left.Id || left.ProcessorPartIds.Any(c.ProcessorPartIds.Contains));
            if (child == null && !separated.Undocked && !left.HasKosProcessor) return;
            if (child != null)
            {
                child.Separated = true;
                child.VesselId = left.Id;
                child.VesselName = left.Name;
            }
            var name = child != null ? child.Name : null;
            Note(LogKind.Notice, ChildVessels.LeftLine(left, separated.Undocked, name));
            Event(mission, separated.Undocked ? "undocked" : "separated", left.Name);
            mission.LoadNotice = (mission.LoadNotice != null ? mission.LoadNotice + "\n\n" : "") + ChildVessels.LeftNotice(left, separated.Undocked, name);
        }

        // ---- Flying one ----

        /// <summary>
        /// The kOS processor a Sequence Plan's script goes to, or null with the reason in the log. The craft's is the Craft
        /// Check's choice; a Child Vessel's is its own, once it has separated and has a CommNet path from Claudia. Either way
        /// every other vessel of the Mission left behind must be stable.
        /// </summary>
        private uint? SequenceProcessor(Mission mission, SequencePlan plan)
        {
            var child = PlannedChild(mission, plan.Vessel);
            string refusal;
            uint? processor = null;
            if (FliesCraft(mission, plan.Vessel))
            {
                child = null;
                processor = _craftCheck.ProcessorFor(_snapshot);
                refusal = processor.HasValue ? LeftBehindUnstable(mission, null) : "the craft has no kOS processor";
            }
            else if (child == null)
                refusal = MissionPrompts.Quoted(plan.Vessel) + " isn't a Child Vessel in the Mission Plan, and separated vessels the plan doesn't name are "
                    + "ignored. Ask Claudia to revise the Mission Plan to use it, or Change the plan";
            else if (!child.Separated) refusal = child.Name + " hasn't separated from the craft yet";
            else if (!child.Linked)
                refusal = "there is no CommNet path from Claudia to " + child.Name + (mission.Remote ? " (from Kerbin)" : " (from the craft)")
                    + ", and its script can only be sent over one. Restore the link, then Approve";
            else if (child.ProcessorPartIds.Count == 0) refusal = child.Name + " has no kOS processor";
            else
            {
                processor = child.ProcessorPartIds[0];
                refusal = LeftBehindUnstable(mission, child);
            }
            if (refusal.Length == 0) return processor;
            Note(LogKind.Notice, "Can't run the script: " + refusal + ".");
            return null;
        }

        /// <summary>Why a vessel of the Mission that the run would leave behind isn't stable, or "".</summary>
        private string LeftBehindUnstable(Mission mission, ChildVessel flown)
        {
            if (flown != null && _snapshot != null && !ChildVessels.Stable(_snapshot.Situation))
                return Unstable("the craft", _snapshot.Situation);
            var unstable = mission.Children.FirstOrDefault(c => c != flown && c.Separated && !ChildVessels.Stable(c.Situation));
            return unstable == null ? "" : Unstable(unstable.Name, unstable.Situation);
        }

        private static string Unstable(string vessel, string situation)
        {
            return vessel + " would be left behind " + situation + "; it must be stable (in orbit, landed or splashed down) first";
        }

        /// <summary>A run started on the vessel the plan names: a Child Vessel's run has no Telemetry Checks, which read the craft.</summary>
        private void StartedOn(Mission mission, SequencePlan plan)
        {
            var child = FliesCraft(mission, plan.Vessel) ? null : PlannedChild(mission, plan.Vessel);
            mission.RunningChild = child != null ? child.Name : "";
            if (child == null) return;
            mission.Checks.Clear();
            Note(LogKind.Notice, "The script is going to " + child.Name + " over its CommNet path; once started it carries on even if the link drops. "
                + "Telemetry Checks watch only the craft, so this run has none.");
            Event(mission, "child_run", child.Name);
        }

        /// <summary>For the Debrief of a Child Vessel's run: the Game Snapshot is still the craft's.</summary>
        private static string ChildRunNote(Mission mission)
        {
            return mission.RunningChild.Length == 0 ? "" : "This sequence flew the Child Vessel " + MissionPrompts.Quoted(mission.RunningChild)
                + "; judge it from its Script Result. The Game Snapshot below is the craft's.\n";
        }

        /// <summary>The running Child Vessel's id, or "" when the run flies the craft (or nothing runs).</summary>
        private string RunningChildVesselId(Mission mission)
        {
            if (!IsRunning(mission) || mission.RunningChild.Length == 0) return "";
            var child = mission.Children.FirstOrDefault(c => c.Name == mission.RunningChild);
            return child != null ? child.VesselId : "";
        }

        /// <summary>The player is in the Child Vessel a sequence is running on: Claudia has its controls.</summary>
        private bool ChildRunActive
        {
            get
            {
                var mission = _mission;
                if (mission == null || MissionOver || _active == null || _scene != GameScene.Flight) return false;
                var id = RunningChildVesselId(mission);
                return id.Length > 0 && _active.Craft.VesselId == id;
            }
        }

        // ---- The end of the Mission ----

        /// <summary>The Mission is over: its Child Vessels are ordinary vessels again.</summary>
        private static void ReleaseChildVessels(Mission mission)
        {
            mission.Children.Clear();
            mission.RunningChild = "";
        }

        private IList<ChildVesselView> ChildVesselViews(Mission mission)
        {
            if (MissionOver) return new List<ChildVesselView>();
            return mission.Children.Select(c => new ChildVesselView
            {
                Name = c.Name, Separated = c.Separated, Linked = c.Linked, Running = IsRunning(mission) && mission.RunningChild == c.Name,
            }).ToList();
        }

        // ---- Persistence ----

        private static string ChildVesselsJson(Mission mission)
        {
            if (mission.Children.Count == 0) return "";
            var json = new JsonWriter().BeginArray();
            foreach (var c in mission.Children)
            {
                json.BeginObject().Field("name", c.Name).Field("vessel_id", c.VesselId).Field("vessel_name", c.VesselName)
                    .Field("separated", c.Separated).Name("processors").BeginArray();
                foreach (var id in c.ProcessorPartIds) json.Number(id);
                json.EndArray().EndObject();
            }
            return json.EndArray().ToString();
        }

        private static void RestoreChildVessels(Mission mission, string text)
        {
            mission.Children = new List<ChildVessel>();
            if (string.IsNullOrEmpty(text)) return;
            JsonNode root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonException)
            {
                return;
            }
            foreach (var node in root.Items.Where(n => n.Kind == JsonKind.Object))
            {
                var processors = node["processors"];
                mission.Children.Add(new ChildVessel
                {
                    Name = JsonNode.Str(node["name"]) ?? "",
                    VesselId = JsonNode.Str(node["vessel_id"]) ?? "",
                    VesselName = JsonNode.Str(node["vessel_name"]) ?? "",
                    Separated = JsonNode.Bool(node["separated"]) ?? false,
                    ProcessorPartIds = processors == null ? new List<uint>()
                        : processors.Items.Select(JsonNode.Long).Where(l => l.HasValue).Select(l => (uint)l.Value).ToList(),
                });
            }
        }
    }
}
