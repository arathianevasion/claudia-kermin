// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>A kOS processor as the mod sees it when asked to start a script on it.</summary>
    public sealed class ProcessorState
    {
        public bool Found { get; set; }

        /// <summary>kOS's processor mode is READY (not OFF or STARVED of electric charge).</summary>
        public bool Powered { get; set; }

        /// <summary>
        /// The processor has had its first update since Flight loaded. Until then kOS's
        /// <c>Interpreter.IsWaitingForCommand()</c> throws.
        /// </summary>
        public bool HasUpdated { get; set; }

        /// <summary>The interpreter is waiting for a command: no program (the player's, or a boot file) is running.</summary>
        public bool Idle { get; set; }

        /// <summary>The mod already has a program running on this processor.</summary>
        public bool ModRunActive { get; set; }
    }

    public enum StartDecisionKind
    {
        Start,
        /// <summary>Ask again next frame.</summary>
        Wait,
        Refuse,
    }

    public sealed class StartDecision
    {
        public StartDecisionKind Kind { get; private set; }

        /// <summary>Why the start was refused, for the player; "" otherwise.</summary>
        public string Reason { get; private set; }

        public StartDecision(StartDecisionKind kind, string reason)
        {
            Kind = kind;
            Reason = reason ?? "";
        }
    }

    /// <summary>Whether the mod may start a script on a processor now. It never interrupts a running program.</summary>
    public static class RunStart
    {
        /// <summary>How long to wait for a processor's first update before giving up.</summary>
        public const double MaxWaitSeconds = 10;

        public static StartDecision Decide(KosCompatibility kos, ProcessorState processor, double secondsWaited)
        {
            if (!kos.IsCompatible) return Refuse(kos.Message);
            if (!processor.Found) return Refuse("There's no kOS processor to run the script on.");
            if (!processor.Powered) return Refuse("The kOS processor is off or has no power.");
            if (processor.ModRunActive) return Refuse("Claudia already has a script running on this processor.");
            if (!processor.HasUpdated)
            {
                return secondsWaited > MaxWaitSeconds
                    ? Refuse("The kOS processor didn't start within " + MaxWaitSeconds + " s of loading.")
                    : new StartDecision(StartDecisionKind.Wait, "");
            }
            if (!processor.Idle)
                return Refuse("The kOS processor is busy running another program (yours or a boot file), and Claudia won't interrupt it.");
            return new StartDecision(StartDecisionKind.Start, "");
        }

        private static StartDecision Refuse(string reason)
        {
            return new StartDecision(StartDecisionKind.Refuse, reason);
        }
    }
}
