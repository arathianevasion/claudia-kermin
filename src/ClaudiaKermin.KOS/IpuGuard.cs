// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using kOS.Safe.Utilities;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// Applies <see cref="ScriptIpu"/>'s decisions: reads and writes kOS's <c>CONFIG:IPU</c> and keeps the player's value in a
    /// file next to the player config while the IPU is raised, so a crash can't lose it (#347, ADR 0008). The decisions are
    /// the core's; this only does them and logs the line each one returns.
    /// </summary>
    public static class IpuGuard
    {
        private const string SavedFile = "ipu_restore";

        private static ScriptIpu _ipu;

        /// <summary>A Mission Script's program is starting: raise the IPU.</summary>
        public static void RunStarted()
        {
            Guard("raise", () => Apply(Ipu().RunStarted(SafeHouse.Config.InstructionsPerUpdate)));
        }

        /// <summary>A Mission Script's run is over, however it ended: put the player's value back.</summary>
        public static void RunEnded(RunOutcome outcome)
        {
            Guard("put back", () => Apply(Ipu().RunEnded(ScriptIpu.EndOf(outcome))));
        }

        /// <summary>Flight loaded: a player's value a crash left unrestored goes back.</summary>
        public static void Loaded()
        {
            Guard("restore at load", () => Apply(Ipu().Loaded(ReadSaved())));
        }

        private static ScriptIpu Ipu()
        {
            // Read once per session: the player's script_ipu applies from the next game start, like the other config values.
            if (_ipu == null) _ipu = new ScriptIpu(PlayerConfig.LoadOrCreate(ConfigDirectory.ResolveCurrent()).ScriptIpu);
            return _ipu;
        }

        private static void Apply(IpuAction action)
        {
            // The player's value goes to disk before the raise, so no crash can come between the two.
            if (action.Persist.HasValue) File.WriteAllText(SavedPath(), action.Persist.Value.ToString(CultureInfo.InvariantCulture));
            if (action.SetTo.HasValue) SafeHouse.Config.InstructionsPerUpdate = action.SetTo.Value;
            if (action.ClearPersisted && File.Exists(SavedPath())) File.Delete(SavedPath());
            if (!string.IsNullOrEmpty(action.Log)) Debug.Log(ModInfo.LogTag + " " + action.Log);
        }

        private static int? ReadSaved()
        {
            var path = SavedPath();
            int value;
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? (int?)value : null;
        }

        private static string SavedPath()
        {
            return Path.Combine(ConfigDirectory.ResolveCurrent(), SavedFile);
        }

        // The IPU must never stop a run from starting or ending.
        private static void Guard(string what, Action work)
        {
            try { work(); }
            catch (Exception e) { Debug.LogWarning(ModInfo.LogTag + " couldn't " + what + " kOS's IPU: " + e.Message); }
        }
    }
}
