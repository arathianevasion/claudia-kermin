// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// With <c>debug_tools = true</c>: the debug block test runner (spec, Testing: seam 3). One click
    /// from the main menu or Space Center runs a Building Block test unattended in real physics: it
    /// writes the test's scenario save and reference craft (from <c>tests/blocks/&lt;test&gt;/</c>,
    /// shipped inside this DLL) to <c>saves/claudia_block_tests/</c>, loads that save, spawns the craft
    /// into the test's orbit, flies to it, runs the test's Mission Script bundled with the Core Library
    /// through the kOS bridge, and judges its Script Result against the contract ranges in
    /// <c>test.cfg</c>. Results go to KSP.log and <c>saves/claudia_block_tests/block-tests.log</c>.
    /// A developer tool: Claude is never involved, so it sits outside the Approval rule of ADR 0002.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class BlockTestRunner : MonoBehaviour
    {
        private const string SaveFolder = "claudia_block_tests";
        private const string ResourcePrefix = "ClaudiaKermin.KOS.blocktests.";

        private enum Stage
        {
            Idle,
            LoadingSave,
            WaitingForFlight,
            Running,
        }

        private readonly List<string> _log = new List<string>();
        private Rect _rect = new Rect(80, 440, 520, 320);
        private Vector2 _scroll;
        private bool _visible = true;

        private Stage _stage = Stage.Idle;
        private string _test;
        private BlockTestSpec _spec;
        private uint _vesselId;
        private uint _partId;
        private float _deadline;
        private int _sceneFrames;

        private void Awake()
        {
            if (!DebugToolsFlag.Read())
            {
                Destroy(this);
                return;
            }
            DontDestroyOnLoad(this);
        }

        private static string SaveDirectory
        {
            get { return Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), SaveFolder); }
        }

        /// <summary>The tests shipped in this DLL, by folder name under <c>tests/blocks/</c>.</summary>
        private static IEnumerable<string> TestNames()
        {
            return typeof(BlockTestRunner).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".test.cfg", StringComparison.Ordinal))
                .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - ".test.cfg".Length))
                .OrderBy(n => n, StringComparer.Ordinal);
        }

        private static string Resource(string test, string file)
        {
            using (var stream = typeof(BlockTestRunner).Assembly.GetManifestResourceStream(ResourcePrefix + test + "." + file))
            {
                if (stream == null) throw new FileNotFoundException("not shipped in the DLL: tests/blocks/" + test + "/" + file);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        // ---- The window ----

        private void OnGUI()
        {
            var scene = HighLogic.LoadedScene;
            if (scene != GameScenes.MAINMENU && scene != GameScenes.SPACECENTER && scene != GameScenes.FLIGHT) return;
            if (!_visible)
            {
                if (GUI.Button(new Rect(_rect.x, _rect.y, 160, 22), "Block tests (debug)")) _visible = true;
                return;
            }
            _rect = GUILayout.Window(GetInstanceID(), _rect, Draw, "Claudia Kermin: block test runner (debug_tools)");
        }

        private void Draw(int id)
        {
            GUILayout.BeginHorizontal();
            foreach (var test in TestNames())
            {
                GUI.enabled = _stage == Stage.Idle && HighLogic.LoadedScene != GameScenes.FLIGHT;
                if (GUILayout.Button("Run " + test)) Begin(test);
            }
            GUI.enabled = true;
            if (GUILayout.Button("Hide", GUILayout.Width(50))) _visible = false;
            GUILayout.EndHorizontal();
            GUILayout.Label(_stage == Stage.Idle
                ? "Idle. Running a test loads its own save (" + SaveFolder + "); leave Flight first."
                : "Running '" + _test + "': " + _stage);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(220));
            GUILayout.Label(string.Join("\n", _log.ToArray()));
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        // ---- The run ----

        private void Begin(string test)
        {
            _test = test;
            _log.Clear();
            try
            {
                _spec = BlockTestSpec.Parse(test, Resource(test, "test.cfg"));
                Directory.CreateDirectory(Path.Combine(SaveDirectory, "Ships/VAB"));
                File.WriteAllText(Path.Combine(SaveDirectory, "persistent.sfs"), Resource(test, "scenario.sfs"));
                File.WriteAllText(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.Craft), Resource(test, _spec.Craft));

                var game = GamePersistence.LoadGame("persistent", SaveFolder, true, false);
                if (game == null) throw new InvalidOperationException("KSP wouldn't load the scenario save");
                Note("loaded the scenario save; going to the Space Center");
                HighLogic.CurrentGame = game;
                HighLogic.SaveFolder = SaveFolder;
                game.startScene = GameScenes.SPACECENTER;
                _stage = Stage.LoadingSave;
                _sceneFrames = 0;
                game.Start();
            }
            catch (Exception e)
            {
                Fail("couldn't start: " + e.Message);
                Debug.LogException(e);
            }
        }

        private void Update()
        {
            try
            {
                if (_stage == Stage.LoadingSave) InSpaceCenter();
                else if (_stage == Stage.WaitingForFlight) InFlight();
                else if (_stage == Stage.Running && Time.realtimeSinceStartup > _deadline) TimedOut();
            }
            catch (Exception e)
            {
                Fail("runner error: " + e.Message);
                Debug.LogException(e);
            }
        }

        /// <summary>Once the Space Center has settled, spawn the craft into orbit and fly to it.</summary>
        private void InSpaceCenter()
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER || ++_sceneFrames < 30) return;
            var proto = CraftSpawner.IntoOrbit(Path.Combine(SaveDirectory, "Ships/VAB/" + _spec.Craft),
                _spec.OrbitBody, _spec.OrbitAltitude, _spec.OrbitInclination);
            _vesselId = proto.persistentId;
            Note("spawned '" + proto.vesselName + "' in a " + _spec.OrbitAltitude + " m orbit of " + _spec.OrbitBody + "; going to Flight");
            GamePersistence.SaveGame("persistent", SaveFolder, SaveMode.OVERWRITE);
            _stage = Stage.WaitingForFlight;
            _sceneFrames = 0;
            FlightDriver.StartAndFocusVessel("persistent", HighLogic.CurrentGame.flightState.protoVessels.IndexOf(proto));
        }

        /// <summary>Once the craft is the active, unpacked vessel, start the test script on its processor.</summary>
        private void InFlight()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return;
            var vessel = FlightGlobals.ActiveVessel;
            var bridge = KosBridge.Instance;
            if (vessel == null || vessel.persistentId != _vesselId || vessel.packed || bridge == null || ++_sceneFrames < 60) return;

            var processor = KosBridge.ChooseProcessor(vessel, null);
            if (!processor.HasValue)
            {
                Fail("the reference craft has no kOS processor");
                return;
            }
            _partId = processor.Value;
            _stage = Stage.Running;
            _deadline = Time.realtimeSinceStartup + (float)_spec.TimeoutSeconds;
            Note("running " + _spec.Block + " (timeout " + _spec.TimeoutSeconds + " s)");
            // The default wait caps, so a test runs the same whatever the developer's player config says.
            var caps = new PlayerConfig();
            var source = CoreLibrary.Bundle(Resource(_test, "test.ks"), caps.WaitCapOrbits, caps.WaitCapGroundSeconds);
            bridge.BeginRun(_partId, "claudia-blocktest:" + _test, source, Judge, reason => Fail("the bridge refused: " + reason));
        }

        private void Judge(RunOutcome outcome)
        {
            if (_stage != Stage.Running) return;
            Finish(_spec.Judge(outcome));
        }

        private void TimedOut()
        {
            var result = _spec.TimedOut();
            Finish(result);
            if (KosBridge.Instance != null) KosBridge.Instance.Abort(_partId, "block test timed out");
        }

        private void Fail(string why)
        {
            Finish(new BlockTestResult(false, "FAIL " + (_test ?? "block test") + ": " + why, new List<string>()));
        }

        private void Finish(BlockTestResult result)
        {
            _stage = Stage.Idle;
            Note(result.Headline);
            foreach (var line in result.Lines) Note("  " + line);
            try
            {
                Directory.CreateDirectory(SaveDirectory);
                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z ";
                File.AppendAllText(Path.Combine(SaveDirectory, "block-tests.log"),
                    stamp + result.Headline + "\n" + string.Concat(result.Lines.Select(l => "    " + l + "\n").ToArray()));
            }
            catch (IOException e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't write block-tests.log: " + e.Message);
            }
        }

        private void Note(string line)
        {
            _log.Add(line);
            Debug.Log(ModInfo.LogTag + " block test: " + line);
        }
    }
}
