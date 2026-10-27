// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>How a Mission Script's run ended, as far as the IPU is concerned. Every one puts the player's value back.</summary>
    public enum IpuEnd
    {
        Ended,
        Interrupted,
        ManualControl,
    }

    /// <summary>What the adapter should do to kOS's <c>CONFIG:IPU</c> and to the saved copy of the player's value.</summary>
    public struct IpuAction
    {
        /// <summary>Set <c>CONFIG:IPU</c> to this; null leaves it alone.</summary>
        public int? SetTo;
        /// <summary>Write the player's value to disk so a crash can't lose it; null writes nothing.</summary>
        public int? Persist;
        /// <summary>Delete the saved value: the player's value is back.</summary>
        public bool ClearPersisted;
        /// <summary>One line for the log, empty when nothing happens.</summary>
        public string Log;
    }

    /// <summary>
    /// The mod raises kOS's IPU while a Mission Script is Running and puts the player's value back (#347, ADR 0008). This
    /// holds the decisions only; the adapter reads and writes <c>CONFIG:IPU</c> and the saved copy. Runs may overlap (two
    /// processors), so the value goes back only when the last one ends.
    /// </summary>
    public sealed class ScriptIpu
    {
        public const int Min = 50;
        public const int Max = 2000;

        private readonly int _scriptIpu;
        private int _running;
        private int _playersValue;

        public ScriptIpu(int scriptIpu)
        {
            _scriptIpu = Clamp(scriptIpu);
        }

        /// <summary>kOS's range for the IPU.</summary>
        public static int Clamp(int ipu)
        {
            return ipu < Min ? Min : ipu > Max ? Max : ipu;
        }

        /// <summary>A Mission Script began Running; <paramref name="currentIpu"/> is kOS's IPU now (the player's, unless a run is already raising it).</summary>
        public IpuAction RunStarted(int currentIpu)
        {
            _running++;
            if (_running > 1) return new IpuAction();
            _playersValue = currentIpu;
            return new IpuAction
            {
                SetTo = _scriptIpu,
                Persist = currentIpu,
                Log = "kOS IPU raised to " + _scriptIpu + " for the script (player's value " + currentIpu + ")",
            };
        }

        /// <summary>A Mission Script's run is over, however it ended: the script finished, it was Interrupted or aborted, or the player took Manual Control.</summary>
        public IpuAction RunEnded(IpuEnd how)
        {
            if (_running == 0) return new IpuAction();
            _running--;
            if (_running > 0) return new IpuAction();
            return new IpuAction
            {
                SetTo = _playersValue,
                ClearPersisted = true,
                Log = "kOS IPU put back to " + _playersValue + " (" + Describe(how) + ")",
            };
        }

        /// <summary>
        /// The game loaded and nothing is running; <paramref name="persisted"/> is the saved player's value, if one was left
        /// (a crash or a quit between a start and its end). Puts it back.
        /// </summary>
        public IpuAction Loaded(int? persisted)
        {
            if (!persisted.HasValue || _running > 0) return new IpuAction();
            return new IpuAction
            {
                SetTo = persisted.Value,
                ClearPersisted = true,
                Log = "kOS IPU put back to " + persisted.Value + " (a crash left it raised)",
            };
        }

        private static string Describe(IpuEnd how)
        {
            switch (how)
            {
                case IpuEnd.Interrupted: return "script interrupted";
                case IpuEnd.ManualControl: return "player took Manual Control";
                default: return "script ended";
            }
        }
    }
}
