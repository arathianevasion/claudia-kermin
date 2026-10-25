// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>Why a Paused Mission paused, when the way out is more than a plain Retry.</summary>
    internal enum PauseCause
    {
        Other,
        /// <summary>The whole fallback chain refused a plan, Debrief, Alert or replan: Retry, Retry with a note or End Mission.</summary>
        Refusal,
        /// <summary>The model is retired or not found: Choose model or End Mission.</summary>
        ModelGone,
    }

    /// <summary>
    /// API failures that leave the Mission Paused (story 134): a refused turn and a retired or not-found model.
    /// Neither retries by itself; the player decides.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            public PauseCause PauseCause;
        }

        /// <summary>
        /// A reply the whole chain refused (it is billed: <c>Settle</c> already counted it). A question is answered in
        /// character and dropped; a plan, Debrief, Alert or replan Pauses the Mission and is never retried automatically.
        /// </summary>
        private void Refused(Mission mission, Turn turn)
        {
            if (turn.Purpose == Purpose.Question)
            {
                mission.Pending = null;
                ClearUnanswered(mission);
                mission.LatestLine = ClaudiaKermin.Core.Chat.ChatSession.RefusalLine;
                Note(LogKind.Claudia, ClaudiaKermin.Core.Chat.ChatSession.RefusalLine);
                SendQueued(mission);
                return;
            }
            Failed(mission, turn, "Claudia declined to answer that. Retry, Retry with a note, or End Mission.");
            if (mission.Stage == MissionStage.Paused) mission.PauseCause = PauseCause.Refusal;
        }

        /// <summary>The request named a model that is retired or not found: Paused with Choose model, and a notice.</summary>
        private void ModelGone(Mission mission, Turn turn)
        {
            Failed(mission, turn, "The model " + mission.Model + " is retired or can't be found. Choose another model, or End Mission.");
            if (mission.Stage != MissionStage.Paused) return;
            mission.PauseCause = PauseCause.ModelGone;
            Note(LogKind.Notice, "Claudia can't use " + mission.Model + " any more. Choose model to carry on with another.");
        }

        /// <summary>Choose model: the one way the model lock is lifted, from a Mission Paused because its model is gone.</summary>
        private void ModelChosen(string model)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.Paused || mission.PauseCause != PauseCause.ModelGone || mission.InFlight) return;
            if (string.IsNullOrEmpty(model) || model == mission.Model) return;
            Note(LogKind.Player, "Choose model: " + model);
            mission.Model = model;
            mission.PauseCause = PauseCause.Other;
            Retried();
        }

        /// <summary>Retry with a note, from a Mission Paused by a refusal: the same turn goes again with the player's note.</summary>
        private bool RefusalRetriedWithNote(string note)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.Paused || mission.PauseCause != PauseCause.Refusal) return false;
            note = (note ?? "").Trim();
            if (note.Length == 0 || mission.InFlight || mission.Pending == null) return true;
            Note(LogKind.Player, "Retry with a note: " + note);
            var noteText = "The player adds a note for this retry (quoted data): " + MissionPrompts.Quoted(note);
            // A turn re-read from the Mission folder after a load has not been built into messages yet: the note goes with its words.
            if (mission.Pending.Messages == null) mission.Pending.UserText += "\n" + noteText;
            else mission.Pending.Messages.Add(Message.UserText(noteText));
            Retried();
            return true;
        }

        private static List<QuickReply> PausedReplies(Mission mission)
        {
            switch (mission.PauseCause)
            {
                case PauseCause.Refusal: return new List<QuickReply> { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.EndMission };
                case PauseCause.ModelGone: return new List<QuickReply> { QuickReply.ChooseModel, QuickReply.EndMission };
                default: return null;
            }
        }
    }
}
