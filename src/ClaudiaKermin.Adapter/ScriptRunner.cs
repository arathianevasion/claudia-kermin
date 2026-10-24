// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Starts and stops scripts on kOS processors. The kOS addon (which references this assembly, not the
    /// other way round) registers its bridge in <see cref="ScriptRunner.Current"/> while Flight is loaded.
    /// </summary>
    public interface IScriptRunner
    {
        /// <summary>
        /// Runs <paramref name="source"/> on the processor in part <paramref name="partId"/>. Exactly one of
        /// <paramref name="onRefused"/> or <paramref name="onOutcome"/> is called later, on the main thread.
        /// </summary>
        void BeginRun(uint partId, string label, string source, Action<RunOutcome> onOutcome, Action<string> onRefused);

        /// <summary>
        /// A Mission's run, with the core's run id: the runtime gates (ADR 0003) are keyed on it while it runs.
        /// If a gate trips, <paramref name="onBlocked"/> gets what the script tried (on the main thread) before
        /// <paramref name="onOutcome"/> reports the stopped run.
        /// </summary>
        void BeginRun(uint partId, int runId, string label, string source, Action<RunOutcome> onOutcome, Action<string> onRefused, Action<string> onBlocked);

        void Abort(uint partId, string reason);

        /// <summary>Aborts the program on that processor only if it is still run <paramref name="runId"/>.</summary>
        void Abort(uint partId, int runId, string reason);
    }

    public static class ScriptRunner
    {
        /// <summary>The kOS bridge in the loaded Flight scene, or null (no Flight, or kOS missing).</summary>
        public static IScriptRunner Current;
    }
}
