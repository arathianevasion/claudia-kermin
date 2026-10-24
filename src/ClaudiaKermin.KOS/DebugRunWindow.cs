// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using ClaudiaKermin.Core.Kos;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// With <c>debug_tools = true</c>: a Flight window that runs a test script on the active vessel's
    /// processor (nearest the root) through the kOS bridge and shows its report or error. A developer
    /// tool: the player starts each fixed test script by hand and Claude is never involved, so it sits
    /// outside the Approval rule of ADR 0002.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DebugRunWindow : MonoBehaviour
    {
        private static readonly string[] Tests = { "report", "runtime_error", "no_report", "long_run" };

        private Rect _rect = new Rect(80, 120, 460, 300);
        private Vector2 _scroll;
        private uint? _partId;
        private string _status = "Idle.";
        private string _result = "";

        private void Awake()
        {
            if (!DebugToolsFlag.Read()) Destroy(this);
        }

        private void OnGUI()
        {
            _rect = GUILayout.Window(GetInstanceID(), _rect, Draw, "Claudia Kermin: kOS bridge test (debug_tools)");
        }

        private void Draw(int id)
        {
            var vessel = FlightGlobals.ActiveVessel;
            var bridge = KosBridge.Instance;
            var target = KosBridge.ChooseProcessor(vessel, null);

            GUILayout.Label(target.HasValue
                ? "Processor: part " + target.Value + " on '" + vessel.vesselName + "' (nearest the root)"
                : "No kOS processor on the active vessel.");
            if (!KosInternals.Result.IsCompatible) GUILayout.Label(KosInternals.Result.Message);

            GUILayout.BeginHorizontal();
            foreach (var test in Tests)
            {
                if (GUILayout.Button(test) && target.HasValue && bridge != null) Run(bridge, target.Value, test);
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Abort") && bridge != null && _partId.HasValue)
                bridge.Abort(_partId.Value, "aborted from the debug window");

            GUILayout.Label(_status);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(160));
            GUILayout.Label(_result);
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void Run(KosBridge bridge, uint partId, string test)
        {
            _partId = partId;
            _status = "Starting '" + test + "'...";
            _result = "";
            bridge.Start(partId, "claudia-debug:" + test, LoadScript(test), Show, reason =>
            {
                _status = "Refused: " + reason;
            });
        }

        private void Show(RunOutcome outcome)
        {
            _status = "Ended: " + outcome.Summary;
            _result = outcome.Kind == RunOutcomeKind.Finished
                ? "Report:\n" + KosValues.Format(outcome.Report)
                : "Terminal tail:\n" + (outcome.ErrorText.Length > 0 ? outcome.ErrorText : "(nothing printed)");
        }

        private static string LoadScript(string test)
        {
            var name = "ClaudiaKermin.KOS.debug." + test + ".ks";
            using (var stream = typeof(DebugRunWindow).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
