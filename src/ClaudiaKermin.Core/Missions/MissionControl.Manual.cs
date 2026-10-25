// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Manual Control and the control locks (spec: Manual Control; story 91 and 93 to 97). While Claudia has the
    /// craft the adapter keeps some controls locked (<see cref="MissionView.Locks"/>). The player takes the craft
    /// from the button, from input held over a second, or by approving a planned manual stretch; that needs a link
    /// or other non-Tourist crew aboard. A running script is aborted first and its run id dropped, so late events
    /// from it are ignored whatever run comes next. Hand back asks Claudia for a short Debrief and then a revised
    /// Mission Plan, through the usual Spending Cap path. State the plan was offered against is kept to turn
    /// Approve into Re-plan when the craft changes beyond the player's tolerance.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>In Manual Control from an Approved manual stretch, so its Debrief is judged against the plan's target.</summary>
            public bool ManualPlanned;
            /// <summary>How Manual Control began, for Claudia's Debrief.</summary>
            public string ManualOrigin = "";
            /// <summary>Handed back: the Debrief of the manual stretch is under way, and a revised Mission Plan follows it.</summary>
            public bool HandBack;
            public string HandBackMessage = "";
            /// <summary>The Game Snapshot the pending plan was offered against (not saved; taken from the next snapshot after a load).</summary>
            public GameSnapshot PlanBaseline;
            /// <summary>UT of the last "not available" notice, so held keys don't repeat it every second.</summary>
            public double RefusedAt = double.NegativeInfinity;
        }

        /// <summary>The "Manual Control isn't available" notice is repeated at most this often, in UT seconds, while keys stay held.</summary>
        private const double RefusalNoticeSeconds = 30;

        /// <summary>UT since which the craft has had no link and no other crew aboard (nobody could fly it); null otherwise.</summary>
        private double? _strandedSince;
        /// <summary>UT the link last came back after a drop; until the grace has passed since, the link is unsteady.</summary>
        private double _linkBackAt = double.NegativeInfinity;

        /// <summary>
        /// Keeps the link's history from each Game Snapshot, and takes the craft back once the craft has been without a
        /// link or other crew in Manual Control for longer than the grace: a link that drops for a moment, or in the
        /// first snapshot after the player took Manual Control, doesn't hand the craft straight back. The history starts
        /// again on another vessel, on the first snapshot after a load or back in Flight, and when time runs backwards
        /// (a quickload).
        /// </summary>
        private void LinkSeen(GameSnapshot previous, GameSnapshot snapshot)
        {
            if (previous == null || previous.Craft.VesselId != snapshot.Craft.VesselId || snapshot.Ut < previous.Ut)
            {
                _strandedSince = null;
                _linkBackAt = double.NegativeInfinity;
            }
            else if (snapshot.HasLink && !previous.HasLink) _linkBackAt = _ut;
            if (snapshot.HasLink || snapshot.HasOtherCrew) _strandedSince = null;
            else if (!_strandedSince.HasValue) _strandedSince = _ut;
            if (InFlightManual && _ut - _strandedSince > _config.ManualLinkGraceSeconds) LinkLostDuringManual();
        }

        /// <summary>The Mission is in Manual Control, in Flight.</summary>
        private bool InFlightManual
        {
            get { return _mission != null && _mission.Stage == MissionStage.ManualControl && _scene == GameScene.Flight; }
        }

        /// <summary>In Manual Control with no link and no other crew aboard: game seconds until Claudia takes the craft back.</summary>
        private double? LinkLossTakeBackIn()
        {
            if (!InFlightManual || !_strandedSince.HasValue) return null;
            return Math.Max(0, _strandedSince.Value + _config.ManualLinkGraceSeconds - _ut);
        }

        /// <summary>
        /// A steady link to Kerbin (CommNet off counts) or another non-Tourist crew member aboard: someone could really fly
        /// the craft. A link that came back less than the grace ago doesn't count, so the button isn't offered just before
        /// the link drops again and Claudia takes the craft back.
        /// </summary>
        private bool ManualAllowed
        {
            get { return _snapshot != null && ManualUnavailableBecause().Length == 0; }
        }

        /// <summary>Why nobody could fly the craft now; "" when someone could (or there is no Game Snapshot yet).</summary>
        private string ManualUnavailableBecause()
        {
            if (_snapshot == null || _snapshot.HasOtherCrew) return "";
            if (!_snapshot.HasLink) return "no link to Kerbin and no other crew aboard";
            return _ut - _linkBackAt < _config.ManualLinkGraceSeconds ? "the link to Kerbin is unsteady" : "";
        }

        /// <summary>A sequence is Running, or a plan waits for Approval with no request out: the player may take the craft now.</summary>
        private bool CanTakeManual
        {
            get
            {
                var mission = _mission;
                if (MissionOver || _scene != GameScene.Flight || mission.DeferredRestore != null) return false;
                if (mission.Stage == MissionStage.Running) return true;
                var waiting = (mission.Stage == MissionStage.AwaitingPlanApproval && !mission.Declined)
                    || mission.Stage == MissionStage.AwaitingSequenceApproval || mission.Stage == MissionStage.UnsafeScript;
                return waiting && !mission.InFlight;
            }
        }

        /// <summary>The controls locked while Claudia has the craft: none in Manual Control, warp too while Running.</summary>
        private ControlLocks LocksNow()
        {
            var mission = _mission;
            if (MissionOver || _scene != GameScene.Flight || mission.Stage == MissionStage.ManualControl) return ControlLocks.None;
            var locks = IsRunning(mission) ? ControlLocks.Craft | ControlLocks.Warp : ControlLocks.Craft;
            // With no link and no other crew nobody could really fly it: the flight controls stay locked too.
            if (_snapshot != null && !ManualAllowed) locks |= ControlLocks.Attitude;
            return locks;
        }

        // ---- Taking the craft ----

        private void InputHeld(double seconds)
        {
            if (seconds <= _config.ManualHoldSeconds) return;
            TakeManual("input held over " + _config.ManualHoldSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s");
        }

        private void TakeManual(string how)
        {
            var mission = _mission;
            if (MissionOver || mission.Stage == MissionStage.ManualControl || !CanTakeManual) return;
            if (!ManualAllowed)
            {
                if (_ut - mission.RefusedAt >= RefusalNoticeSeconds)
                {
                    mission.RefusedAt = _ut;
                    Note(LogKind.Notice, "Manual Control isn't available: " + ManualUnavailableBecause() + ", so the flight controls stay locked.");
                }
                return;
            }

            var plan = mission.PendingSequence;
            mission.ManualPlanned = false;
            if (mission.Stage == MissionStage.Running)
            {
                mission.ManualOrigin = "while the Sequence Plan " + MissionPrompts.Quoted(plan != null ? plan.Name : "(unknown)")
                    + " was running; the mod aborted it and made the craft safe";
                // The run is over for the loop: its late events carry a run id nothing waits for any more.
                if (mission.RunningProcessor.HasValue) _commands.Add(new AbortScript(mission.RunningProcessor.Value, "the player took Manual Control"));
                mission.RunId = null;
                mission.RunningProcessor = null;
            }
            else mission.ManualOrigin = "while a plan waited for the player's Approval";
            Entered(mission, how, "Manual Control: you have the controls and Claudia only advises. Hand back to Claudia when you are done.");
        }

        /// <summary>The player approved a planned manual stretch: the craft is theirs until they hand it back.</summary>
        private void StartPlannedManual(Mission mission, SequencePlan plan)
        {
            if (!ManualAllowed)
            {
                Note(LogKind.Notice, "Can't start the manual stretch: " + ManualUnavailableBecause() + ", so the flight controls stay locked.");
                return;
            }
            mission.ManualPlanned = true;
            mission.ManualOrigin = "the planned manual stretch " + MissionPrompts.Quoted(plan.Name);
            Entered(mission, "planned stretch " + plan.Name, "Manual stretch approved: you have the controls and Claudia only advises. Hand back to Claudia when you are done.");
        }

        private void Entered(Mission mission, string how, string line)
        {
            mission.Stage = MissionStage.ManualControl;
            mission.ConfirmPrompt = null;
            Note(LogKind.Notice, line);
            Event(mission, "manual_control", how);
        }

        // ---- Handing back ----

        private void HandedBack(string message)
        {
            var mission = _mission;
            if (MissionOver || mission.Stage != MissionStage.ManualControl) return;
            mission.HandBack = true;
            mission.HandBackMessage = (message ?? "").Trim();
            Note(LogKind.Player, mission.HandBackMessage.Length > 0 ? "Hand back to Claudia: " + mission.HandBackMessage : "Hand back to Claudia.");
            Event(mission, "hand_back", mission.HandBackMessage);
            mission.Stage = MissionStage.Debriefing;
            Begin(HandBackTurn(mission));
        }

        /// <summary>The short Debrief of a manual stretch: judged for a planned stretch, a plain account for Manual Control nobody planned.</summary>
        private Turn HandBackTurn(Mission mission)
        {
            var plan = mission.PendingSequence ?? new SequencePlan { Name = "(unknown)" };
            return new Turn
            {
                Purpose = Purpose.Debrief,
                Effort = "high",
                UserText = MissionPrompts.HandBackReport(plan, mission.ManualPlanned, mission.ManualOrigin, mission.HandBackMessage)
                    + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.HandBackDebriefNotice(mission.ManualPlanned),
            };
        }

        /// <summary>The Debrief of the manual stretch is in and the Mission goes on: Claudia revises the Mission Plan for Approval.</summary>
        private void RevisePlanAfterHandBack(Mission mission)
        {
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.MissionPlan,
                Effort = "high",
                UserText = "Revise the Mission Plan.\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.RevisedPlanNotice,
            });
        }

        /// <summary>
        /// The link dropped during Manual Control with no other crew aboard (story 104): Claudia takes the craft back,
        /// the mod makes it safe once, warp drops, the player is told on screen why the controls went dead, and the stretch
        /// is Debriefed as a hand back with no message.
        /// </summary>
        private void LinkLostDuringManual()
        {
            var mission = _mission;
            const string text = "No link to Kerbin and no other crew: Claudia has the craft back and the controls are locked.";
            Note(LogKind.Notice, text);
            // Off warp first, so the make-safe's throttle and steering apply to a craft off rails.
            _commands.Add(new DropWarp());
            _commands.Add(new MakeCraftSafe("the link dropped during Manual Control with no other crew aboard"));
            _commands.Add(new PostScreenMessage(text, always: true));
            mission.ManualOrigin += "; then the link dropped with no other crew aboard, so you took the craft back and the mod made it safe";
            HandedBack("");
        }

        // ---- Re-plan when the craft changed ----

        /// <summary>Why the pending plan is stale ("" when it isn't): the craft changed beyond tolerance since the plan was offered.</summary>
        private string StaleBecause(Mission mission)
        {
            if (mission == null || !mission.AwaitingApproval || mission.Declined || mission.InFlight) return "";
            if (mission.Stage == MissionStage.AwaitingSequenceApproval && mission.PendingSequence == null) return "";
            return PlanDrift.Exceeded(mission.PlanBaseline, _snapshot, _config.ReplanTolerancePercent);
        }

        /// <summary>Re-plan: after "Couldn't produce a safe script", or when the craft changed beyond the pending plan's tolerances.</summary>
        private void Replanned()
        {
            var mission = _mission;
            if (mission != null && mission.Stage == MissionStage.UnsafeScript)
            {
                ScriptRetried(null, true);
                return;
            }
            var stale = StaleBecause(mission);
            if (stale.Length == 0 || !CanAnswerPlan) return;
            var missionPlan = mission.Stage == MissionStage.AwaitingPlanApproval;
            Note(LogKind.Player, "Re-plan: the craft changed (" + stale + ").");
            mission.ConfirmPrompt = null;
            mission.PlanBaseline = null;
            mission.Stage = missionPlan ? MissionStage.Planning : MissionStage.Sequencing;
            Begin(new Turn
            {
                Purpose = missionPlan ? Purpose.MissionPlan : Purpose.SequencePlan,
                Effort = "high",
                UserText = MissionPrompts.ReplanBecauseCraftChanged(stale, missionPlan) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = missionPlan ? MissionPrompts.PlanNotice : MissionPrompts.SequenceNotice,
            });
        }
    }
}
