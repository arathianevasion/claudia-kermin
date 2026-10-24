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
        /// <summary>"Couldn't produce a safe script": the Sequence Plan's script failed the Script Check twice. Retry, Retry with a note or Re-plan.</summary>
        UnsafeScript,
        /// <summary>Claudia was killed aboard the craft: the Mission is over, closed by a final Debrief from Mission Control.</summary>
        Lost,
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
        /// <summary>Resume a Mission the loaded save has no snapshot for.</summary>
        Resume,
        /// <summary>After "Couldn't produce a safe script": the reply box sends a note with the retry.</summary>
        RetryWithNote,
        /// <summary>After "Couldn't produce a safe script": ask for a different Sequence Plan.</summary>
        Replan,
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
        /// <summary>A Sequence Plan whose script is a Custom Script (written from scratch, not from Building Blocks): show the badge.</summary>
        public bool CustomScript;
        /// <summary>Why no Building Block fits, shown with the Custom Script badge; "" otherwise.</summary>
        public string CustomScriptReason = "";
        /// <summary>A Debrief's outcome (Nominal, Off-Nominal, Failed); "" otherwise.</summary>
        public string Outcome = "";
        /// <summary>The model that answered, when a fallback model served the reply; "" otherwise.</summary>
        public string AnsweredBy = "";
    }

    public enum CraftCheckState
    {
        /// <summary>No Craft Check has run for this craft.</summary>
        None,
        Running,
        /// <summary>The stock delta-v simulator is off; the Craft Check stopped and asks the player to enable it.</summary>
        NeedsSimulator,
        Done,
        /// <summary>The request failed or Claude's reply didn't fit; the Craft Check can be run again.</summary>
        Error,
    }

    public enum CraftVerdict
    {
        None,
        Pass,
        PassWithWarnings,
        Fail,
    }

    public enum CraftFindingSeverity
    {
        Blocker,
        Warning,
    }

    /// <summary>One Blocker or Warning of a Craft Check, with the fix Claudia suggests.</summary>
    public sealed class CraftFinding
    {
        public string Code = "";
        public CraftFindingSeverity Severity;
        /// <summary>The finding in the player's words, e.g. "No landing legs".</summary>
        public string Title = "";
        public string Explanation = "";
        public string Fix = "";
        /// <summary>Only Warnings can be acknowledged.</summary>
        public bool Acknowledged;
    }

    /// <summary>A kOS processor on the craft that Claudia could use.</summary>
    public sealed class ProcessorOption
    {
        public uint PartId;
        public string Title = "";
        public bool Chosen;
        /// <summary>The boot file set on it; "" for none.</summary>
        public string BootFile = "";
    }

    /// <summary>The Craft Check as the Mission board pins it.</summary>
    public sealed class CraftCheckView
    {
        public CraftCheckState State;
        public CraftVerdict Verdict;
        public string Summary = "";
        /// <summary>The simulator prompt or the error, when <see cref="State"/> is NeedsSimulator or Error.</summary>
        public string Message = "";
        public IList<CraftFinding> Findings = new List<CraftFinding>();
        /// <summary>The craft, the Mission Goal or the processor changed since the result.</summary>
        public bool Stale;
        public IList<ProcessorOption> Processors = new List<ProcessorOption>();
        public uint? ChosenProcessor;
        /// <summary>
        /// Start Mission may be pressed: there is a craft, no Craft Check is in the way, and a current result has
        /// no Blocker and every Warning acknowledged. With no current result, pressing it runs the Craft Check.
        /// </summary>
        public bool StartAllowed;
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
        /// <summary>Why the last Start Mission was turned away before the Craft Check (Claudia Missing for a crewed Mission, no link for a remote one); "" otherwise.</summary>
        public string Refusal = "";
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
        /// <summary>The Craft Check, pinned at the bottom of the Mission board.</summary>
        public CraftCheckView CraftCheck = new CraftCheckView();

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
        /// <summary>The Mission is flown remotely: Claudia is at Mission Control, not aboard.</summary>
        public bool Remote;
        /// <summary>While Paused for EVA science: what the player is to do by hand, then Resume; "" otherwise.</summary>
        public string EvaScience = "";
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
        /// <summary>Leaving Flight now would cut off a running sequence, so the adapter asks the player first.</summary>
        public bool LeavingFlightNeedsConfirmation;

        public IList<LogEntry> Log = new List<LogEntry>();
    }
}
