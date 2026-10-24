// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Runs the mod stops itself (ADR 0003): a runtime gate trip, which the kOS bridge reports with
    /// <see cref="KosRunBlocked"/> after it has stopped the program and made the craft safe, and the runaway
    /// hard stop (<see cref="RunawayStop"/>), which this side times in UT and carries out with an
    /// <see cref="AbortScript"/>. Either way the run's Debrief gives the reason.
    /// </summary>
    public sealed partial class MissionControl
    {
        private void StartRunawayTimer(Mission mission, SequencePlan plan)
        {
            mission.StopReason = null;
            mission.RunDeadlineUt = RunawayStop.Deadline(_ut, plan.ExpectedDurationSeconds, _config.RunawayMultiplier);
        }

        /// <summary>After every input: a run still going at its deadline is aborted (with make-safe) and Debriefed as "timed out".</summary>
        private void CheckRunaway()
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || !mission.RunId.HasValue || !mission.RunningProcessor.HasValue) return;
            if (!mission.RunDeadlineUt.HasValue || _ut < mission.RunDeadlineUt.Value || mission.StopReason != null) return;
            mission.StopReason = RunawayStop.Reason;
            var planned = mission.PendingSequence != null ? mission.PendingSequence.ExpectedDurationSeconds : 0;
            var text = "The script was still running at " + Percent(_config.RunawayMultiplier) + " of its planned "
                + planned.ToString("0", CultureInfo.InvariantCulture) + " s: the mod stopped it and made the craft safe.";
            Note(LogKind.Notice, text);
            NeedsPlayer(text);
            Event(mission, "timed_out", text);
            _commands.Add(new AbortScript(mission.RunningProcessor.Value, RunawayStop.Reason, mission.RunId.Value));
        }

        private void RunBlocked(KosRunBlocked blocked)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != blocked.RunId || mission.StopReason != null) return;
            mission.StopReason = "blocked: " + blocked.What;
            var text = "A runtime gate stopped the script (" + mission.StopReason + ") and the craft was made safe.";
            Note(LogKind.Notice, text);
            NeedsPlayer(text);
            Event(mission, "blocked", blocked.What);
        }

        /// <summary>The run's outcome as the Debrief sees it: a run the mod stopped itself is Aborted, with the reason.</summary>
        private static RunOutcome StoppedOutcome(Mission mission, RunOutcome outcome)
        {
            if (mission.StopReason == null || outcome == null || outcome.Reason == mission.StopReason) return outcome;
            var kind = outcome.Kind == RunOutcomeKind.Finished ? RunOutcomeKind.Finished : RunOutcomeKind.Aborted;
            return new RunOutcome(kind, outcome.Report, outcome.ErrorText, mission.StopReason, true);
        }

        /// <summary>For the Debrief card's title, then forgotten: " (blocked: …)", " (timed out)" or "".</summary>
        private static string TakeStopReason(Mission mission)
        {
            var reason = mission.StopReason;
            mission.StopReason = null;
            return reason == null ? "" : " (" + reason + ")";
        }

        private static string Percent(double multiplier)
        {
            return (multiplier * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
