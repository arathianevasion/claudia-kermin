// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Everything the adapter tells <see cref="MissionControl"/>: player actions, Game Snapshots, game
    /// events, kOS run events and UT. Plain data, built on the main thread.
    /// </summary>
    public abstract class MissionInput
    {
    }

    // ---- Player actions ----

    /// <summary>The Mission start form: the Mission Goal, Spending Cap (USD) and model, which is then locked for the Mission.</summary>
    public sealed class StartMission : MissionInput
    {
        public readonly string Goal;
        public readonly decimal SpendingCap;
        public readonly string Model;

        public StartMission(string goal, decimal spendingCap, string model)
        {
            Goal = goal;
            SpendingCap = spendingCap;
            Model = model;
        }
    }

    /// <summary>The Approve button: an unambiguous Approval of the pending Mission Plan or Sequence Plan.</summary>
    public sealed class Approve : MissionInput
    {
    }

    /// <summary>"Change something", with what the player wants changed.</summary>
    public sealed class ChangeSomething : MissionInput
    {
        public readonly string Text;
        public ChangeSomething(string text) { Text = text; }
    }

    /// <summary>"Ask a question", or a question at any time; it never changes the Mission's state.</summary>
    public sealed class AskQuestion : MissionInput
    {
        public readonly string Text;
        public AskQuestion(string text) { Text = text; }
    }

    /// <summary>Free text typed while a plan awaits Approval; Claude classifies it.</summary>
    public sealed class Reply : MissionInput
    {
        public readonly string Text;
        public Reply(string text) { Text = text; }
    }

    /// <summary>The player's answer to "Approve the pending plan?" after a typed reply.</summary>
    public sealed class ConfirmApproval : MissionInput
    {
        public readonly bool Yes;
        public ConfirmApproval(bool yes) { Yes = yes; }
    }

    /// <summary>Sends the turn that failed again, unchanged.</summary>
    public sealed class Retry : MissionInput
    {
    }

    /// <summary>
    /// End Mission, after the player confirmed: cancels any request in flight, aborts a running script, marks
    /// the Mission ended in its folder (folder and transcript are kept) and frees the start form.
    /// </summary>
    public sealed class EndMission : MissionInput
    {
    }

    /// <summary>The debug capture button: writes the latest Game Snapshot to the save's <c>ClaudiaKermin/snapshots</c> folder.</summary>
    public sealed class CaptureSnapshot : MissionInput
    {
    }

    // ---- Game events ----

    public enum GameScene
    {
        /// <summary>The VAB or SPH.</summary>
        Editor,
        Flight,
        /// <summary>Anywhere else (KSC, Tracking Station, main menu).</summary>
        Other,
    }

    /// <summary>A save was loaded; Mission folders go under <c>&lt;SaveDirectory&gt;/ClaudiaKermin/</c>.</summary>
    public sealed class SaveLoaded : MissionInput
    {
        public readonly string SaveDirectory;
        public SaveLoaded(string saveDirectory) { SaveDirectory = saveDirectory; }
    }

    public sealed class SceneChanged : MissionInput
    {
        public readonly GameScene Scene;
        public SceneChanged(GameScene scene) { Scene = scene; }
    }

    /// <summary>Game time, in seconds of UT.</summary>
    public sealed class UtAdvanced : MissionInput
    {
        public readonly double Ut;
        public UtAdvanced(double ut) { Ut = ut; }
    }

    // ---- kOS run events ----

    /// <summary>The kOS bridge would not start the script (busy or missing processor, kOS incompatible...).</summary>
    public sealed class KosRunRefused : MissionInput
    {
        public readonly string Reason;
        public KosRunRefused(string reason) { Reason = reason; }
    }

    /// <summary>The script started by <see cref="StartScript"/> ended: its report, or why it didn't report, and any error text.</summary>
    public sealed class KosRunEnded : MissionInput
    {
        public readonly RunOutcome Outcome;
        public KosRunEnded(RunOutcome outcome) { Outcome = outcome; }
    }
}
