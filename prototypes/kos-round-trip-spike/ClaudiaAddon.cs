// PROTOTYPE: throwaway kOS round-trip spike. Answers kOS round-trip spike (issue #18).
using System.Collections.Generic;
using System.Linq;
using System.Text;
using kOS;
using kOS.AddOns;
using kOS.Module;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Execution;
using kOS.Safe.Utilities;
using UnityEngine;

namespace ClaudiaSpike
{
    public static class SpikeLog
    {
        public static readonly List<string> Lines = new List<string>();

        public static void Log(string msg)
        {
            string line = string.Format("[UT {0:F0}] {1}", Planetarium.GetUniversalTime(), msg);
            UnityEngine.Debug.Log("[ClaudiaSpike] " + line);
            Lines.Add(line);
            if (Lines.Count > 300) Lines.RemoveAt(0);
        }
    }

    // kOS constructs one instance per processor (per SharedObjects). The spike logs every
    // construction so dock / undock / staging / scene reload behaviour is visible.
    [kOSAddon("CLAUDIA")]
    [KOSNomenclature("ClaudiaSpikeAddon")]
    public class ClaudiaAddon : kOS.Suffixed.Addon, IPopContextNotifyee
    {
        public static readonly List<ClaudiaAddon> All = new List<ClaudiaAddon>();
        private static int nextId;

        public readonly int InstanceId;
        public string RunLabel = "";
        public bool RunActive;
        public bool Reported;
        public string LastOutcome = "";

        public ClaudiaAddon(SharedObjects shared) : base(shared)
        {
            InstanceId = ++nextId;
            All.Add(this);
            AddSuffix("REPORT", new OneArgsSuffix<Lexicon>(Report, "Report a Claudia script result"));
            AddSuffix("DONE", new OneArgsSuffix<StringValue>(Done, "Boot listener: a script returned"));
            AddSuffix("INSTANCE", new Suffix<ScalarValue>(() => InstanceId));
            SpikeLog.Log(string.Format("addon#{0} CREATED {1}", InstanceId, Describe()));
        }

        public SharedObjects Shared { get { return shared; } }
        public kOSProcessor Processor { get { return shared == null ? null : shared.Processor as kOSProcessor; } }
        public bool IsStale { get { return Processor == null || shared.Cpu == null; } }

        public string Describe()
        {
            if (IsStale) return "(processor destroyed)";
            return string.Format("core={0} tag='{1}' part={2} vessel='{3}' mode={4}",
                shared.Processor.KOSCoreId, shared.Processor.Tag, shared.KSPPart.flightID,
                shared.Vessel == null ? "null" : shared.Vessel.vesselName, Processor.ProcessorMode);
        }

        public override BooleanValue Available() { return true; }

        public void BeginRun(string label)
        {
            RunLabel = label;
            RunActive = true;
            Reported = false;
            LastOutcome = "running";
            shared.Cpu.AddPopContextNotifyee(this);
            SpikeLog.Log(string.Format("addon#{0} START {1} on {2}", InstanceId, label, Describe()));
        }

        private void Report(Lexicon result)
        {
            Reported = true;
            LastOutcome = "REPORTED";
            SpikeLog.Log(string.Format("addon#{0} REPORT run='{1}' {2}\n  result: {3}", InstanceId, RunLabel, Describe(), Format(result)));
        }

        private void Done(StringValue path)
        {
            SpikeLog.Log(string.Format("addon#{0} DONE (boot listener) path={1} reported={2}", InstanceId, path, Reported));
            if (RunActive && !Reported) Finish("FAILED: returned without report");
            RunActive = false;
        }

        // Called by the CPU after a program context ends (normally, by error, or by abort).
        public bool OnPopContext(IProgramContext context)
        {
            if (RunActive) Finish(Reported ? "OK (context ended after report)" : "FAILED: context ended without report");
            RunActive = false;
            return false;
        }

        // Read by SpikeWindow: terminal tails to capture a moment after a run ends.
        public static readonly List<KeyValuePair<float, ClaudiaAddon>> PendingTails = new List<KeyValuePair<float, ClaudiaAddon>>();

        private void Finish(string outcome)
        {
            LastOutcome = outcome;
            SpikeLog.Log(string.Format("addon#{0} END run='{1}' outcome={2}\n  --- terminal tail at pop ---\n{3}", InstanceId, RunLabel, outcome, ScreenTail(15)));
            PendingTails.Add(new KeyValuePair<float, ClaudiaAddon>(Time.realtimeSinceStartup + 0.5f, this));
        }

        // kOS throws from IsWaitingForCommand() until the processor's first Update().
        public string IdleState()
        {
            if (IsStale) return "-";
            try { return shared.Interpreter.IsWaitingForCommand() ? "idle" : "busy"; }
            catch (System.ArgumentOutOfRangeException) { return "not booted"; }
        }

        public string ScreenTail(int n)
        {
            if (IsStale) return "";
            // Lines are NUL-padded char arrays; Unity's log truncates at the first NUL.
            var lines = shared.Screen.GetBuffer().Select(l => l.ToString().Replace('\0', ' ').TrimEnd()).Where(l => l.Length > 0).ToList();
            return string.Join("\n", lines.Skip(System.Math.Max(0, lines.Count - n)).Select(l => "  | " + l).ToArray());
        }

        private static string Format(Structure s)
        {
            var lex = s as Lexicon;
            if (lex == null) return s == null ? "null" : s.ToString();
            var sb = new StringBuilder("{");
            foreach (var kv in lex) sb.AppendFormat(" {0}={1};", kv.Key, Format(kv.Value));
            return sb.Append(" }").ToString();
        }
    }
}
