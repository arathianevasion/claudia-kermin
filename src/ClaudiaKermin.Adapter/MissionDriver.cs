// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKermin.Core.Missions;
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
        private GameScene? _scene;
        private float _nextSnapshot;

        public readonly MissionWindow Window;
        public readonly CapcomStrip Strip;

        public MissionDriver(MissionControl mission, bool debugTools)
        {
            _mission = mission;
            Window = new MissionWindow(Send, debugTools);
            Strip = new CapcomStrip(Window);
        }

        public void Send(MissionInput input)
        {
            _mission.Send(input);
            Carry();
        }

        /// <summary>Call each frame, after the background queue has drained.</summary>
        public void Update()
        {
            if (HighLogic.CurrentGame != null && HighLogic.SaveFolder != _saveFolder)
            {
                _saveFolder = HighLogic.SaveFolder;
                Send(new SaveLoaded(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), _saveFolder)));
            }

            var scene = Scene();
            if (scene != _scene)
            {
                _scene = scene;
                Send(new SceneChanged(scene));
            }

            var vessel = scene == GameScene.Flight ? FlightGlobals.ActiveVessel : null;
            if (vessel != null && Time.realtimeSinceStartup >= _nextSnapshot)
            {
                _nextSnapshot = Time.realtimeSinceStartup + SnapshotSeconds;
                Send(new UtAdvanced(Planetarium.GetUniversalTime()));
                Send(SnapshotReader.Read(vessel));
            }
            Carry();
        }

        public void OnGUI()
        {
            Window.View = _mission.View;
            Strip.View = _mission.View;
            Window.OnGUI();
            if (HighLogic.LoadedScene == GameScenes.FLIGHT) Strip.OnGUI();
            else Strip.Unlock();
            Carry();
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
            foreach (var command in _mission.TakeCommands())
            {
                var start = command as StartScript;
                if (start != null)
                {
                    StartScript(start);
                    continue;
                }
                var message = command as PostScreenMessage;
                if (message != null && !Window.Visible)
                    ScreenMessages.PostScreenMessage(message.Text, 6f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        private void StartScript(StartScript start)
        {
            var runner = ScriptRunner.Current;
            if (runner == null)
            {
                Send(new KosRunRefused("kOS isn't running in this scene, so the script can't start."));
                return;
            }
            runner.BeginRun(start.ProcessorPartId, start.Label, start.Source,
                outcome => Send(new KosRunEnded(outcome)),
                reason => Send(new KosRunRefused(reason)));
        }
    }
}
