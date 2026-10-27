// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Diagnostics;
using ClaudiaKermin.Adapter;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Tools;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Exceptions;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The test-only bridge to Planning Tools (ADR 0006): <c>ADDONS:CLAUDIA:PLANNINGTOOL(name, input)</c> runs a tool for a
    /// tests/tools scenario so it can fly the answer and log predicted against actual. It works only while the debug block test
    /// runner is running a test, and only for that test's processor (<see cref="ToolTestGate"/>); any other call is a kOS error.
    /// The Script Check refuses it in Mission Scripts. Like Claudia's own calls, it reads a fresh Game Snapshot and never changes
    /// the game.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddToolSuffixes()
        {
            AddSuffix("PLANNINGTOOL", new TwoArgsSuffix<Lexicon, StringValue, Lexicon>(PlanningTool,
                "Block tests only: run a Planning Tool on a lexicon input; gives its JSON result as a lexicon, plus call_ms and snapshot_ms"));
        }

        private Lexicon PlanningTool(StringValue name, Lexicon input)
        {
            var refusal = ToolTestGate.Shared.Refusal(shared.KSPPart == null ? 0 : shared.KSPPart.persistentId);
            if (refusal != null) throw new KOSException(refusal);
            var tool = name.ToString();
            var inputJson = ToolTestGate.InputJson(KosValues.ToDictionary(input));

            var watch = Stopwatch.StartNew();
            var snapshot = SnapshotReader.Read(shared.Vessel);
            var snapshotMs = watch.Elapsed.TotalMilliseconds;
            watch.Reset();
            watch.Start();
            var outcome = PlanningTools.Default().Run(tool, inputJson, snapshot, new NoPlanningBody());
            var callMs = watch.Elapsed.TotalMilliseconds;

            Debug.Log(ModInfo.LogTag + " Planning Tool test: " + tool + " " + inputJson + " -> " + outcome.Json);
            Debug.Log(ModInfo.LogTag + " " + ToolTestGate.TimingLine(tool, snapshotMs, callMs));
            var result = (Lexicon)KosValues.FromPlain(ToolTestGate.ResultValues(outcome.Json));
            result.Add(new StringValue("call_ms"), ScalarValue.Create(callMs));
            result.Add(new StringValue("snapshot_ms"), ScalarValue.Create(snapshotMs));
            return result;
        }
    }
}
