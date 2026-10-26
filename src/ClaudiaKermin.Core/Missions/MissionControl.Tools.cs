// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The tool loop (#265 "Tool loop"): Claudia calls Planning Tools during a Mission turn and plans from their answers. The
    /// loop lives in the pending turn: each step appends her tool_use reply and the tool results to <c>Turn.Messages</c>
    /// exactly, so each request is the last one plus two messages and the cache holds, and the turn joins the conversation once,
    /// with the accepted reply. Every Mission request carries the same tools list (offering never breaks the cache); which tools
    /// a turn may call is said in its notice and enforced here, so a call during an Alert is answered with an error.
    /// </summary>
    public sealed partial class MissionControl
    {
        private PlanningTools _tools = PlanningTools.Default();
        private IPlanningBody _body = new NoPlanningBody();

        /// <summary>The Planning Tools the Mission and its Craft Checks offer; tests replace them.</summary>
        internal PlanningTools Tools
        {
            get { return _tools; }
            set
            {
                _tools = value;
                _craftCheck.Tools = value;
            }
        }

        private TimeSpan _toolCallTimeout = ToolStep.CallTimeout;

        /// <summary>Each tool call's cap; tests shorten it.</summary>
        internal TimeSpan ToolCallTimeout
        {
            get { return _toolCallTimeout; }
            set
            {
                _toolCallTimeout = value;
                _craftCheck.ToolCallTimeout = value;
            }
        }

        /// <summary>The live game's terrain, biome and sun for the tools; until the adapter passes one, tools that need it say so.</summary>
        public IPlanningBody PlanningBody
        {
            set
            {
                _body = value ?? new NoPlanningBody();
                _craftCheck.Body = _body;
            }
        }

        /// <summary>Why a turn may not call <paramref name="tool"/>, or null when it may: no tool during an Alert.</summary>
        private static string ToolRefusal(Purpose purpose, string tool)
        {
            return purpose == Purpose.Alert ? "not available during an Alert" : null;
        }

        /// <summary>
        /// Claudia's reply asked for tools: they run on a worker against the Game Snapshot the turn was built with, while the
        /// Capcom strip names them. At the step limit nothing runs and every result says to reply now; a reply that still calls
        /// tools after that fails the turn, so a loop can never run away.
        /// </summary>
        private void ToolsCalled(Mission mission, Turn turn, AssistantMessage message)
        {
            if (message.FallbackRan)
                Event(mission, "tool_loop_fallback", turn.Purpose + " step " + (turn.ToolSteps + 1) + " was served by " + (message.Model ?? "a fallback model") + "; the loop carries on");
            // A save loaded while this ran: its state comes back (MissionControl.Finished), and the turn is sent again from scratch.
            if (mission.DeferredRestore != null)
            {
                mission.Pending = null;
                return;
            }
            if (turn.ToolLimitSent)
            {
                Failed(mission, turn, "Claudia kept calling Planning Tools after the limit of " + ToolStep.MaxSteps + " steps.");
                return;
            }
            var atLimit = turn.ToolSteps >= ToolStep.MaxSteps;
            var names = message.Content.OfType<ToolUseBlock>().Select(t => _tools.Activity(t.Name)).Distinct().ToArray();
            mission.InFlight = true;
            mission.Activity = names.Length > 0 ? string.Join(" and ", names) : "calculating";
            turn.Cancel = new CancellationTokenSource();
            var tools = _tools;
            var body = _body;
            var snapshot = turn.ToolSnapshot;
            var purpose = turn.Purpose;
            var timeout = _toolCallTimeout;
            _background.Run(
                () => ToolStep.Run(tools, message, name => ToolRefusal(purpose, name), atLimit, snapshot, body, timeout),
                step => ToolsAnswered(mission, turn, message, step, atLimit),
                error => ToolsAnswered(mission, turn, message, null, atLimit));
        }

        private void ToolsAnswered(Mission mission, Turn turn, AssistantMessage message, ToolStepResult step, bool atLimit)
        {
            if (mission.Pending != turn || !mission.InFlight) return;
            mission.InFlight = false;
            mission.Activity = "";
            turn.Cancel.Dispose();
            if (mission != _mission) return;
            try
            {
                if (mission.Stage == MissionStage.Lost || mission.DeferredRestore != null)
                {
                    mission.Pending = null;
                    return;
                }
                if (step == null)
                {
                    Failed(mission, turn, "A Planning Tool failed; see KSP.log.");
                    return;
                }
                // Byte for byte as Claude sent it, then the results: the next request is this one plus these two messages.
                turn.Messages.Add(message.ToMessage());
                turn.Messages.Add(step.Results);
                turn.ToolSteps++;
                if (atLimit) turn.ToolLimitSent = true;
                foreach (var call in step.Calls)
                {
                    Note(LogKind.Notice, "Planning Tool " + call.Name + " (" + Shorten(call.InputJson) + "): " + (call.IsError ? "error: " : "") + call.Summary);
                    Event(mission, "tool", call.Name + " " + call.InputJson + " -> " + (call.IsError ? "error: " : "") + call.Summary);
                }
                // Paused while the tools ran (at the Spending Cap after the step's charge): the loop goes, the turn waits for Retry.
                if (mission.Stage == MissionStage.Paused)
                {
                    DiscardLoop(mission, turn);
                    return;
                }
                NextStep(mission, turn);
            }
            finally
            {
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
            }
        }

        /// <summary>The next step goes only if the Spending Cap has room for its worst case; with none, the loop is discarded and the Mission Pauses (or the call waits, as any waiting turn does).</summary>
        private void NextStep(Mission mission, Turn turn)
        {
            var worst = RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, turn));
            if (mission.Committed + worst <= mission.SpendingCap)
            {
                Send(mission, turn);
                return;
            }
            LogCost(mission, "blocked", worst, "over_cap");
            DiscardLoop(mission, turn);
            if (turn.Waits || IsRunning(mission) || mission.Blackout) WaitForCap(mission);
            else PauseForCap(mission, "Claudia's next calculation step could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). Raise cap to carry on, or End Mission.");
        }

        /// <summary>
        /// Drops a turn's tool loop: the turn goes back to its first form and is sent again from scratch, with the Game Snapshot
        /// of that moment (<see cref="ResendFromScratch"/>). Nothing for a turn that has had no tool step.
        /// </summary>
        private void DiscardLoop(Mission mission, Turn turn)
        {
            if (turn.ToolSteps == 0) return;
            Event(mission, "tool_loop_discarded", turn.Purpose + " after " + turn.ToolSteps + " step" + (turn.ToolSteps == 1 ? "" : "s"));
            ResendFromScratch(mission, turn, null);
        }

        private static string Shorten(string input)
        {
            input = input ?? "";
            return input.Length <= 160 ? input : input.Substring(0, 157) + "...";
        }
    }
}
