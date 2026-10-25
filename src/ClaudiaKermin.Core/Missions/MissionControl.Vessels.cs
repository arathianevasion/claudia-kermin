// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Vessel switching and docking (spec stories 116 to 120). The Mission's craft is whichever vessel
    /// <see cref="MissionCraft"/> says it is: the one Claudia is aboard, or (remote) the one with the original kOS
    /// processor. Game Snapshots of any other vessel are kept as <c>_active</c> only, so Claudia's facts, the Craft
    /// Check, Manual Control and the control locks are always about the craft. The player may fly elsewhere between
    /// sequences and nearby while Running; a run whose craft unloads (a far switch the player confirmed, or flying out
    /// of range) is interrupted and Debriefed once the player is back in the craft. Resume is refused from any other
    /// vessel. The mod never switches vessels on its own; the Go to button switches only when the player presses it.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The original kOS processor (its part's <c>persistentId</c>), which a remote Mission's craft follows; null before Flight.</summary>
            public uint? ProcessorPartId;
            /// <summary>Why Claudia can fly no vessel now, from the vessels last seen; "" when she can.</summary>
            public string CannotFlyBecause = "";
            /// <summary>Paused because Claudia could fly no vessel, so Resume is offered.</summary>
            public bool PausedCannotFly;
            /// <summary>The 10-second unload warning was given and the craft is still on its way out of range.</summary>
            public bool UnloadWarned;
            /// <summary>
            /// A far switch was confirmed and the player hasn't been seen in another vessel yet: the owed Debrief waits for them
            /// to leave and come back, not for the last snapshots of the craft before the scene reloads.
            /// </summary>
            public bool SwitchingAway;
            /// <summary>Why the running sequence was interrupted, for its Debrief even across a reload; null otherwise.</summary>
            public string InterruptReason;
            /// <summary>The craft docked since the pending plan was offered (what with), so Approve is Re-plan; "" otherwise.</summary>
            public string Recheck = "";
            /// <summary>Requests sent when the craft docked: a plan from a later request was made with the facts after docking.</summary>
            public int RecheckSince;
        }

        /// <summary>The latest Game Snapshot of the vessel the player is in, the craft or not; null outside Flight.</summary>
        private GameSnapshot _active;

        /// <summary>The player is in the Mission's craft in Flight (or there is no Mission): its latest snapshot is the craft's.</summary>
        private bool CraftActive
        {
            get { return _scene == GameScene.Flight && _active != null && ReferenceEquals(_active, _snapshot); }
        }

        /// <summary>The player is visibly in another vessel in Flight.</summary>
        private bool ElsewhereInFlight
        {
            get { return _scene == GameScene.Flight && _active != null && !ReferenceEquals(_active, _snapshot); }
        }

        /// <summary>The Mission has a craft in Flight to follow: it has left the editor and isn't over.</summary>
        private bool FollowsCraft(Mission mission)
        {
            if (MissionOver) return false;
            var stage = mission.Stage == MissionStage.Paused ? mission.StageBeforePause : mission.Stage;
            return stage != MissionStage.WaitingForFlight && stage != MissionStage.None;
        }

        /// <summary>
        /// Takes a Game Snapshot of the vessel the player is in. It is the Mission's snapshot only if that vessel is the
        /// craft; the craft's id and name follow it (docking and undocking can change both). False: another vessel.
        /// </summary>
        private bool TakeSnapshot(GameSnapshot snapshot)
        {
            _active = snapshot;
            var mission = _mission;
            if (mission == null || !FollowsCraft(mission) || _scene != GameScene.Flight) return true;
            if (!MissionCraft.Is(mission.Remote, mission.ProcessorPartId, mission.VesselId, snapshot))
            {
                mission.SwitchingAway = false;
                return false;
            }
            if (snapshot.Craft.VesselId.Length > 0) mission.VesselId = snapshot.Craft.VesselId;
            mission.VesselName = snapshot.Craft.Name;
            return true;
        }

        /// <summary>The first snapshot in Flight makes the craft: its vessel and (for a remote Mission to follow) its processor.</summary>
        private void NoteCraft(Mission mission, GameSnapshot snapshot)
        {
            mission.VesselId = snapshot.Craft.VesselId;
            mission.VesselName = snapshot.Craft.Name;
            mission.ProcessorPartId = _craftCheck.ProcessorFor(snapshot);
        }

        // ---- Which vessel is the craft ----

        private void VesselsChanged(IList<SeenVessel> vessels)
        {
            var mission = _mission;
            if (mission == null || !FollowsCraft(mission) || _scene != GameScene.Flight || vessels == null) return;
            var craft = MissionCraft.Follow(mission.Remote, mission.ProcessorPartId, mission.VesselId, vessels);
            if (craft != null)
            {
                mission.VesselId = craft.Id;
                mission.VesselName = craft.Name;
            }
            ChildVesselsSeen(vessels);
            var why = MissionCraft.CannotFly(mission.Remote, craft);
            if (why == mission.CannotFlyBecause) return;
            mission.CannotFlyBecause = why;
            if (why.Length > 0) Event(mission, "cannot_fly", why);
            else Note(LogKind.Notice, "Claudia can fly " + mission.VesselName + " again. Resume from it when you're ready.");
        }

        /// <summary>If she can fly no vessel the Mission is Paused (not in Manual Control: the player has the craft until hand back).</summary>
        private void CheckFlyable()
        {
            var mission = _mission;
            if (mission == null || mission.CannotFlyBecause.Length == 0 || !FollowsCraft(mission) || mission.InFlight) return;
            if (mission.Stage == MissionStage.Paused || mission.Stage == MissionStage.ManualControl) return;
            PauseCannotFly(mission);
        }

        /// <summary>A request is about to go while she can fly no vessel: it waits, Paused, for Resume. True: held.</summary>
        private bool HeldForCraft(Mission mission, Turn turn)
        {
            if (mission.CannotFlyBecause.Length == 0 || turn.Purpose == Purpose.Question || turn.Purpose == Purpose.Alert) return false;
            PauseCannotFly(mission);
            return true;
        }

        private void PauseCannotFly(Mission mission)
        {
            mission.PausedCannotFly = true;
            Pause(mission, "Claudia can fly no vessel: " + mission.CannotFlyBecause + ". " + (mission.Remote
                ? "Resume once the Mission's craft is back (for example after loading a save), or End Mission."
                : "Get her aboard a craft with a kOS processor, switch to it, then Resume."));
        }

        /// <summary>Why Resume (or Retry) can't carry the Paused Mission on now, or "": it needs the player in the craft (#124).</summary>
        private string ResumeRefusal(Mission mission)
        {
            if (!FollowsCraft(mission)) return "";
            var craft = mission.VesselName.Length > 0 ? mission.VesselName : "the Mission's craft";
            if (ElsewhereInFlight)
                return "Resume from the Mission's craft: you are flying " + _active.Craft.Name + ". Switch to " + craft + ", then Resume.";
            // Out of Flight, a Pause that is about the craft (EVA science, no vessel to fly) waits for it; others carry on from its last facts.
            if (!CraftActive && (mission.EvaScience.Length > 0 || mission.PausedCannotFly))
                return "Resume from the Mission's craft: go to " + craft + " in Flight, then Resume.";
            if (mission.CannotFlyBecause.Length > 0) return "Can't resume yet: Claudia can fly no vessel (" + mission.CannotFlyBecause + ").";
            return "";
        }

        // ---- Switching away while Running ----

        /// <summary>The player confirmed a far switch (the adapter asked): the craft unloads, so the run is interrupted.</summary>
        private void FarSwitched(string vesselName)
        {
            var mission = _mission;
            if (mission == null || MissionOver || !IsRunning(mission)) return;
            mission.SwitchingAway = true;
            Interrupt(mission, "the player switched to " + (string.IsNullOrEmpty(vesselName) ? "a vessel" : vesselName) + ", far enough away to unload the craft");
        }

        /// <summary>The player is nearby in another vessel: a warning (once per trip out) 10 s before the craft would unload.</summary>
        private void RangeSeen(CraftRange range)
        {
            var mission = _mission;
            if (mission == null || MissionOver || !IsRunning(mission) || !ElsewhereInFlight) return;
            if (!MissionCraft.UnloadsSoon(range.Distance, range.UnloadDistance, range.RangeRate))
            {
                mission.UnloadWarned = false;
                return;
            }
            if (mission.UnloadWarned) return;
            mission.UnloadWarned = true;
            var left = MissionCraft.SecondsToUnload(range.Distance, range.UnloadDistance, range.RangeRate) ?? 0;
            var text = "The craft unloads in about " + Math.Ceiling(left).ToString("0", CultureInfo.InvariantCulture) + " s at this distance ("
                + (range.Distance / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " of " + (range.UnloadDistance / 1000).ToString("0.0", CultureInfo.InvariantCulture)
                + " km), and the running sequence will be interrupted. Head back toward " + mission.VesselName + " to keep it loaded.";
            Note(LogKind.Notice, text);
            _commands.Add(new PostScreenMessage(text, always: true));
        }

        /// <summary>The craft unloaded with the player elsewhere while Running: the run is interrupted, Debriefed on return.</summary>
        private void Unloaded(string vesselId)
        {
            var mission = _mission;
            if (mission == null || MissionOver || !IsRunning(mission)) return;
            // A Child Vessel's run is cut off the same way when it goes out of physics range.
            var child = RunningChildVesselId(mission);
            if (child.Length > 0)
            {
                if (vesselId == child) Interrupt(mission, mission.RunningChild + " unloaded: it went out of range of the player's vessel");
                return;
            }
            if (vesselId != mission.VesselId || CraftActive) return;
            Interrupt(mission, "the craft unloaded: the player's vessel went too far from it");
        }

        /// <summary>
        /// The run ended while the player is in another vessel (its craft unloaded, say): Claudia's Debrief needs the craft's
        /// facts, so it is owed until the player is back in the craft. True: deferred.
        /// </summary>
        private bool DebriefOnReturn(Mission mission, RunOutcome outcome)
        {
            if (!ElsewhereInFlight) return false;
            if (mission.Stage == MissionStage.Paused) mission.StageBeforePause = MissionStage.Debriefing;
            else mission.Stage = MissionStage.Debriefing;
            mission.Owed = Owed.Debrief;
            mission.OwedOutcome = outcome;
            Note(LogKind.Notice, "Claudia will Debrief it when you are back in " + mission.VesselName + ".");
            return true;
        }

        // ---- Docking ----

        /// <summary>Docking merges a vessel into the craft: a notice in the log and to Claudia, and the pending plan needs Re-plan.</summary>
        private void Docked(VesselsDocked docked)
        {
            var mission = _mission;
            if (mission == null || !FollowsCraft(mission)) return;
            var added = MissionCraft.Added(mission.Remote, mission.ProcessorPartId, mission.VesselId, docked.First, docked.Second);
            if (added == null) return;
            Note(LogKind.Notice, MissionCraft.DockingLine(added));
            Event(mission, "docked", added.Name);
            mission.LoadNotice = (mission.LoadNotice != null ? mission.LoadNotice + "\n\n" : "") + MissionCraft.DockingNotice(added);
            mission.Recheck = "docked with " + added.Name;
            mission.RecheckSince = mission.Requests;
        }

        /// <summary>A plan is offered: if it came from a request sent after docking, it was made with the rechecked facts.</summary>
        private static void PlanOffered(Mission mission)
        {
            if (mission.Requests > mission.RecheckSince) mission.Recheck = "";
        }
    }
}
