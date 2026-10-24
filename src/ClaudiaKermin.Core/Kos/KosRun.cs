// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// How a mod-started kOS program ended. Only Finished means the script reported; every other kind
    /// is a run that failed, was aborted or was interrupted. (Not a Debrief outcome: a Debrief can be
    /// Nominal, Off-Nominal or Failed whatever the run's kind.)
    /// </summary>
    public enum RunOutcomeKind
    {
        /// <summary>The script reported before the program ended.</summary>
        Finished,
        /// <summary>The script errored, or returned without reporting.</summary>
        NoReport,
        /// <summary>The mod stopped the program (<c>BreakExecution</c>) and recorded why.</summary>
        Aborted,
        /// <summary>The player pressed Ctrl+C in the terminal: kOS printed its break message.</summary>
        StoppedByPlayer,
        /// <summary>The program was cut off from outside: a scene change, or the processor was lost.</summary>
        Interrupted,
    }

    public sealed class RunOutcome
    {
        public RunOutcomeKind Kind { get; private set; }

        /// <summary>The script's last <c>ADDONS:CLAUDIA:REPORT</c> lexicon; null unless Finished.</summary>
        public IDictionary<string, object> Report { get; private set; }

        /// <summary>What kOS printed after the run started (a terminal tail), NULs stripped; "" when Finished.</summary>
        public string ErrorText { get; private set; }

        /// <summary>Why the run was aborted or interrupted; "" otherwise.</summary>
        public string Reason { get; private set; }

        /// <summary>The mod aborted the run, even if the script had already reported.</summary>
        public bool WasAborted { get; private set; }

        public RunOutcome(RunOutcomeKind kind, IDictionary<string, object> report, string errorText, string reason, bool wasAborted)
        {
            Kind = kind;
            Report = report;
            ErrorText = errorText ?? "";
            Reason = reason ?? "";
            WasAborted = wasAborted;
        }

        /// <summary>One line for logs and the debug window, e.g. "Aborted (player pressed Abort)".</summary>
        public string Summary
        {
            get { return Kind + (Reason.Length > 0 ? " (" + Reason + ")" : ""); }
        }
    }

    /// <summary>
    /// One mod-started program on one kOS processor, from start to its outcome. The adapter feeds it
    /// kOS events; it decides how the run ended.
    /// </summary>
    public sealed class KosRun
    {
        /// <summary>
        /// What kOS 1.6 prints when a program is broken manually (Ctrl+C or <c>BreakExecution(true)</c>);
        /// a string literal in kOS.Safe 1.6.0.1.
        /// </summary>
        public const string BreakMessage = "Program aborted.";

        private readonly IList<string> _terminalBefore;
        private IDictionary<string, object> _report;
        private string _abortReason;
        private string _interruptReason;

        /// <param name="terminalBefore">The processor's terminal lines when the run started.</param>
        public KosRun(IList<string> terminalBefore)
        {
            _terminalBefore = terminalBefore;
        }

        /// <summary>The script called <c>ADDONS:CLAUDIA:REPORT(lexicon)</c>.</summary>
        public void Report(IDictionary<string, object> report)
        {
            if (!HasEnded) _report = report;
        }

        /// <summary>True once kOS has popped the program context.</summary>
        public bool HasEnded { get; private set; }

        /// <summary>
        /// The mod is stopping the program. Only the mod knows a break was its own, so it records
        /// the reason here before calling <c>BreakExecution</c>.
        /// </summary>
        public void Aborted(string reason)
        {
            _abortReason = reason;
        }

        /// <summary>Something outside the program is ending it (leaving Flight, losing the processor).</summary>
        public void Interrupted(string reason)
        {
            if (_interruptReason == null) _interruptReason = reason;
        }

        /// <summary>kOS popped the program context: the program is over.</summary>
        public void ProgramEnded()
        {
            HasEnded = true;
        }

        /// <summary>
        /// Decides the outcome, given the terminal read a frame after the program ended (kOS prints
        /// the error only after the context pops).
        /// </summary>
        public RunOutcome Finish(IList<string> terminalAfter)
        {
            var aborted = _abortReason != null;
            if (_report != null) return new RunOutcome(RunOutcomeKind.Finished, _report, "", _abortReason, aborted);
            var lines = TerminalText.LinesSince(_terminalBefore, terminalAfter);
            var printed = string.Join("\n", lines);
            if (aborted) return new RunOutcome(RunOutcomeKind.Aborted, null, printed, _abortReason, true);
            if (_interruptReason != null) return new RunOutcome(RunOutcomeKind.Interrupted, null, printed, _interruptReason, false);
            if (lines.Contains(BreakMessage)) return new RunOutcome(RunOutcomeKind.StoppedByPlayer, null, printed, "", false);
            return new RunOutcome(RunOutcomeKind.NoReport, null, printed, "", false);
        }
    }
}
