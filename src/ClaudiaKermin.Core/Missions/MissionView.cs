// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>Where the Mission is in its loop.</summary>
    public enum MissionStage
    {
        /// <summary>No Mission: the start form is offered.</summary>
        None,
        /// <summary>Started in the VAB/SPH; Claudia plans once the craft is in Flight.</summary>
        WaitingForFlight,
        /// <summary>Claudia is writing the Mission Plan.</summary>
        Planning,
        AwaitingPlanApproval,
        /// <summary>Claudia is writing the next Sequence Plan.</summary>
        Sequencing,
        AwaitingSequenceApproval,
        /// <summary>A Maneuver Sequence's script is running on the craft; ends at the Debrief.</summary>
        Running,
        /// <summary>Claudia is writing the Debrief.</summary>
        Debriefing,
        /// <summary>The Mission Goal is met.</summary>
        Succeeded,
        /// <summary>A Failed Debrief: the Mission Goal can't be reached as stated. (Offering a reduced goal comes later.)</summary>
        Ended,
        /// <summary>Stopped on an error; Retry resumes.</summary>
        Paused,
    }

    public enum QuickReply
    {
        Approve,
        ChangeSomething,
        AskQuestion,
        /// <summary>"Yes, approve" after a typed reply.</summary>
        ConfirmApproval,
        /// <summary>"Not yet" after a typed reply.</summary>
        NotYet,
        Retry,
        /// <summary>Paused at the Spending Cap: asks for a higher cap.</summary>
        RaiseCap,
        /// <summary>Paused at the Spending Cap: End Mission (the adapter asks for confirmation).</summary>
        EndMission,
    }

    public enum LogKind
    {
        Player,
        Claudia,
        /// <summary>A line from the mod (Approval given, script started, errors).</summary>
        Notice,
        MissionPlan,
        SequencePlan,
        Debrief,
    }

    /// <summary>One entry in the log drawer: a chat line, or a card for a plan or Debrief.</summary>
    public sealed class LogEntry
    {
        public LogKind Kind;
        /// <summary>The line, or the card's body.</summary>
        public string Text = "";
        /// <summary>The card's title (plan or sequence name); "" for lines.</summary>
        public string Title = "";
        /// <summary>The card's short "why"; "" for lines.</summary>
        public string Why = "";
        /// <summary>A Sequence Plan's Mission Script, for "Show script"; "" otherwise.</summary>
        public string Script = "";
        /// <summary>A Debrief's outcome (Nominal, Off-Nominal, Failed); "" otherwise.</summary>
        public string Outcome = "";
        /// <summary>The model that answered, when a fallback model served the reply; "" otherwise.</summary>
        public string AnsweredBy = "";
    }

    /// <summary>What the Mission start form offers.</summary>
    public sealed class StartFormView
    {
        public bool CanStart;
        public IList<string> Models = new List<string>();
        public string DefaultModel = "";
        public decimal DefaultSpendingCap;
        /// <summary>Estimated cost of a whole Mission (a Mun landing and return) per model in <see cref="Models"/>, USD.</summary>
        public IDictionary<string, decimal> Estimates = new Dictionary<string, decimal>();
    }

    /// <summary>
    /// Everything the adapter's windows draw: the start form, the Capcom strip and the log drawer.
    /// Rebuilt by <see cref="MissionControl"/> after every input and response; <see cref="Version"/> goes up each time.
    /// </summary>
    public sealed class MissionView
    {
        public int Version;
        public MissionStage Stage;
        public StartFormView StartForm = new StartFormView();

        public string MissionId = "";
        public string Goal = "";
        public string Model = "";
        public decimal SpendingCap;
        /// <summary>Cost so far from real usage, USD. With <see cref="HeldCharges"/> and <see cref="PendingCharge"/> it is what counts against the Spending Cap.</summary>
        public decimal Spent;
        /// <summary>Worst-case charges kept for cancelled and lost requests; never refunded.</summary>
        public decimal HeldCharges;
        /// <summary>The worst case of the request in flight, recorded before sending and replaced by the real usage; 0 when none is.</summary>
        public decimal PendingCharge;
        /// <summary>Spent and held charges have reached 80% of the Spending Cap.</summary>
        public bool CapWarning;

        /// <summary>Claudia's latest line, for the Capcom strip.</summary>
        public string LatestLine = "";
        /// <summary>A request to Claude is in progress.</summary>
        public bool Busy;
        /// <summary>What Claudia is doing while busy ("thinking", "writing"), or "".</summary>
        public string Activity = "";

        /// <summary>The Mission Plan's Maneuver Sequences, for the stepper.</summary>
        public IList<string> PlanSequences = new List<string>();
        /// <summary>Index into <see cref="PlanSequences"/> of the Maneuver Sequence in hand; -1 before the first.</summary>
        public int CurrentSequence = -1;

        /// <summary>While Running, what is running; "" otherwise.</summary>
        public string RunStatus = "";
        /// <summary>Why the Mission is Paused; "" otherwise.</summary>
        public string PauseReason = "";
        /// <summary>The question shown with Yes/Not yet after a typed reply; null otherwise.</summary>
        public string ConfirmPrompt;

        /// <summary>Quick replies for the current state, in display order.</summary>
        public IList<QuickReply> QuickReplies = new List<QuickReply>();
        /// <summary>The player may type free text now (a reply, a change or a question).</summary>
        public bool CanType;
        /// <summary>A Game Snapshot is available to capture.</summary>
        public bool CanCaptureSnapshot;
        /// <summary>A Mission exists that is not over, so End Mission is offered.</summary>
        public bool CanEndMission;

        public IList<LogEntry> Log = new List<LogEntry>();
    }
}
