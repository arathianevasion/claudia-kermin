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
        void Start(uint partId, string label, string source, Action<RunOutcome> onOutcome, Action<string> onRefused);

        void Abort(uint partId, string reason);
    }

    public static class ScriptRunner
    {
        /// <summary>The kOS bridge in the loaded Flight scene, or null (no Flight, or kOS missing).</summary>
        public static IScriptRunner Current;
    }
}
