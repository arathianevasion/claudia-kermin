// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Craft Check's tool loop: the check and the Margin recheck offer only the tools marked for them (<c>stage_performance</c>),
    /// with the same step and call limits as a Mission turn. The start check runs its loop here and gates each step on the
    /// Spending Cap the player entered; the Margin recheck's loop is Mission Control's, which charges each step to the Mission.
    /// </summary>
    internal sealed partial class CraftCheckFlow
    {
        internal PlanningTools Tools = PlanningTools.Default();
        internal IPlanningBody Body = new NoPlanningBody();
        internal TimeSpan ToolCallTimeout = ToolStep.CallTimeout;

        /// <summary>The start check's request, kept so each step can append to it; null when no check is running.</summary>
        private MessagesRequest _request;
        private GameSnapshot _toolSnapshot;
        private decimal _spendingCap;
        private int _toolSteps;
        private bool _toolLimitSent;
        /// <summary>The settled charges of the start check's tool steps so far; the final reply's charge carries them.</summary>
        private readonly List<CraftCheckCharge> _stepCharges = new List<CraftCheckCharge>();

        /// <summary>What the check is doing while its tools run, for the Capcom strip; "" otherwise.</summary>
        public string ToolActivity { get; private set; } = "";

        /// <summary>Why the Craft Check or Margin recheck may not call <paramref name="name"/>, or null when it may.</summary>
        public string Refusal(string name)
        {
            return Tools.Definitions(ToolOffer.CraftCheck).Any(t => t.Name == name) ? null : "not available on the Craft Check";
        }

        /// <summary>Runs one step of a Craft Check or Margin recheck loop on a worker; <paramref name="done"/> gets the step (null if it blew up) on the main thread.</summary>
        public void RunTools(AssistantMessage message, GameSnapshot snapshot, bool atLimit, Action<ToolStepResult> done)
        {
            var tools = Tools;
            var body = Body;
            var timeout = ToolCallTimeout;
            _background.Run(() => ToolStep.Run(tools, message, Refusal, atLimit, snapshot, body, timeout), done, error => done(null));
        }

        /// <summary>The plain words for the tools a reply calls, for the Capcom strip.</summary>
        public string ActivityOf(AssistantMessage message)
        {
            var names = message.Content.OfType<ToolUseBlock>().Select(t => Tools.Activity(t.Name)).Distinct().ToArray();
            return names.Length > 0 ? string.Join(" and ", names) : "calculating";
        }

        /// <summary>The start check's reply asked for tools: they run, then the next step goes if the Spending Cap has room.</summary>
        private void ToolsCalled(CancellationTokenSource cancel, CraftCheckCharge charge, AssistantMessage message)
        {
            _stepCharges.Add(charge);
            if (_toolLimitSent)
            {
                FailLoop("Claudia kept calling Planning Tools after the limit of " + ToolStep.MaxSteps + " steps.");
                return;
            }
            var atLimit = _toolSteps >= ToolStep.MaxSteps;
            ToolActivity = ActivityOf(message);
            RunTools(message, _toolSnapshot, atLimit, step => ToolsAnswered(cancel, message, step, atLimit));
        }

        private void ToolsAnswered(CancellationTokenSource cancel, AssistantMessage message, ToolStepResult step, bool atLimit)
        {
            if (_cancel != cancel || _state != CraftCheckState.Running) return;
            ToolActivity = "";
            if (step == null)
            {
                FailLoop("A Planning Tool failed; see KSP.log.");
                return;
            }
            _request.Messages.Add(message.ToMessage());
            _request.Messages.Add(step.Results);
            _toolSteps++;
            if (atLimit) _toolLimitSent = true;
            foreach (var call in step.Calls)
                _client.Log("Craft Check Planning Tool " + call.Name + ": " + (call.IsError ? "error: " : "") + call.Summary);

            var worstCase = RequestCharge.WorstCase(_config.Prices, _request.Model, _request);
            var spent = _stepCharges.Sum(c => c.Settlement.Spent + c.Settlement.Held);
            if (spent + worstCase > _spendingCap)
            {
                FailLoop("The Craft Check's next calculation step could pass the Spending Cap ($" + _spendingCap.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "). Raise the Spending Cap, then run it again.");
                return;
            }
            var next = new CraftCheckCharge { Model = _request.Model, Pending = worstCase };
            var request = _request;
            _background.Run(
                () => _client.Stream(request, update => { }, cancel.Token),
                result => Finished(cancel, next, result),
                error => Finished(cancel, next, null));
        }

        /// <summary>The loop ends without a verdict: what its steps cost goes with the failed check (to KSP.log), as a failed check's cost always does.</summary>
        private void FailLoop(string message)
        {
            ToolActivity = "";
            var last = _stepCharges[_stepCharges.Count - 1];
            _stepCharges.RemoveAt(_stepCharges.Count - 1);
            last.Earlier.AddRange(_stepCharges);
            _stepCharges.Clear();
            _cost = last;
            var cancel = _cancel;
            _cancel = null;
            if (cancel != null) cancel.Dispose();
            Fail(message);
            _completed();
        }

        /// <summary>The loop's state goes with a check that is replaced or reset; charges it settled go to KSP.log.</summary>
        private void ForgetLoop()
        {
            foreach (var charge in _stepCharges) LogCost(charge);
            _stepCharges.Clear();
            _request = null;
            _toolSnapshot = null;
            _toolSteps = 0;
            _toolLimitSent = false;
            ToolActivity = "";
        }
    }
}
