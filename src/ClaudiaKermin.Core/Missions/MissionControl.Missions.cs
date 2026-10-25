// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Several Missions in one save, one active (spec stories 150, 151, 29). The active Mission is <c>_mission</c>; the others
    /// are Paused at a sequence boundary, held as the save states they were parked with (<c>_parked</c>, written to the save with
    /// the active one), or finished, which are read-only history in their Mission folders. Starting or resuming a Mission parks
    /// the active one at its next sequence boundary, never mid-sequence: a Mission that is Running, in a request or in a manual
    /// stretch is left alone until it gets there, and the switch is queued.
    ///
    /// "The Mission in play" and "history" are different things: a finished Mission is in play (its Debrief still on screen)
    /// until a new one starts, and is history all the while.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The log was built again from the transcript after a load (<see cref="MissionLogRebuild"/>).</summary>
            public bool LogRebuilt;
            /// <summary>Cards made for a plan the log already holds are not added (a plan offered again after a load).</summary>
            public bool SuppressCards;
        }

        private enum SwitchKind
        {
            /// <summary>Start a new Mission.</summary>
            Start,
            /// <summary>Resume a Paused Mission.</summary>
            Resume,
            /// <summary>Just pause the active one.</summary>
            Pause,
        }

        /// <summary>A switch waiting for the active Mission to reach a sequence boundary.</summary>
        private sealed class QueuedSwitch
        {
            public SwitchKind Kind;
            public StartMission Start;
            public string Id;
        }

        private readonly List<MissionSaveState> _parked = new List<MissionSaveState>();
        private QueuedSwitch _switch;
        private bool _switching;

        /// <summary>Bumped whenever the Missions in the save change under the list; with the Mission in play it keys the cached list.</summary>
        private int _historyVersion;
        private string _historyKey;
        private List<MissionListEntry> _others = new List<MissionListEntry>();
        private readonly Dictionary<string, MissionListEntry> _finishedCache = new Dictionary<string, MissionListEntry>();

        /// <summary>
        /// The Mission is between Maneuver Sequences: nothing is Running, no request is out for it and it is not in a manual
        /// stretch, so it can be left as it is and picked up again later.
        /// </summary>
        private static bool AtBoundary(Mission mission)
        {
            return !mission.InFlight && !IsRunning(mission) && mission.Stage != MissionStage.ManualControl;
        }

        /// <summary>
        /// Gets the active Mission out of the way for <paramref name="wanted"/>: parks it now when it is at a boundary, queues the
        /// switch when it is not. False means the caller waits (the switch is queued and carried out at the boundary).
        /// </summary>
        private bool MakeRoom(QueuedSwitch wanted)
        {
            if (MissionOver) return true;
            if (AtBoundary(_mission))
            {
                Park(_mission);
                return true;
            }
            if (_switch == null)
                Note(LogKind.Notice, "Claudia finishes this sequence first: the Mission pauses at its next sequence boundary, then "
                    + (wanted.Kind == SwitchKind.Start ? "the new Mission starts." : wanted.Kind == SwitchKind.Resume ? "the other Mission resumes." : "it stays paused."));
            _switch = wanted;
            return false;
        }

        /// <summary>The active Mission becomes a Paused one in the save, waiting where it stands (a Paused Mission keeps the reason it was Paused for).</summary>
        private void Park(Mission mission)
        {
            var state = StateOf(mission);
            state.Active = false;
            state.TranscriptMarker = mission.Conversation.Count;
            if (state.Stage != MissionStage.Paused)
            {
                state.StageBeforePause = state.Stage;
                state.Stage = MissionStage.Paused;
                state.PauseReason = "";
            }
            _parked.RemoveAll(p => p.Id == mission.Id);
            _parked.Add(state);
            try
            {
                MissionStore.WriteVessel(mission.Folder, mission.VesselId, mission.VesselName);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The state carries the vessel too; only the folder's copy is missing.
            }
            Event(mission, "paused", "paused at a sequence boundary to make room for another Mission");
            _historyVersion++;
            _mission = null;
        }

        /// <summary>Carries out the queued switch once the active Mission is at a boundary; called before the view is rebuilt, after any input or reply.</summary>
        private void SwitchIfReady()
        {
            var queued = _switch;
            if (queued == null || _switching || (!MissionOver && !AtBoundary(_mission))) return;
            _switching = true;
            try
            {
                _switch = null;
                if (queued.Kind == SwitchKind.Start) Start(queued.Start);
                else if (queued.Kind == SwitchKind.Resume) ResumeRequested(queued.Id);
                else PauseRequested();
                TryPlan();
                TryOwed();
            }
            finally
            {
                _switching = false;
            }
        }

        private void PauseRequested()
        {
            if (MissionOver || _saveDirectory == null) return;
            MakeRoom(new QueuedSwitch { Kind = SwitchKind.Pause });
        }

        private void ResumeRequested(string id)
        {
            if (_saveDirectory == null || string.IsNullOrEmpty(id) || (_mission != null && _mission.Id == id)) return;
            Restore restore = null;
            var state = _parked.FirstOrDefault(p => p.Id == id);
            if (state != null) restore = new Restore { State = state };
            else if (MissionHistory.IsResumable(_saveDirectory, id)) restore = NoSnapshot(MissionStore.ReadInfo(MissionFolder.For(_saveDirectory, id)));
            if (restore == null) return;
            if (!MakeRoom(new QueuedSwitch { Kind = SwitchKind.Resume, Id = id })) return;

            _parked.RemoveAll(p => p.Id == id);
            _historyVersion++;
            Reinstate(restore, false);
            var mission = _mission;
            if (mission == null) return;
            Event(mission, "resumed", "resumed from the Missions list");
            // A Mission parked at a boundary carries on from there; one that was Paused for a reason (or has no snapshot) waits for its own Resume or Retry.
            if (mission.Stage == MissionStage.Paused && mission.PauseReason.Length == 0 && !restore.NoSnapshot)
            {
                mission.AwaitingResume = true;
                mission.LoadNotice = "The Mission was paused while the player flew another Mission and has just been resumed. Time has passed and the "
                    + "craft may have changed; treat the Game Snapshot as the truth about the craft.";
                Retried();
            }
        }

        private void DeleteRequested(string id)
        {
            if (_saveDirectory == null || string.IsNullOrEmpty(id)) return;
            // The Mission in play can be deleted only once it is over; Paused and active Missions are not history.
            var current = _mission != null && _mission.Id == id;
            if (current && !MissionOver) return;
            try
            {
                if (!MissionHistory.Delete(_saveDirectory, id)) return;
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't delete the Mission's folder: " + e.Message);
                return;
            }
            _finishedCache.Remove(id);
            _historyVersion++;
            if (current) _mission = null;
        }

        private void GoToMissionVesselRequested(string id)
        {
            var entry = Missions().FirstOrDefault(m => m.Id == id);
            if (entry != null && entry.CanGoToVessel) _commands.Add(new SwitchToVessel(entry.VesselId));
        }

        // ---- The Missions list ----

        /// <summary>The read-only list for the KSC and Tracking Station: the Mission in play first, then Paused Missions, then history, newest first.</summary>
        private IList<MissionListEntry> Missions()
        {
            var list = new List<MissionListEntry>();
            if (_saveDirectory == null) return list;
            var key = _saveDirectory + "|" + _historyVersion + "|" + (_mission == null ? "" : _mission.Id) + "|" + string.Join(",", _parked.Select(p => p.Id).ToArray());
            if (key != _historyKey)
            {
                _historyKey = key;
                _others = Others();
            }
            if (_mission != null) list.Add(InPlay(_mission));
            // The folder's cost log moves with every request; the parked Missions' costs are read again, the finished ones' never change.
            list.AddRange(_others.Where(e => !e.Finished).Select(e => Paused(e.Id)));
            list.AddRange(_others.Where(e => e.Finished));
            return list;
        }

        private MissionListEntry Paused(string id)
        {
            return MissionHistory.Paused(_saveDirectory, id, _parked.FirstOrDefault(p => p.Id == id));
        }

        private List<MissionListEntry> Others()
        {
            var others = new List<MissionListEntry>();
            foreach (var id in MissionHistory.Ids(_saveDirectory))
            {
                if (_mission != null && _mission.Id == id) continue;
                MissionListEntry entry;
                if (_parked.Any(p => p.Id == id)) entry = Paused(id);
                else if (!_finishedCache.TryGetValue(id, out entry))
                {
                    entry = MissionHistory.Finished(_saveDirectory, id, _config);
                    if (entry != null) _finishedCache[id] = entry;
                    else if (MissionHistory.IsResumable(_saveDirectory, id)) entry = Paused(id);
                }
                if (entry != null) others.Add(entry);
            }
            return others;
        }

        private MissionListEntry InPlay(Mission mission)
        {
            var finished = mission.Stage == MissionStage.Succeeded || mission.Stage == MissionStage.Ended || mission.Stage == MissionStage.Lost;
            var entry = new MissionListEntry
            {
                Id = mission.Id,
                Goal = mission.Goal,
                Status = finished ? (MissionStatus)Enum.Parse(typeof(MissionStatus), mission.Stage.ToString()) : mission.Stage == MissionStage.Paused ? MissionStatus.Paused : MissionStatus.Active,
                Current = true,
                Cost = mission.Committed,
                SpendingCap = mission.SpendingCap,
                VesselId = mission.VesselId,
                VesselName = mission.VesselName,
                CanGoToVessel = mission.VesselId.Length > 0,
                CanDelete = finished,
            };
            if (finished) entry.FinalDebrief = MissionLogRebuild.FinalDebrief(mission.Log);
            return entry;
        }
    }
}
