// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Reflection;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using UnityEngine;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// What the warp trace (<see cref="WarpTrace"/>) reads off KSP: the warp mode, the rate tables, and why KSP refuses or
    /// lowers a rails rate. KSP keeps its reasons in the private <c>TimeWarp.getMaxOnRailsRateIdx</c>, which only reads the
    /// game (it writes one private cache field), so it is called by reflection (docs/research/ksp-rails-warp-refusals.md on
    /// the research branch, #300). If it isn't there, refusals are left blank and the trace still records the rest.
    /// </summary>
    internal static class WarpTracing
    {
        private delegate int MaxOnRails(TimeWarp warp, int rateIndex, bool lookAhead, out ClearToSaveStatus reason);

        private static MaxOnRails maxOnRails;
        private static bool looked;

        public static string Mode
        {
            get { return TimeWarp.WarpMode == TimeWarp.Modes.LOW ? "physics" : "rails"; }
        }

        public static double Ut
        {
            get { return Planetarium.GetUniversalTime(); }
        }

        public static double Real
        {
            get { return Time.realtimeSinceStartup; }
        }

        /// <summary>The rate at a <c>WARP</c> index in the current mode, or null if the index is off the table.</summary>
        public static double? RateAt(int index)
        {
            var warp = TimeWarp.fetch;
            if (warp == null) return null;
            var rates = TimeWarp.WarpMode == TimeWarp.Modes.LOW ? warp.physicsWarpRates : warp.warpRates;
            return rates != null && index >= 0 && index < rates.Length ? rates[index] : (double?)null;
        }

        /// <summary>The rails index of the rate nearest to <paramref name="rate"/>, as kOS rounds a <c>RATE</c>.</summary>
        public static int IndexNear(double rate)
        {
            var warp = TimeWarp.fetch;
            if (warp == null || warp.warpRates == null) return 1;
            var best = 0;
            for (var i = 1; i < warp.warpRates.Length; i++)
                if (Math.Abs(warp.warpRates[i] - rate) <= Math.Abs(warp.warpRates[best] - rate)) best = i;
            return best;
        }

        /// <summary>
        /// KSP's message if its rails limits would refuse or lower a warp to rateIndex now (1 for a <c>WARPTO</c>, where
        /// KSP picks the rate itself), else "". Physics warp has no such limits.
        /// </summary>
        public static string Refusal(int rateIndex)
        {
            try
            {
                var warp = TimeWarp.fetch;
                var check = Check();
                if (warp == null || check == null || TimeWarp.WarpMode == TimeWarp.Modes.LOW || rateIndex <= 0) return "";
                ClearToSaveStatus reason;
                var allowed = check(warp, rateIndex, true, out reason);
                if (allowed >= rateIndex) return "";
                var rate = RateAt(allowed) ?? 1;
                double? limit = null;
                var vessel = FlightGlobals.ActiveVessel;
                if (vessel != null && vessel.mainBody != null && vessel.mainBody.timeWarpAltitudeLimits != null
                    && allowed + 1 < vessel.mainBody.timeWarpAltitudeLimits.Length)
                    limit = vessel.mainBody.timeWarpAltitudeLimits[allowed + 1];
                return WarpRefusal.Message(reason.ToString(), rate, limit);
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " warp trace couldn't read KSP's warp limits: " + e.Message);
                return "";
            }
        }

        /// <summary>
        /// KSP's rails limits would allow rails warp above 1x now (<c>ADDONS:CLAUDIA:RAILSWARP</c>, #339), so the Core Library's
        /// wait asks for rails warp, not physics warp. True if KSP's check can't be read, as a wait asked for rails before.
        /// </summary>
        public static bool RailsAllowed()
        {
            try
            {
                var warp = TimeWarp.fetch;
                var check = Check();
                if (warp == null || check == null) return true;
                ClearToSaveStatus reason;
                return check(warp, 1, true, out reason) >= 1;
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " couldn't read KSP's rails warp limits: " + e.Message);
                return true;
            }
        }

        private static MaxOnRails Check()
        {
            if (looked) return maxOnRails;
            looked = true;
            var method = typeof(TimeWarp).GetMethod("getMaxOnRailsRateIdx", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(int), typeof(bool), typeof(ClearToSaveStatus).MakeByRefType() }, null);
            if (method == null)
            {
                Debug.LogWarning(ModInfo.LogTag + " warp trace: KSP has no TimeWarp.getMaxOnRailsRateIdx(int, bool, out ClearToSaveStatus); refusals go unrecorded");
                return null;
            }
            maxOnRails = (MaxOnRails)Delegate.CreateDelegate(typeof(MaxOnRails), method);
            return maxOnRails;
        }
    }
}
