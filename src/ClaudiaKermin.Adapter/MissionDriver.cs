// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.IO;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Ui;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The adapter's side of the Mission seam: feeds <see cref="MissionControl"/> the save, the scene, UT
    /// and Game Snapshots, turns window clicks into inputs, and carries out its commands (starting
    /// approved scripts through the kOS bridge, screen messages). This is the only way the adapter
    /// reaches Mission logic. Lives as long as the game, so a Mission survives scene changes.
    /// </summary>
    public class MissionDriver
    {
        private const float SnapshotSeconds = 1f;

        private readonly MissionControl _mission;
        private string _saveFolder;
        private Game _game;
        private GameScene? _scene;
        private float _nextSnapshot;
        private readonly AttentionWatch _watch = new AttentionWatch();
        private int _watchedVersion = -1;
        private const float UtSeconds = 0.5f;
        private float _nextUt;

        private readonly ManualInputWatcher _inputWatcher = new ManualInputWatcher();
        private readonly VesselWatch _vessels;
        /// <summary>Staging, part loss, overheating and science during a run, for its Debrief.</summary>
        private readonly RunEventWatcher _events;

        /// <summary>The Mission window's body, drawn by the floating window or, docked in Flight, under the Capcom strip.</summary>
        public readonly MissionPanel Panel;
        public readonly MissionWindow Window;
        public readonly CapcomStrip Strip;
        /// <summary>The read-only Missions list: in the Space Center, the Tracking Station and wherever the toolbar button is.</summary>
        public readonly MissionsWindow MissionsList;

        /// <summary>The player undocked the Mission panel in Flight: it shows as the floating window. Kept for the session.</summary>
        public bool Undocked { get; private set; }
        /// <summary>The Mission being set up, shared by the Mission window and the Settings window.</summary>
        public readonly MissionSetup Setup = new MissionSetup();

        /// <summary>What the windows draw.</summary>
        public MissionView View
        {
            get { return _mission.View; }
        }
        /// <summary>The control locks while Claudia has the craft; the owner releases them with the other locks (scene change, shutdown).</summary>
        public readonly CraftLocks Locks = new CraftLocks();

        /// <summary>
        /// Claudia needs the player (a Debrief, an Approval or a Pause) and the Mission panel was closed when
        /// she did: the toolbar button flashes until the floating window or the docked panel is opened.
        /// </summary>
        public bool Flashing { get; private set; }

        /// <summary>The player has seen the button's call by another way (Settings open in the Space Center, where there is no Mission panel): the flash stops.</summary>
        public void AcknowledgeFlash()
        {
            Flashing = false;
            _pending = null;
        }
        // The alert behind the flash, until the player opens the window; the strip's amber tab reads it too.
        private Attention _pending;

        /// <summary>The player can read Claudia now: the floating window or the docked panel is up and not hidden by F2.</summary>
        private bool Watching
        {
            get
            {
                return !Window.UiHidden
                    && (Window.Visible || (HighLogic.LoadedSceneIsFlight && Strip.PanelShowing));
            }
        }

        /// <summary>In Flight with a Mission under way and the panel docked, the panel lives under the Capcom strip; otherwise in the floating window.</summary>
        private bool DockedNow
        {
            get { return HighLogic.LoadedSceneIsFlight && !Undocked && _mission.View.Stage != MissionStage.None; }
        }

        /// <summary>
        /// The Mission panel is open where it shows now: the docked panel under the Capcom strip, or the floating window.
        /// The toolbar button and the strip's tab toggle it.
        /// </summary>
        public bool PanelOpen
        {
            get { return DockedNow ? Strip.Expanded : Window.Visible; }
            set
            {
                if (DockedNow) Strip.Expanded = value;
                else Window.Visible = value;
            }
        }

        // Undock shows the panel as the floating window; Dock puts it back under the strip. It was open to be clicked, so it stays open.
        private void SetDocked(bool docked)
        {
            Undocked = !docked;
            Strip.Expanded = false;
            Window.Visible = false;
            PanelOpen = true;
        }

        // The panel moves between its two homes as the Mission comes and goes in Flight, staying open or closed:
        // a Mission started from the floating window docks, and one that ends falls back to the floating window.
        private void HandOver()
        {
            if (DockedNow && Window.Visible)
            {
                Window.Visible = false;
                Strip.Expanded = true;
            }
            else if (!DockedNow && Strip.Expanded)
            {
                Strip.Expanded = false;
                Window.Visible = true;
            }
        }

        /// <summary>The driver of this game session, for the scenario module and the leave-Flight guard.</summary>
        public static MissionDriver Current { get; private set; }

        /// <summary>A sequence is Running, so leaving Flight needs the player's say-so first.</summary>
        public bool LeavingFlightNeedsConfirmation
        {
            get { return _mission.View.LeavingFlightNeedsConfirmation; }
        }

        /// <summary>The Mission's save-state for the scenario module to write.</summary>
        public IList<MissionSaveState> ExportStates()
        {
            return _mission.ExportStates();
        }

        public MissionDriver(MissionControl mission, bool debugTools)
        {
            Current = this;
            _mission = mission;
            Panel = new MissionPanel(Send, Setup, debugTools) { SetDocked = SetDocked };
            Window = new MissionWindow(Panel);
            MissionsList = new MissionsWindow(Send);
            Strip = new CapcomStrip(Panel) { TabClicked = () => PanelOpen = !PanelOpen };
            GameEvents.onKerbalStatusChange.Add(OnKerbalStatusChange);
            _vessels = new VesselWatch(Send);
            _events = new RunEventWatcher(Send, () => _mission.View.Stage == MissionStage.Running,
                () => _mission.View.CraftVesselId, () => _mission.View.RunningChildVesselId);
            // Any stock alarm drops a running script's in-script warp (ADR 0004); the core ignores it when nothing runs.
            GameEvents.onAlarmTriggered.Add(alarm => Send(new StockAlarmFired()));
            // Building Block progress, for the strip and the log, with no Claude call behind it.
            ScriptRunner.BlockReported = (runId, block, result) =>
                Send(result == null ? (MissionInput)new KosBlockStarted(block, runId) : new KosBlockEnded(block, result, runId));
        }

        public void Send(MissionInput input)
        {
            _mission.Send(input);
            Carry();
        }

        /// <summary>Call each frame, after the background queue has drained.</summary>
        public void Update()
        {
            // A new Game object means a save was loaded: continue, quickload, revert or another save. A scene
            // change keeps the same Game, so it never counts. The core puts the Missions in step with the save.
            if (HighLogic.CurrentGame != null && !ReferenceEquals(HighLogic.CurrentGame, _game))
            {
                _game = HighLogic.CurrentGame;
                _saveFolder = HighLogic.SaveFolder;
                Send(new MissionsRestored(SaveDirectory(), Saved(_game)));
            }
            else if (HighLogic.CurrentGame != null && HighLogic.SaveFolder != _saveFolder)
            {
                _saveFolder = HighLogic.SaveFolder;
                Send(new SaveLoaded(SaveDirectory()));
            }

            var scene = Scene();
            if (scene != _scene)
            {
                _scene = scene;
                Send(new SceneChanged(scene));
            }

            // The clock runs in every scene, so a Due Time reached at the Tracking Station or in another vessel is noticed.
            if (HighLogic.CurrentGame != null && Planetarium.fetch != null && Time.realtimeSinceStartup >= _nextUt)
            {
                _nextUt = Time.realtimeSinceStartup + UtSeconds;
                Send(new UtAdvanced(Planetarium.GetUniversalTime()));
            }

            // Which vessel is the craft after a switch, docking or EVA: the core decides from what the game shows.
            _vessels.Update(scene == GameScene.Flight && FlightGlobals.ready);
            var vessel = scene == GameScene.Flight ? FlightGlobals.ActiveVessel : null;
            if (vessel != null && Time.realtimeSinceStartup >= _nextSnapshot)
            {
                _nextSnapshot = Time.realtimeSinceStartup + SnapshotSeconds;
                Send(new UtAdvanced(Planetarium.GetUniversalTime()));
                Send(SnapshotReader.Read(vessel));
                _vessels.Range(_mission.View.CraftVesselId);
                _vessels.Link(_mission.View.CraftVesselId);
            }
            else if (scene == GameScene.Editor && EditorLogic.fetch != null && Time.realtimeSinceStartup >= _nextSnapshot)
            {
                // The ship on the VAB/SPH floor is what the Craft Check judges before launch.
                _nextSnapshot = Time.realtimeSinceStartup + SnapshotSeconds;
                var ship = SnapshotReader.ReadEditor(EditorLogic.fetch.ship);
                if (ship != null) Send(ship);
            }

            // The core says what Claudia's hold on the craft locks; the player's own input held over a second is
            // reported back, and the core decides whether that takes Manual Control.
            var locks = scene == GameScene.Flight ? _mission.View.Locks : ControlLocks.None;
            Locks.Apply(locks);
            _inputWatcher.Update(locks != ControlLocks.None, seconds => Send(new ManualInputHeld(seconds)));
            if (scene == GameScene.Flight) _events.Update();

            // Once a Mission is under way its setup is spent: the next one starts from the defaults.
            if (_mission.View.CanEndMission) Setup.Reset();
            else Setup.SeedFrom(_mission.View.StartForm);
            Carry();
        }

        public void OnGUI()
        {
            Window.View = _mission.View;
            Strip.View = _mission.View;
            HandOver();
            Strip.Docked = !Undocked;
            Panel.OfferDock = Undocked && HighLogic.LoadedSceneIsFlight && _mission.View.Stage != MissionStage.None;
            Window.OnGUI();
            if (HighLogic.LoadedScene == GameScenes.FLIGHT) Strip.OnGUI();
            else Strip.Unlock();
            Carry();
        }

        private static string SaveDirectory()
        {
            return Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), HighLogic.SaveFolder);
        }

        /// <summary>The Mission snapshots the loaded game's save file holds.</summary>
        private static List<MissionSaveState> Saved(Game game)
        {
            var states = new List<MissionSaveState>();
            foreach (var module in game.scenarios)
                if (module.moduleName == typeof(MissionScenario).Name) states.AddRange(MissionScenario.Read(module.GetData()));
            return states;
        }

        private static GameScene Scene()
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.FLIGHT: return GameScene.Flight;
                case GameScenes.EDITOR: return GameScene.Editor;
                default: return GameScene.Other;
            }
        }

        /// <summary>Carries out the commands issued since the last call.</summary>
        private void Carry()
        {
            var attention = WatchAttention();
            foreach (var command in _mission.TakeCommands())
            {
                var start = command as StartScript;
                if (start != null)
                {
                    StartScript(start);
                    continue;
                }
                var abort = command as AbortScript;
                if (abort != null)
                {
                    // The existing abort path: stops the program and makes the craft safe.
                    var runner = ScriptRunner.Current;
                    if (runner != null && abort.RunId.HasValue) runner.Abort(abort.ProcessorPartId, abort.RunId.Value, abort.Reason);
                    else if (runner != null) runner.Abort(abort.ProcessorPartId, abort.Reason);
                    continue;
                }
                var safe = command as MakeCraftSafe;
                if (safe != null)
                {
                    CraftSafety.MakeSafe(safe, Send);
                    continue;
                }
                if (command is SeatClaudia)
                {
                    SeatClaudia();
                    continue;
                }
                if (command is SetDueTimeAlarm) DueTimeActions.SetAlarm((SetDueTimeAlarm)command);
                else if (command is ClearDueTimeAlarm) DueTimeActions.ClearAlarm(((ClearDueTimeAlarm)command).MissionId);
                else if (command is WarpToUt) DueTimeActions.WarpTo(((WarpToUt)command).Ut);
                else if (command is DropWarp) DueTimeActions.DropWarp();
                else if (command is HoldScriptWarp) ScriptWarpHold.Apply((HoldScriptWarp)command);
                else if (command is SwitchToVessel) DueTimeActions.GoToVessel(((SwitchToVessel)command).VesselId);
                var message = command as PostScreenMessage;
                // The alert below says it better when it fires for the same moment, unless the message must be seen.
                if (message != null && (message.Always || (attention == null && !Watching)))
                    ScreenMessages.PostScreenMessage(message.Text, 6f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (attention != null) NotifyPlayer(attention);
        }

        /// <summary>The new moment Claudia needs the player in the latest view, if the window is closed for it; null otherwise.</summary>
        private Attention WatchAttention()
        {
            if (Watching) { Flashing = false; _pending = null; }
            Strip.Pending = _pending;
            var view = _mission.View;
            if (view.Version == _watchedVersion) return null;
            _watchedVersion = view.Version;
            var attention = _watch.Observe(view);
            return attention != null && !Watching ? attention : null;
        }

        // Closed window: flash the toolbar button, say it on screen, and drop to 1x so the player isn't warped past it.
        // An Abort recommendation also brings the Mission panel up, with its Abort button.
        private void NotifyPlayer(Attention attention)
        {
            Flashing = true;
            _pending = attention;
            Strip.Pending = attention;
            if (attention.Kind == AttentionKind.Abort) PanelOpen = true;
            ScreenMessages.PostScreenMessage(attention.Text, 6f, ScreenMessageStyle.UPPER_CENTER);
            // Cancelling the warp-to too: KSP's warp-to (a script's in-script warp) would otherwise raise the rate again.
            if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0) DueTimeActions.DropWarp();
        }

        /// <summary>A crewed Mission started in the editor with her not aboard: she takes a free seat.</summary>
        private static void SeatClaudia()
        {
            var game = HighLogic.CurrentGame;
            if (game != null && game.CrewRoster != null && Roster.RosterGateway.SeatInEditor(game.CrewRoster)) return;
            ScreenMessages.PostScreenMessage("Claudia could not be seated: seat her in the crew dialog yourself before launch.", 8f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Her death is a game event for the core; only a crewed Mission cares (the core ignores it for a remote one).</summary>
        private void OnKerbalStatusChange(ProtoCrewMember kerbal, ProtoCrewMember.RosterStatus oldStatus, ProtoCrewMember.RosterStatus newStatus)
        {
            if (newStatus == ProtoCrewMember.RosterStatus.Dead && Roster.RosterGateway.IsClaudia(kerbal)) Send(new ClaudiaKilled(VesselAboard(kerbal)));
        }

        /// <summary>The id of the vessel whose crew still holds her, or "" when none does.</summary>
        private static string VesselAboard(ProtoCrewMember kerbal)
        {
            if (FlightGlobals.Vessels == null) return "";
            foreach (var vessel in FlightGlobals.Vessels)
                if (vessel != null && vessel.GetVesselCrew().Contains(kerbal)) return vessel.id.ToString("N");
            return "";
        }

        private void StartScript(StartScript start)
        {
            var runner = ScriptRunner.Current;
            if (runner == null)
            {
                Send(new KosRunRefused("kOS isn't running in this scene, so the script can't start.", start.RunId));
                return;
            }
            runner.BeginRun(start.ProcessorPartId, start.RunId, start.Label, start.Source,
                outcome => Send(new KosRunEnded(outcome, start.RunId)),
                reason => Send(new KosRunRefused(reason, start.RunId)),
                what => Send(new KosRunBlocked(what, start.RunId)));
        }
    }
}
