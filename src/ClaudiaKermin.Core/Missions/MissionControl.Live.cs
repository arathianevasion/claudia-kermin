// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Live supervision (spec: Live supervision; stories 83 to 90 and 125). A normal run makes no Claude calls: the mod
    /// records each Building Block's result, shows progress, and checks the run's Telemetry Checks (Claudia's, with the
    /// config defaults filling the gaps) against every Game Snapshot and UT. A crossed check raises one low-effort Alert;
    /// each check fires once per run, and once more at a follow-up threshold Claudia may give with a carry on. An Abort
    /// recommendation drops warp and calls the player to the Abort button; Claudia never aborts herself. One request per
    /// Mission is in flight at a time: the rest queue, Alerts ahead of questions. Any request sent while Running waits at
    /// the Spending Cap rather than Pausing the Mission (the waiting call, <see cref="WaitForCap"/>).
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>One Telemetry Check as the current run watches it.</summary>
        private sealed class LiveCheck
        {
            public TelemetryCheck Check;
            public bool Fired;
            /// <summary>A carry on's follow-up threshold, armed once the first Alert is judged.</summary>
            public double? FollowUp;
            public bool FollowUpFired;
            /// <summary>The first reading past the threshold, awaiting confirmation (a stock delta-v check); null when none.</summary>
            public double? Pending;
            /// <summary>The same for the follow-up threshold.</summary>
            public double? FollowUpPending;
        }

        /// <summary>A crossed Telemetry Check, for its Alert and the run's Debrief.</summary>
        private sealed class Crossing
        {
            public TelemetryCheck Check;
            public double Threshold;
            public double Value;
            public double ElapsedSeconds;
            public bool FollowUp;
            /// <summary>"" until judged; then "carry on", "Abort recommended" or why it was never judged.</summary>
            public string Verdict = "";
            public string Why = "";

            public string Describe()
            {
                return Check.Name() + " " + Check.Format(Value) + (Check.Below ? " under " : " over ") + Check.Format(Threshold)
                    + (FollowUp ? " (follow-up threshold)" : Check.Default ? " (default check)" : "");
            }
        }

        private sealed partial class Mission
        {
            /// <summary>Turns waiting for the one in hand, in the order they go: Alerts first, questions last.</summary>
            public readonly List<Turn> Queue = new List<Turn>();
            public double RunStartUt;
            public double? RunStartDeltaVLeft;
            public double? RunStartMonopropellant;
            /// <summary>The current run's Telemetry Checks.</summary>
            public readonly List<LiveCheck> Checks = new List<LiveCheck>();
            /// <summary>Crossings since the last Debrief, which lists them.</summary>
            public readonly List<Crossing> Crossings = new List<Crossing>();
            public LiveReadings Readings;
            /// <summary>The snapshot the stock delta-v checks last judged; another input on the same snapshot is not a second reading.</summary>
            public GameSnapshot JudgedSnapshot;
            /// <summary>The Building Block running now, from the kOS bridge; "" between blocks.</summary>
            public string CurrentBlock = "";
            public int BlocksDone;
            /// <summary>Claudia recommends Abort for the current run: what she said, for the view; "" otherwise.</summary>
            public string AbortRecommendation = "";
        }

        // ---- The queue ----

        /// <summary>Queued ahead of everything else: an Alert can't wait behind a question.</summary>
        private static int Priority(Turn turn)
        {
            return turn.Purpose == Purpose.Alert ? 0 : turn.Purpose == Purpose.Question ? 2 : 1;
        }

        private static void Enqueue(Mission mission, Turn turn)
        {
            var at = mission.Queue.FindIndex(t => Priority(t) > Priority(turn));
            if (at < 0) mission.Queue.Add(turn);
            else mission.Queue.Insert(at, turn);
        }

        /// <summary>The next queued turn, now that nothing is in hand; null when none.</summary>
        private static Turn TakeQueued(Mission mission)
        {
            if (mission.Queue.Count == 0) return null;
            var next = mission.Queue[0];
            mission.Queue.RemoveAt(0);
            return next;
        }

        // ---- The run ----

        /// <summary>A run starts: its Telemetry Checks are armed and its baseline taken.</summary>
        private void StartLive(Mission mission, SequencePlan plan)
        {
            mission.RunStartUt = _ut;
            mission.RunStartDeltaVLeft = LiveReadings.DeltaVLeftOf(MissionVesselSnapshot(mission));
            mission.RunStartMonopropellant = LiveReadings.MonopropellantOf(MissionVesselSnapshot(mission));
            mission.Checks.Clear();
            foreach (var check in TelemetryDefaults.Fill(plan, _config)) mission.Checks.Add(new LiveCheck { Check = check });
            mission.Crossings.Clear();
            mission.JudgedSnapshot = null;
            mission.CurrentBlock = "";
            mission.BlocksDone = 0;
            mission.AbortRecommendation = "";
            mission.Readings = Readings(mission);
        }

        /// <summary>
        /// The run is over for the loop (it ended, was aborted for Manual Control, refused or interrupted): Alerts not yet
        /// sent are dropped, since there is nothing left to abort, and their crossings go to the Debrief unjudged. A
        /// question waiting at the cap goes back in the queue, so the run's Debrief goes first and Pauses at the cap as
        /// usual (story 124).
        /// </summary>
        private static void EndLive(Mission mission)
        {
            if (mission.WaitingForCap && !mission.InFlight && mission.Pending != null)
            {
                var waiting = mission.Pending;
                mission.Pending = null;
                mission.WaitingForCap = false;
                mission.CapWaitText = "";
                ClearUnanswered(mission);
                mission.Queue.Insert(0, waiting);
            }
            foreach (var alert in mission.Queue.Where(t => t.Purpose == Purpose.Alert).ToList())
            {
                mission.Queue.Remove(alert);
                if (alert.Crossing != null && alert.Crossing.Verdict.Length == 0) alert.Crossing.Verdict = "not judged: the run ended first";
            }
            mission.Checks.Clear();
            mission.CurrentBlock = "";
            mission.AbortRecommendation = "";
        }

        /// <summary>The latest snapshot if it is of the Mission's vessel in Flight; null otherwise.</summary>
        private GameSnapshot MissionVesselSnapshot(Mission mission)
        {
            if (_snapshot == null || _scene != GameScene.Flight) return null;
            if (mission.VesselId.Length > 0 && _snapshot.Craft.VesselId != mission.VesselId) return null;
            return _snapshot;
        }

        private LiveReadings Readings(Mission mission)
        {
            var plan = mission.PendingSequence;
            return LiveReadings.Read(MissionVesselSnapshot(mission), _ut - mission.RunStartUt, mission.RunStartDeltaVLeft,
                plan != null ? plan.ExpectedDeltaV : 0, LaterDeltaV(mission));
        }

        /// <summary>What the Mission Plan's sequences after the one in hand need, m/s.</summary>
        private static double LaterDeltaV(Mission mission)
        {
            if (mission.Plan == null) return 0;
            return mission.Plan.Sequences.Skip(StepsBegun(mission)).Sum(s => Math.Max(0, s.DeltaV));
        }

        /// <summary>After every input while Running: the readings are refreshed and each check is tested; a crossing raises an Alert.</summary>
        private void CheckTelemetry()
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.Running || !mission.RunId.HasValue) return;
            mission.Readings = Readings(mission);
            var snapshot = MissionVesselSnapshot(mission);
            var fresh = !ReferenceEquals(snapshot, mission.JudgedSnapshot);
            mission.JudgedSnapshot = snapshot;
            foreach (var live in mission.Checks)
            {
                if (!fresh && live.Check.NeedsConfirmation) continue;
                var reading = mission.Readings.Of(live.Check);
                if (!live.Fired)
                {
                    if (Confirmed(mission, live, reading, live.Check.Threshold, ref live.Pending))
                    {
                        live.Fired = true;
                        Crossed(mission, live, live.Check.Threshold, reading.Value, false);
                    }
                }
                else if (live.FollowUp.HasValue && !live.FollowUpFired)
                {
                    if (Confirmed(mission, live, reading, live.FollowUp.Value, ref live.FollowUpPending))
                    {
                        live.FollowUpFired = true;
                        Crossed(mission, live, live.FollowUp.Value, reading.Value, true);
                    }
                }
            }
        }

        /// <summary>
        /// The reading crosses <paramref name="threshold"/> and may raise an Alert. A stock delta-v check needs two crossing
        /// readings in a row, since stock misreads for a snapshot now and then (#381): a null reading neither confirms nor
        /// clears a pending crossing, and a reading that doesn't cross clears it. Both readings go to the Mission's events.
        /// </summary>
        private bool Confirmed(Mission mission, LiveCheck live, double? reading, double threshold, ref double? pending)
        {
            var check = live.Check;
            if (!check.NeedsConfirmation) return check.Crossed(reading, threshold);
            if (!reading.HasValue) return false;
            var crossed = check.Crossed(reading, threshold);
            var first = pending;
            if (crossed && !first.HasValue)
            {
                pending = reading;
                Event(mission, "telemetry_check_pending", check.Name() + " " + check.Format(reading.Value) + (check.Below ? " under " : " over ")
                    + check.Format(threshold) + ": first crossing, awaiting a second. " + StockDeltaVFacts());
                return false;
            }
            pending = null;
            if (first.HasValue)
                Event(mission, crossed ? "telemetry_check_confirmed" : "telemetry_check_cleared",
                    check.Name() + " " + check.Format(first.Value) + " then " + check.Format(reading.Value) + (check.Below ? " under " : " over ")
                    + check.Format(threshold) + (crossed ? ": confirmed. " : ": not confirmed, no Alert. ") + StockDeltaVFacts());
            return crossed;
        }

        /// <summary>The latest snapshot's per-stage delta-v (vacuum, m/s) and throttle, for telling a stock misreading from a real one.</summary>
        private string StockDeltaVFacts()
        {
            var snapshot = _snapshot;
            if (snapshot == null) return "no snapshot.";
            return "Stage delta-v (vac): [" + string.Join(", ", snapshot.Stages.Select(s => s.DeltaVVac.ToString("0.#", CultureInfo.InvariantCulture)))
                + "] m/s; throttle " + snapshot.Throttle.ToString("0.##", CultureInfo.InvariantCulture) + ".";
        }

        private void Crossed(Mission mission, LiveCheck live, double threshold, double value, bool followUp)
        {
            var crossing = new Crossing
            {
                Check = live.Check,
                Threshold = threshold,
                Value = value,
                ElapsedSeconds = mission.Readings.ElapsedSeconds,
                FollowUp = followUp,
            };
            mission.Crossings.Add(crossing);
            if (mission.Blackout)
            {
                RecordedInBlackout(crossing);
                Event(mission, "telemetry_check", crossing.Describe() + " (Blackout: recorded, no Alert sent)");
                return;
            }
            Note(LogKind.Notice, "Telemetry Check crossed: " + crossing.Describe() + ". Claudia is judging it.");
            Event(mission, "telemetry_check", crossing.Describe());
            Begin(new Turn
            {
                Purpose = Purpose.Alert,
                UserText = MissionPrompts.Alert(mission.PendingSequence, crossing.Describe(), crossing.Check.Why) + "\n" + CompactFacts(mission),
                Notice = MissionPrompts.AlertNotice,
                Crossing = crossing,
                Waits = true,
            });
        }

        /// <summary>
        /// Claudia judged an Alert. Carry on leaves a log line (and arms her follow-up threshold); Abort, while the run is
        /// still going, drops warp to 1x and calls the player to the Abort button. She never aborts it herself.
        /// </summary>
        private void Alerted(Mission mission, Turn turn, MissionReply reply)
        {
            var verdict = reply.Alert;
            var crossing = turn.Crossing;
            crossing.Verdict = verdict.Abort ? "Abort recommended" : "carry on";
            crossing.Why = verdict.Why;
            var live = mission.Checks.FirstOrDefault(c => c.Check == crossing.Check);
            if (!verdict.Abort)
            {
                if (live != null && !crossing.FollowUp && verdict.FollowUpThreshold.HasValue) live.FollowUp = verdict.FollowUpThreshold;
                return;
            }
            if (mission.Stage != MissionStage.Running || !mission.RunId.HasValue) return;
            // Abort is disabled in a Blackout (story 103): a verdict that comes back after the link dropped isn't offered.
            // Under Auto Mode it needs no one to offer it to: the approve-in-advance check takes it after this reply.
            if (mission.Blackout && !mission.AutoMode)
            {
                crossing.Verdict = AbortNotOffered;
                return;
            }
            mission.AbortRecommendation = "Claudia recommends Abort: " + crossing.Describe() + ". " + verdict.Why;
            Event(mission, "abort_recommended", crossing.Describe());
            if (mission.AutoMode) return;
            // A moment Claudia needs the player (stories 31 and 87): warp drops and the script holds it at 1x until they decide.
            _commands.Add(new DropWarp());
            _commands.Add(new PostScreenMessage(mission.AbortRecommendation, always: true));
        }

        /// <summary>The Abort button: the run is stopped and the craft made safe, and the mod records that the player aborted it.</summary>
        private void AbortPressed()
        {
            AbortTaken(byAutoMode: false);
        }

        /// <summary>
        /// The run is stopped and the craft made safe: by the player's Abort button, or by Auto Mode on Claudia's Abort
        /// recommendation, which goes ahead in a Blackout too (the player gave it in advance) and with no room check.
        /// </summary>
        private void AbortTaken(bool byAutoMode)
        {
            var mission = _mission;
            // Paused mid-run (an Alert Claudia couldn't judge) the script still flies, so Abort still stops it.
            if (mission == null || !IsRunning(mission) || !mission.RunId.HasValue || !mission.RunningProcessor.HasValue) return;
            if (mission.StopReason != null || (mission.Blackout && !byAutoMode)) return;
            var why = byAutoMode ? "Auto Mode took Claudia's Abort recommendation" : "the player pressed Abort";
            mission.StopReason = byAutoMode ? AbortedByAutoMode : AbortedByThePlayer;
            if (byAutoMode)
            {
                Note(LogKind.Notice, mission.AbortRecommendation);
                Note(LogKind.Notice, "Abort taken by Auto Mode.");
            }
            else Note(LogKind.Player, "Abort.");
            mission.AbortRecommendation = "";
            Event(mission, "aborted", why);
            mission.RunEvents.Add(_ut, RunEventKind.Stopped, why);
            _commands.Add(new AbortScript(mission.RunningProcessor.Value, why, mission.RunId.Value));
        }

        /// <summary>The stop reason of a run the player aborted, on the Debrief's title and in its run report.</summary>
        internal const string AbortedByThePlayer = "aborted by the player";
        /// <summary>The stop reason of a run Auto Mode aborted, on the Debrief's title and in its run report.</summary>
        internal const string AbortedByAutoMode = "aborted by Auto Mode";

        // ---- Building Blocks ----

        private void BlockStarted(KosBlockStarted started)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != started.RunId) return;
            mission.CurrentBlock = started.Block ?? "";
        }

        /// <summary>A block switched RCS: one log row with its reason and figures, and the run's tally (the Debrief reads it).</summary>
        private void RcsSwitched(KosRcsSwitched switched)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != switched.RunId) return;
            string problem;
            var change = RcsSwitch.Parse(switched.Switch, out problem);
            if (change == null) { Note(LogKind.Notice, "RCS switch not logged: " + problem + "."); return; }
            mission.RcsUse.Switched(switched.Ut > 0 ? switched.Ut : _ut, mission.CurrentBlock, change.On);
            Note(LogKind.Notice, change.Line());
        }

        /// <summary>A Building Block's result: one log row, with no Claude call.</summary>
        private void BlockEnded(KosBlockEnded ended)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != ended.RunId) return;
            mission.BlocksDone++;
            mission.CurrentBlock = "";
            var line = BlockLine(ended.Block, ended.Result);
            Note(LogKind.Notice, line);
            if (!BlockSucceeded(ended.Result)) mission.RunEvents.Add(_ut, RunEventKind.BlockAborted, line.TrimEnd('.'));
        }

        /// <summary>"Building Block exec_node: done, 812 m/s used, 63 s." or "Building Block exec_node: failed (abort min_ec_pct: 8%), 40 s.".</summary>
        private static string BlockLine(string block, IDictionary<string, object> result)
        {
            var text = "Building Block " + (string.IsNullOrEmpty(block) ? "?" : block) + ": ";
            object value;
            var success = BlockSucceeded(result);
            var reason = result != null && result.TryGetValue("reason", out value) && value != null ? value.ToString() : "";
            text += success ? "done" : "failed" + (reason.Length > 0 ? " (" + reason + ")" : "");
            var dv = Number(result, "dv_used");
            if (dv.HasValue) text += ", " + dv.Value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s used";
            var duration = Number(result, "duration");
            if (duration.HasValue) text += ", " + duration.Value.ToString("0", CultureInfo.InvariantCulture) + " s";
            return text + ".";
        }

        private static bool BlockSucceeded(IDictionary<string, object> result)
        {
            object value;
            return result != null && result.TryGetValue("success", out value) && value is bool && (bool)value;
        }

        private static double? Number(IDictionary<string, object> result, string key)
        {
            object value;
            if (result == null || !result.TryGetValue(key, out value) || value == null) return null;
            try
            {
                return value is string ? (double?)null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                return null;
            }
        }

        // ---- What Claudia and the player see ----

        /// <summary>The compact craft snapshot a mid-run question or an Alert carries: key numbers only, not the whole Game Snapshot.</summary>
        private string CompactFacts(Mission mission)
        {
            var readings = Readings(mission);
            var snapshot = MissionVesselSnapshot(mission);
            var json = new JsonWriter().BeginObject()
                .Field("ut", Math.Round(_ut, 2))
                .Field("sequence", mission.PendingSequence != null ? mission.PendingSequence.Name : "")
                .Field("block", mission.CurrentBlock)
                .Field("blocks_done", mission.BlocksDone)
                .Field("run_elapsed_s", Math.Round(readings.ElapsedSeconds))
                .Field("planned_s", mission.PendingSequence != null ? mission.PendingSequence.ExpectedDurationSeconds : 0);
            if (snapshot != null)
            {
                json.Field("situation", snapshot.Situation).Field("body", snapshot.Body)
                    .Field("altitude_m", Math.Round(snapshot.Altitude)).Field("vertical_speed_ms", Math.Round(snapshot.VerticalSpeed, 1))
                    .Field("stage", snapshot.Craft.Stage);
                if (snapshot.Orbit != null)
                    json.Field("ap_m", Math.Round(snapshot.Orbit.Apoapsis)).Field("pe_m", Math.Round(snapshot.Orbit.Periapsis))
                        .Field("inc_deg", Math.Round(snapshot.Orbit.Inclination, 2));
            }
            json.Field("dv_used_ms", Rounded(readings.DeltaVUsed)).Field("dv_left_ms", Rounded(readings.DeltaVLeft));
            if (snapshot != null && snapshot.DeltaVUnreliable)
                json.Field("dv_reading", "unreadable now: stock delta-v reads 0 while an engine is lit at a trickle throttle (" + Math.Round(snapshot.Throttle * 100) + "%), not a real 0");
            json.Name("resources_pct").BeginObject();
            foreach (var pair in readings.ResourcePercent) json.Field(pair.Key, Math.Round(pair.Value, 1));
            json.EndObject();
            return "Craft now (compact, JSON data): " + json.EndObject();
        }

        private static double? Rounded(double? value)
        {
            return value.HasValue ? Math.Round(value.Value) : (double?)null;
        }

        /// <summary>The Debrief's list of crossed checks with their mid-run verdicts; "" when none was crossed.</summary>
        private static string CrossingsText(Mission mission)
        {
            if (mission.Crossings.Count == 0) return "";
            return string.Join("; ", mission.Crossings.Select(c => c.Describe() + ": " + (c.Verdict.Length > 0 ? c.Verdict : "not judged yet")
                + (c.Why.Length > 0 ? " (" + c.Why + ")" : "")).ToArray());
        }

        /// <summary>The crossings for the save file, enough for the Debrief to list them and rule out Nominal; "" when none.</summary>
        private static string CrossingsJson(Mission mission)
        {
            if (mission.Crossings.Count == 0) return "";
            var json = new JsonWriter().BeginArray();
            foreach (var c in mission.Crossings)
                json.BeginObject().Field("quantity", TelemetryCheck.Key(c.Check.Quantity)).Field("resource", c.Check.Resource)
                    .Field("below", c.Check.Below).Field("default", c.Check.Default).Field("check_why", c.Check.Why)
                    .Field("threshold", c.Threshold).Field("value", c.Value).Field("elapsed_s", c.ElapsedSeconds)
                    .Field("follow_up", c.FollowUp).Field("verdict", c.Verdict).Field("why", c.Why).EndObject();
            return json.EndArray().ToString();
        }

        private static void RestoreCrossings(Mission mission, string text)
        {
            mission.Crossings.Clear();
            if (string.IsNullOrEmpty(text)) return;
            JsonNode root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonException)
            {
                return;
            }
            foreach (var node in root.Items.Where(n => n.Kind == JsonKind.Object))
            {
                var quantity = TelemetryCheck.Parse(JsonNode.Str(node["quantity"]) ?? "");
                if (!quantity.HasValue) continue;
                mission.Crossings.Add(new Crossing
                {
                    Check = new TelemetryCheck
                    {
                        Quantity = quantity.Value,
                        Resource = JsonNode.Str(node["resource"]) ?? "",
                        Below = JsonNode.Bool(node["below"]) ?? false,
                        Default = JsonNode.Bool(node["default"]) ?? false,
                        Why = JsonNode.Str(node["check_why"]) ?? "",
                    },
                    Threshold = JsonNode.Double(node["threshold"]) ?? 0,
                    Value = JsonNode.Double(node["value"]) ?? 0,
                    ElapsedSeconds = JsonNode.Double(node["elapsed_s"]) ?? 0,
                    FollowUp = JsonNode.Bool(node["follow_up"]) ?? false,
                    Verdict = JsonNode.Str(node["verdict"]) ?? "",
                    Why = JsonNode.Str(node["why"]) ?? "",
                });
            }
        }

        /// <summary>The strip's live progress while Running: no Claude call is needed for it.</summary>
        private RunProgressView Progress(Mission mission)
        {
            var plan = mission.PendingSequence;
            var readings = mission.Readings ?? Readings(mission);
            var progress = new RunProgressView
            {
                Running = true,
                Sequence = plan != null ? plan.Name : "",
                Block = mission.CurrentBlock,
                BlocksDone = mission.BlocksDone,
                ElapsedSeconds = readings.ElapsedSeconds,
                PlannedSeconds = plan != null ? plan.ExpectedDurationSeconds : 0,
                DeltaVUsed = readings.DeltaVUsed,
                PlannedDeltaV = plan != null ? plan.ExpectedDeltaV : 0,
                Apoapsis = readings.Apoapsis,
                Periapsis = readings.Periapsis,
                Altitude = readings.Altitude,
                ChecksCrossed = mission.Crossings.Count,
            };
            double ec;
            if (readings.ResourcePercent.TryGetValue("ElectricCharge", out ec)) progress.ElectricChargePercent = ec;
            if (progress.PlannedSeconds > 0) progress.Fraction = Math.Min(1, progress.ElapsedSeconds / progress.PlannedSeconds);
            return progress;
        }
    }
}
