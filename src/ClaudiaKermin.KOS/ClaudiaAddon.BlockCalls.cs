// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Diagnostics;
using System.Globalization;
using ClaudiaKermin.Adapter;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Tools;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using Debug = UnityEngine.Debug;

namespace ClaudiaKermin.KOS
{
    /// <summary>
    /// The block-internal calls (ADR 0006, ADR 0007): the mod's calculations a Building Block makes while it flies, on the same C#
    /// as the Planning Tools (<see cref="BlockCalls"/>). The Script Check lets only the Core Library call them; a Mission Script
    /// passes a block its goal instead. Each reads a fresh Game Snapshot of the processor's vessel (TURNTIME reads only its torque
    /// and moment of inertia) and never changes the game.
    /// </summary>
    public partial class ClaudiaAddon
    {
        private void AddBlockCallSuffixes()
        {
            AddSuffix("LANDINGSITE", new OneArgsSuffix<Lexicon, Lexicon>(LandingSite,
                "Core Library only (land): find_landing_site's search on the body orbited; gives the first site as lexicon error, lat, lng, biome, ..."));
            AddSuffix("DEORBITBURN", new OneArgsSuffix<Lexicon, Lexicon>(DeorbitBurn,
                "Core Library only (deorbit): the deorbit burn that lands on lat, lng by the reentry predictor; lexicon error, burn_ut, prograde, normal, radial, dv, lat, lng, error_km, ...; leave_out (part UIDs) flies the craft without them"));
            AddSuffix("REENTRYIMPACT", new OneArgsSuffix<Lexicon, Lexicon>(ReentryImpact,
                "Core Library only (deorbit, return): where the orbit as it is now, or the one given as orbit_sma, orbit_ecc, ..., comes down, held retrograde; lexicon error, lat, lng, landing_ut, error_km"));
            AddSuffix("TURNTIME", new OneArgsSuffix<Lexicon, Lexicon>(TurnTime,
                "Core Library only (a block that turns to a burn): the time to leave for a 190 degree turn on the wheels and RCS (if the craft has monoprop, on or off), given max_stopping_time; lexicon error, turn_time, turn_s, top_rate, and wheels_turn_time, wheels_turn_s on the wheels alone (-1: no wheel torque)"));
            AddSuffix("EJECTIONBURN", new OneArgsSuffix<Lexicon, Lexicon>(EjectionBurn,
                "Core Library only (transfer_to): the cheapest ejection to target (a moon's name) arriving at target_pe, within one synodic period from earliest_ut or window (to latest_ut, refusing over max_dv); lexicon error, ut, prograde, normal, radial, dv, encounter_ut, arrival_ut, target_pe_actual, ..."));
        }

        /// <summary>
        /// The one wrapper every block-internal call goes through (#439): runs <paramref name="call"/>, logs its name and wall-clock
        /// time with <paramref name="describe"/>'s account of the result (so block tests can read it back from KSP.log), and turns an
        /// exception into <c>lexicon("error", reason)</c> so the block fails with <c>reason</c> instead of the kOS program ending.
        /// Core returns expected failures as an <c>error</c>; an exception here is a bug, logged with its stack.
        /// </summary>
        internal static Structure Guarded(string name, Func<Structure> call, Func<string> describe)
        {
            var watch = Stopwatch.StartNew();
            Structure result;
            try
            {
                result = call();
            }
            catch (Exception e)
            {
                Debug.LogError(ModInfo.LogTag + " block call " + name + " failed after " + Ms(watch) + " ms: " + e);
                var error = new Lexicon();
                error.Add(new StringValue("error"), new StringValue(name + " failed: " + e.Message));
                return error;
            }
            // The log line is only a diagnostic: a failure writing it must not fail a call that worked.
            var took = Ms(watch);
            try
            {
                Debug.Log(ModInfo.LogTag + " block call " + name + " took " + took + " ms: " + (describe == null ? "done" : describe()));
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " block call " + name + " took " + took + " ms; its log line failed: " + e.Message);
            }
            return result;
        }

        private Lexicon EjectionBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> node = null;
            // The search's time, against the minutes the old RSVP search took, and the node.
            return (Lexicon)Guarded("EJECTIONBURN", () =>
            {
                node = BlockCalls.EjectionBurn(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel));
                return KosValues.FromPlain(node);
            }, () => node["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "node at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00}, radial {3:0.00} m/s ({4:0.0} m/s); Pe {5:0} m at UT {6:0}; searched {7:0} s from UT {8:0.0}",
                    node["ut"], node["prograde"], node["normal"], node["radial"], node["dv"], node["target_pe_actual"], node["arrival_ut"], node["synodic"], node["search_from"])
                : (string)node["error"]);
        }

        private Lexicon TurnTime(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> turn = null;
            TurnCraft craft = null;
            // The prediction, so block tests can read it back from KSP.log against the turn the block flew.
            return (Lexicon)Guarded("TURNTIME", () =>
            {
                craft = TurnCraftReader.Read(shared.Vessel);
                turn = BlockCalls.TurnTime(KosValues.ToDictionary(input), craft);
                return KosValues.FromPlain(turn);
            }, () => string.Format(CultureInfo.InvariantCulture,
                "{0}; wheels {1:0.##}/{2:0.##} kN m, RCS {3:0.##}/{4:0.##} kN m{5}, MOI {6:0.##}/{7:0.##} t m2 (pitch/yaw)",
                turn["error"] as string == ""
                    ? string.Format(CultureInfo.InvariantCulture, "turn_time {0:0.0} s, turn {1:0.0} s at up to {2:0.0} deg/s; wheels alone {3}",
                        turn["turn_time"], turn["turn_s"], turn["top_rate"], (double)turn["wheels_turn_time"] < 0 ? "no torque"
                            : string.Format(CultureInfo.InvariantCulture, "turn_time {0:0.0} s, turn {1:0.0} s", turn["wheels_turn_time"], turn["wheels_turn_s"]))
                    : turn["error"],
                craft.WheelPitch, craft.WheelYaw, craft.RcsPitch, craft.RcsYaw, craft.RcsUsable ? "" : " (no monoprop)", craft.MoiPitch, craft.MoiYaw));
        }

        private Lexicon DeorbitBurn(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> found = null;
            return (Lexicon)Guarded("DEORBITBURN", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                found = BlockCalls.DeorbitBurn(ask, SnapshotReader.Read(vessel), new PlanningBody(),
                    new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask)));
                return KosValues.FromPlain(found);
            }, () => Landing(found));
        }

        private Lexicon ReentryImpact(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> landing = null;
            return (Lexicon)Guarded("REENTRYIMPACT", () =>
            {
                var vessel = shared.Vessel;
                var ask = KosValues.ToDictionary(input);
                landing = BlockCalls.ReentryImpact(ask, SnapshotReader.Read(vessel), new PlanningBody(),
                    new ReentryAir(vessel.mainBody), new ReentryCraft(vessel, BlockCalls.LeftOut(ask)));
                return KosValues.FromPlain(landing);
            }, () => Landing(landing));
        }

        /// <summary>A predicted landing for the log, so predicted against actual can be read back from KSP.log.</summary>
        private static string Landing(System.Collections.Generic.IDictionary<string, object> result)
        {
            var text = result["error"] as string ?? "";
            if (!result.ContainsKey("lat")) return text;
            // The frame the ground was read in (#326): stock's inverse-rotation angle and whether the frame turns with the body.
            return string.Format(CultureInfo.InvariantCulture, "{0}landing {1:0.0000}, {2:0.0000} at UT {3:0}, {4:0.00} km from the target{5}; inverse rotation {6:0.00} deg{7}",
                text == "" ? "" : text + "; ", result["lat"], result["lng"], result["landing_ut"], result["error_km"],
                result.ContainsKey("burn_ut")
                    ? string.Format(CultureInfo.InvariantCulture, "; burn at UT {0:0.0}, prograde {1:0.00}, normal {2:0.00} m/s", result["burn_ut"], result["prograde"], result["normal"])
                    : "",
                Planetarium.InverseRotAngle, FlightGlobals.RefFrameIsRotating ? " (rotating frame)" : "");
        }

        private static string Ms(Stopwatch watch)
        {
            return watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private Lexicon LandingSite(Lexicon input)
        {
            System.Collections.Generic.IDictionary<string, object> site = null;
            // The search runs on the main thread, as kOS does: its time against the frame budget is for tuning it.
            return (Lexicon)Guarded("LANDINGSITE", () =>
            {
                site = BlockCalls.LandingSite(KosValues.ToDictionary(input), SnapshotReader.Read(shared.Vessel), new PlanningBody());
                return KosValues.FromPlain(site);
            }, () => site["error"] as string == ""
                ? string.Format(CultureInfo.InvariantCulture, "{0} at {1}, {2}", site["biome"], site["lat"], site["lng"])
                : (string)site["error"]);
        }
    }
}
