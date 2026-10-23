// PROTOTYPE: throwaway kOS round-trip spike. In-flight test bench for kOS round-trip spike (issue #18).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using kOS.Safe.Compilation;
using kOS.Safe.Execution;
using kOS.Safe.Persistence;
using kOS.Safe.UserIO;
using kOS.Safe.Utilities;
using UnityEngine;

namespace ClaudiaSpike
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SpikeWindow : MonoBehaviour
    {
        private Rect rect = new Rect(60, 80, 760, 640);
        private bool minimized;
        private Vector2 logScroll;
        private int testIndex;
        private bool deliverLocal = true;
        private readonly Queue<Action> pending = new Queue<Action>();

        private void Start()
        {
            SpikeLog.Log("=== Flight scene start. Live addons: " + ClaudiaAddon.All.Count(a => !a.IsStale) + ", stale: " + ClaudiaAddon.All.Count(a => a.IsStale));
            GameEvents.onPartCouple.Add(OnCouple);
            GameEvents.onPartUndock.Add(OnUndock);
            GameEvents.onStageSeparation.Add(OnStageSep);
            GameEvents.onVesselSOIChanged.Add(OnSoi);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneRequest);
            GameEvents.onVesselChange.Add(OnVesselChange);
        }

        private void OnDestroy()
        {
            GameEvents.onPartCouple.Remove(OnCouple);
            GameEvents.onPartUndock.Remove(OnUndock);
            GameEvents.onStageSeparation.Remove(OnStageSep);
            GameEvents.onVesselSOIChanged.Remove(OnSoi);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneRequest);
            GameEvents.onVesselChange.Remove(OnVesselChange);
        }

        private void OnCouple(GameEvents.FromToAction<Part, Part> a) { SpikeLog.Log("EVENT dock/couple " + a.from.vessel.vesselName + " -> " + a.to.vessel.vesselName); Later(DumpAddons); }
        private void OnUndock(Part p) { SpikeLog.Log("EVENT undock part=" + p.flightID); Later(DumpAddons); }
        private void OnStageSep(EventReport r) { SpikeLog.Log("EVENT stage separation stage=" + r.stage); Later(DumpAddons); }
        private void OnSoi(GameEvents.HostedFromToAction<Vessel, CelestialBody> a) { SpikeLog.Log("EVENT SOI change " + a.host.vesselName + ": " + a.from.bodyName + " -> " + a.to.bodyName + " warp=" + TimeWarp.CurrentRate); }
        private void OnSceneRequest(GameScenes s) { SpikeLog.Log("EVENT scene change requested -> " + s); }
        private void OnVesselChange(Vessel v) { SpikeLog.Log("EVENT active vessel -> " + v.vesselName); }

        // Run a few frames later so vessel references have settled after dock/undock.
        private void Later(Action a) { StartCoroutine(Delay(a)); }
        private System.Collections.IEnumerator Delay(Action a) { yield return new WaitForSeconds(1f); a(); }

        private void DumpAddons()
        {
            foreach (var a in ClaudiaAddon.All)
                SpikeLog.Log(string.Format("  addon#{0} {1}{2}", a.InstanceId, a.Describe(), a.RunActive ? " [run active: " + a.RunLabel + "]" : ""));
        }

        // CPU work happens in FixedUpdate, the same phase kOS runs in.
        private void FixedUpdate()
        {
            while (pending.Count > 0)
            {
                try { pending.Dequeue()(); }
                catch (Exception e) { SpikeLog.Log("EXCEPTION " + e); }
            }
        }

        private void OnGUI()
        {
            rect = GUILayout.Window(0x6c617564, rect, DrawWindow, "PROTOTYPE: Claudia kOS round-trip spike");
        }

        private void DrawWindow(int id)
        {
            if (GUILayout.Button(minimized ? "Expand" : "Minimize")) minimized = !minimized;
            if (minimized) { rect.height = 60; GUI.DragWindow(); return; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Test:", GUILayout.Width(40));
            testIndex = GUILayout.SelectionGrid(testIndex, SpikeScripts.Names, SpikeScripts.Names.Length);
            GUILayout.EndHorizontal();
            deliverLocal = GUILayout.Toggle(deliverLocal, deliverLocal ? "Deliver to processor's local disk (C# HardDisk.SaveFile)" : "Deliver to archive 0:/ (Ships/Script)");
            string test = SpikeScripts.Names[testIndex];

            foreach (var a in ClaudiaAddon.All.ToList())
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(string.Format("addon#{0} {1}  idle={2}  last={3}", a.InstanceId, a.Describe(),
                    a.IsStale ? "-" : a.Shared.Interpreter.IsWaitingForCommand().ToString(), a.LastOutcome));
                if (!a.IsStale)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("A: keystrokes")) Queue(() => StartByKeys(a, test));
                    if (GUILayout.Button("B: message")) Queue(() => StartByMessage(a, test));
                    if (GUILayout.Button("C: direct")) Queue(() => StartDirect(a, test));
                    if (GUILayout.Button("Abort")) Queue(() => { SpikeLog.Log("addon#" + a.InstanceId + " ABORT via Cpu.BreakExecution(true)"); a.Shared.Cpu.BreakExecution(true); });
                    if (GUILayout.Button("Install boot listener + reboot")) Queue(() => StartCoroutine(InstallBoot(a)));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Dump addons")) DumpAddons();
            if (GUILayout.Button("Clear log")) SpikeLog.Lines.Clear();
            GUILayout.EndHorizontal();

            logScroll = GUILayout.BeginScrollView(logScroll, GUILayout.Height(300));
            GUILayout.Label(string.Join("\n", SpikeLog.Lines.Skip(Math.Max(0, SpikeLog.Lines.Count - 60)).ToArray()));
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void Queue(Action a) { pending.Enqueue(a); }

        // Delivery: write the script where kOS can see it and return its kOS path.
        private string Deliver(ClaudiaAddon a, string test)
        {
            string text = SpikeScripts.Get(test);
            if (deliverLocal)
            {
                var disk = a.Processor.HardDisk;
                disk.SaveFile(VolumePath.FromString("/claudia/" + test + ".ks"), new FileContent(text));
                int volumeId = a.Shared.VolumeMgr.GetVolumeId(disk);
                return volumeId + ":/claudia/" + test + ".ks";
            }
            string dir = Path.Combine(SafeHouse.ArchiveFolder, "claudia");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, test + ".ks"), text);
            return "0:/claudia/" + test + ".ks";
        }

        // Start method A: type the command into the terminal.
        private void StartByKeys(ClaudiaAddon a, string test)
        {
            string path = Deliver(a, test);
            a.BeginRun(test + " via A:keys " + path);
            var window = a.Processor.GetWindow();
            foreach (char ch in "runpath(\"" + path + "\").") window.ProcessOneInputChar(ch, null);
            window.ProcessOneInputChar((char)UnicodeCommand.STARTNEXTLINE, null);
        }

        // Start method B: message to a resident boot listener (claudia_boot.ks).
        private void StartByMessage(ClaudiaAddon a, string test)
        {
            string path = Deliver(a, test);
            a.BeginRun(test + " via B:message " + path);
            a.Processor.Send(new kOS.Safe.Encapsulation.StringValue(path));
        }

        // Start method C: push a program context directly, the way CPU.Boot() runs a boot file.
        private void StartDirect(ClaudiaAddon a, string test)
        {
            var shared = a.Shared;
            if (!shared.Interpreter.IsWaitingForCommand()) { SpikeLog.Log("addon#" + a.InstanceId + " C:direct refused: CPU busy"); return; }
            string path = Deliver(a, test);
            a.BeginRun(test + " via C:direct " + path);
            string command = "run \"" + path + "\".";
            var options = new CompilerOptions
            {
                LoadProgramsInSameAddressSpace = true,
                FuncManager = shared.FunctionManager,
                BindManager = shared.BindingMgr,
                AllowClobberBuiltins = SafeHouse.Config.AllowClobberBuiltIns,
                IsCalledFromRun = false
            };
            shared.ScriptHandler.ClearContext("program");
            IProgramContext context = shared.Cpu.SwitchToProgramContext();
            context.Silent = false;
            shared.Cpu.YieldProgram(YieldFinishedCompile.RunScript(new CommandPath(command), 1, command, "program", options));
        }

        // Boot assignment from C#: copy the listener to the local disk, set bootFile, power-cycle.
        private System.Collections.IEnumerator InstallBoot(ClaudiaAddon a)
        {
            var p = a.Processor;
            p.HardDisk.SaveFile(VolumePath.FromString("/boot/claudia_boot.ks"), new FileContent(SpikeScripts.BootListener));
            p.bootFile = "/boot/claudia_boot.ks";
            SpikeLog.Log("addon#" + a.InstanceId + " boot listener installed, bootFile=" + p.bootFile + "; power-cycling");
            p.TogglePower();
            yield return new WaitForSeconds(0.5f);
            p.TogglePower();
        }

        private class CommandPath : InternalPath
        {
            private readonly string command;
            public CommandPath(string command) { this.command = command; }
            public override string Line(int line) { return command; }
            public override string ToString() { return "claudia-spike"; }
        }
    }
}
