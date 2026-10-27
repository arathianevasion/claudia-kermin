// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>The Second look (#272) for the start Craft Check; the Margin recheck's is Mission Control's.</summary>
    internal sealed partial class CraftCheckFlow
    {
        /// <summary>The check has had its one Second look.</summary>
        private bool _secondLooked;

        /// <summary>The check is on its Second look, for the Capcom strip.</summary>
        public bool SecondLooking
        {
            get { return _secondLooked && _state == CraftCheckState.Running; }
        }

        /// <summary>
        /// A reply that says it is unsure is dropped and the check goes again at high effort with the reason, from the start, if the
        /// Spending Cap the player entered has room for it on top of what the check has cost so far; without room the reply stands
        /// (KSP.log says so). True when the check was sent again. The second reply's doubt is logged only.
        /// </summary>
        private bool SecondLookSent(CancellationTokenSource cancel, CraftCheckCharge charge, StreamResult result)
        {
            if (result == null || result.Status != StreamStatus.Completed) return false;
            var reply = CraftCheckReply.Parse(result.Message.Text);
            if (reply == null || reply.UnsureBecause.Length == 0) return false;
            if (_secondLooked)
            {
                _client.Log("Craft Check second look was unsure too: " + reply.UnsureBecause);
                return false;
            }
            if (!SecondLook.Eligible("craft_check", _request.Effort)) return false;

            var request = CheckRequest(_request.Model, _goal, _toolSnapshot, _remote, reply.UnsureBecause);
            var worstCase = RequestCharge.WorstCase(_config.Prices, request.Model, request);
            var spent = _stepCharges.Sum(c => c.Settlement.Spent + c.Settlement.Held) + charge.Settlement.Spent + charge.Settlement.Held;
            if (spent + worstCase > _spendingCap)
            {
                _client.Log("Craft Check second look skipped: it could pass the Spending Cap ($" + _spendingCap.ToString("0.00", CultureInfo.InvariantCulture)
                    + "). The first reply was unsure because: " + reply.UnsureBecause);
                return false;
            }

            _client.Log("Craft Check second look: the first reply was unsure because: " + reply.UnsureBecause);
            _stepCharges.Add(charge);
            _secondLooked = true;
            _request = request;
            _toolSteps = 0;
            _toolLimitSent = false;
            var next = new CraftCheckCharge { Model = request.Model, Pending = worstCase };
            _background.Run(
                () => _client.Stream(request, update => { }, cancel.Token),
                r => Finished(cancel, next, r),
                error => Finished(cancel, next, null));
            // The check is still running: this only lets Mission Control show "taking a second look".
            _completed();
            return true;
        }
    }
}
