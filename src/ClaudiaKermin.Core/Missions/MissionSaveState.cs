// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// What a save file remembers of one Mission, as plain data: <see cref="MissionControl.ExportStates"/>
    /// makes it and <see cref="MissionsRestored"/> hands it back. The adapter's ScenarioModule only turns it
    /// into flat string values (<see cref="ToValues"/>) for a ConfigNode and back (<see cref="FromValues"/>).
    /// Everything else a Mission has (the transcript, plans, Debriefs, scripts, the cost and event log) lives
    /// in the Mission folder, which saves never rewind.
    /// </summary>
    public sealed class MissionSaveState
    {
        public string Id = "";
        /// <summary>The Mission in play; a save holds several Missions, one active, the rest Paused at a sequence boundary. A save from before Missions were several reads as active.</summary>
        public bool Active = true;
        public string Goal = "";
        public string Model = "";
        public MissionStage Stage;
        /// <summary>Where a Paused Mission resumes to.</summary>
        public MissionStage StageBeforePause;
        /// <summary>The Spending Cap in USD. Persisted here; enforcing it is the cost ticket's.</summary>
        public decimal SpendingCap;
        /// <summary>The Mission Settings, by name. Opaque to persistence.</summary>
        public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
        /// <summary>
        /// How many messages the committed conversation held when this was saved. The transcript in the
        /// Mission folder may hold more (turns past a loaded save are kept, never rewound).
        /// </summary>
        public int TranscriptMarker;
        public int SequencesProposed;
        /// <summary>The Maneuver Sequence in hand has already counted towards <see cref="SequencesProposed"/>.</summary>
        public bool SequenceCounted;
        /// <summary>What the Script Check found, while the Mission is at "Couldn't produce a safe script".</summary>
        public readonly List<string> UnsafeProblems = new List<string>();
        public string LatestLine = "";
        public string PauseReason = "";
        /// <summary>Why a Paused Mission paused, when Retry is not the only way out: "Refusal" or "ModelGone"; "" for a plain Pause and in older saves.</summary>
        public string PauseCause = "";
        /// <summary>The Mission's vessel, for the Go to button at a Due Time.</summary>
        public string VesselId = "";
        public string VesselName = "";
        /// <summary>The Due Time of the Sequence Plan in hand has been reached and announced.</summary>
        public bool DueNotified;
        /// <summary>The Mission is remote: Claudia is at Mission Control, not aboard. A save from before this existed reads as crewed.</summary>
        public bool Remote;
        /// <summary>While Paused for EVA science: what the player is to do by hand; "" otherwise.</summary>
        public string EvaScience = "";
        /// <summary>In Manual Control (or its Hand back Debrief): the stretch was planned, so its Debrief is judged against the target.</summary>
        public bool ManualPlanned;
        /// <summary>How Manual Control began, in words for Claudia's Debrief; "" outside Manual Control.</summary>
        public string ManualOrigin = "";
        /// <summary>The player handed back and the Debrief of the manual stretch is not finished: a revised Mission Plan follows it.</summary>
        public bool HandBack;
        /// <summary>The player's optional message with Hand back.</summary>
        public string HandBackMessage = "";
        /// <summary>Why the mod stopped the last run ("blocked: …", "timed out") until its Debrief; null otherwise.</summary>
        public string StopReason;
        /// <summary>Why the running sequence was interrupted (a far switch, say) until its Debrief; null otherwise.</summary>
        public string InterruptReason;
        /// <summary>The original kOS processor's part, which a remote Mission's craft follows; null before Flight or in older saves.</summary>
        public uint? ProcessorPartId;
        /// <summary>A Failed Debrief's reduced Mission Goal awaiting the player's Approval; "" otherwise.</summary>
        public string ReducedGoal = "";
        /// <summary>The Mission's Child Vessels and what the mod knows of each (JSON written by MissionControl); "" for none.</summary>
        public string ChildVessels = "";
        /// <summary>The ids of vessels the mod saw leave the craft (a JSON array written by MissionControl); "" for none.</summary>
        public string LeftTheCraft = "";
        /// <summary>The Telemetry Check crossings of the run since the last Debrief, with their verdicts (JSON written by MissionControl); "" for none or in older saves.</summary>
        public string Crossings = "";
        /// <summary>The current or last run's event log (JSON written by MissionControl); "" for none, and in older saves.</summary>
        public string RunEvents = "";
        /// <summary>Game time the current or last run began, s of UT; 0 in older saves.</summary>
        public double RunStartUt;
        /// <summary>The kOS terminal text of the last run that did not report, until its Debrief card; "" otherwise.</summary>
        public string ScriptError = "";
        /// <summary>The craft docked since the pending plan was offered (what with), so Approve is Re-plan; "" for none.</summary>
        public string Recheck = "";
        /// <summary>Requests sent when the craft docked; a plan from a later request was made with the facts after docking.</summary>
        public int RecheckSince;
        /// <summary>What Claudia is still to be told on her next turn (the docking notice, say); "" for none.</summary>
        public string PendingNotice = "";
        /// <summary>Paused because Claudia could fly no vessel, so Resume is offered.</summary>
        public bool PausedCannotFly;
        public MissionPlan MissionPlan;
        /// <summary>The current Sequence Plan: the one awaiting Approval, running, or just run.</summary>
        public SequencePlan SequencePlan;

        private const string SettingPrefix = "setting.";
        private const string UnsafeProblemPrefix = "unsafeProblem.";

        public IDictionary<string, string> ToValues()
        {
            var values = new Dictionary<string, string>();
            foreach (var pair in RawValues()) values[pair.Key] = Escape(pair.Value);
            return values;
        }

        private IDictionary<string, string> RawValues()
        {
            var values = new Dictionary<string, string>
            {
                { "id", Id },
                { "active", Active ? "true" : "false" },
                { "goal", Goal },
                { "model", Model },
                { "stage", Stage.ToString() },
                { "stageBeforePause", StageBeforePause.ToString() },
                { "spendingCap", SpendingCap.ToString(CultureInfo.InvariantCulture) },
                { "transcriptMarker", TranscriptMarker.ToString(CultureInfo.InvariantCulture) },
                { "sequencesProposed", SequencesProposed.ToString(CultureInfo.InvariantCulture) },
                { "sequenceCounted", SequenceCounted ? "true" : "false" },
                { "latestLine", LatestLine },
                { "pauseReason", PauseReason },
                { "vesselId", VesselId },
                { "vesselName", VesselName },
                { "dueNotified", DueNotified ? "true" : "false" },
                { "remote", Remote ? "true" : "false" },
                { "evaScience", EvaScience },
                { "manualPlanned", ManualPlanned ? "true" : "false" },
                { "manualOrigin", ManualOrigin },
                { "handBack", HandBack ? "true" : "false" },
                { "handBackMessage", HandBackMessage },
                { "reducedGoal", ReducedGoal },
            };
            if (PauseCause.Length > 0) values["pauseCause"] = PauseCause;
            if (StopReason != null) values["stopReason"] = StopReason;
            if (InterruptReason != null) values["interruptReason"] = InterruptReason;
            if (ProcessorPartId.HasValue) values["processorPartId"] = ProcessorPartId.Value.ToString(CultureInfo.InvariantCulture);
            if (ChildVessels.Length > 0) values["childVessels"] = ChildVessels;
            if (LeftTheCraft.Length > 0) values["leftTheCraft"] = LeftTheCraft;
            if (Crossings.Length > 0) values["crossings"] = Crossings;
            if (RunEvents.Length > 0) values["runEvents"] = RunEvents;
            if (RunStartUt != 0) values["runStartUt"] = RunStartUt.ToString("R", CultureInfo.InvariantCulture);
            if (ScriptError.Length > 0) values["scriptError"] = ScriptError;
            if (Recheck.Length > 0) values["recheck"] = Recheck;
            if (RecheckSince != 0) values["recheckSince"] = RecheckSince.ToString(CultureInfo.InvariantCulture);
            if (PendingNotice.Length > 0) values["pendingNotice"] = PendingNotice;
            if (PausedCannotFly) values["pausedCannotFly"] = "true";
            foreach (var setting in Settings) values[SettingPrefix + setting.Key] = setting.Value;
            for (var i = 0; i < UnsafeProblems.Count; i++) values[UnsafeProblemPrefix + i.ToString(CultureInfo.InvariantCulture)] = UnsafeProblems[i];
            if (MissionPlan != null || SequencePlan != null) values["plans"] = WritePlans(MissionPlan, SequencePlan);
            return values;
        }

        /// <summary>The state in <paramref name="values"/>, or null when it holds no Mission id.</summary>
        public static MissionSaveState FromValues(IDictionary<string, string> escaped)
        {
            if (escaped == null) return null;
            var values = new Dictionary<string, string>();
            foreach (var pair in escaped) values[pair.Key] = Unescape(pair.Value);
            return FromRawValues(values);
        }

        private static MissionSaveState FromRawValues(IDictionary<string, string> values)
        {
            if (values == null || !values.ContainsKey("id") || string.IsNullOrEmpty(values["id"])) return null;
            var state = new MissionSaveState
            {
                Id = values["id"],
                Active = Get(values, "active") != "false",
                Goal = Get(values, "goal"),
                Model = Get(values, "model"),
                Stage = ParseStage(Get(values, "stage")),
                StageBeforePause = ParseStage(Get(values, "stageBeforePause")),
                LatestLine = Get(values, "latestLine"),
                PauseReason = Get(values, "pauseReason"),
                PauseCause = Get(values, "pauseCause"),
                VesselId = Get(values, "vesselId"),
                VesselName = Get(values, "vesselName"),
                DueNotified = Get(values, "dueNotified") == "true",
                Remote = Get(values, "remote") == "true",
                EvaScience = Get(values, "evaScience"),
                ManualPlanned = Get(values, "manualPlanned") == "true",
                ManualOrigin = Get(values, "manualOrigin"),
                HandBack = Get(values, "handBack") == "true",
                HandBackMessage = Get(values, "handBackMessage"),
                ReducedGoal = Get(values, "reducedGoal"),
                ChildVessels = Get(values, "childVessels"),
                LeftTheCraft = Get(values, "leftTheCraft"),
                Crossings = Get(values, "crossings"),
                RunEvents = Get(values, "runEvents"),
                ScriptError = Get(values, "scriptError"),
                Recheck = Get(values, "recheck"),
                PendingNotice = Get(values, "pendingNotice"),
                PausedCannotFly = Get(values, "pausedCannotFly") == "true",
            };
            double runStart;
            if (double.TryParse(Get(values, "runStartUt"), NumberStyles.Float, CultureInfo.InvariantCulture, out runStart)) state.RunStartUt = runStart;
            decimal cap;
            if (decimal.TryParse(Get(values, "spendingCap"), NumberStyles.Number, CultureInfo.InvariantCulture, out cap)) state.SpendingCap = cap;
            int number;
            if (int.TryParse(Get(values, "transcriptMarker"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) state.TranscriptMarker = number;
            if (int.TryParse(Get(values, "sequencesProposed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) state.SequencesProposed = number;
            state.SequenceCounted = Get(values, "sequenceCounted") == "true";
            if (int.TryParse(Get(values, "recheckSince"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) state.RecheckSince = number;
            string stopReason;
            if (values.TryGetValue("stopReason", out stopReason)) state.StopReason = stopReason;
            string interruptReason;
            if (values.TryGetValue("interruptReason", out interruptReason)) state.InterruptReason = interruptReason;
            uint processor;
            if (uint.TryParse(Get(values, "processorPartId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out processor)) state.ProcessorPartId = processor;
            foreach (var pair in values)
                if (pair.Key.StartsWith(SettingPrefix, StringComparison.Ordinal)) state.Settings[pair.Key.Substring(SettingPrefix.Length)] = pair.Value;
            // In order: unsafeProblem.0, unsafeProblem.1, ... up to the first one missing.
            string problem;
            for (var i = 0; values.TryGetValue(UnsafeProblemPrefix + i.ToString(CultureInfo.InvariantCulture), out problem); i++) state.UnsafeProblems.Add(problem);
            string plans;
            if (values.TryGetValue("plans", out plans))
            {
                // The plans are stored in the shape of a Mission reply, so the reply parser reads them back.
                var reply = MissionReply.Parse(plans);
                if (reply != null)
                {
                    state.MissionPlan = reply.MissionPlan;
                    state.SequencePlan = reply.SequencePlan;
                }
            }
            return state;
        }

        private static string Escape(string text)
        {
            var escaped = new System.Text.StringBuilder();
            foreach (var c in text ?? "")
            {
                switch (c)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '/': escaped.Append("\\s"); break;
                    case '{': escaped.Append("\\o"); break;
                    case '}': escaped.Append("\\c"); break;
                    default: escaped.Append(c); break;
                }
            }
            return escaped.ToString();
        }

        private static string Unescape(string text)
        {
            var plain = new System.Text.StringBuilder();
            for (var i = 0; i < (text ?? "").Length; i++)
            {
                if (text[i] != '\\' || i + 1 == text.Length)
                {
                    plain.Append(text[i]);
                    continue;
                }
                i++;
                switch (text[i])
                {
                    case 'n': plain.Append('\n'); break;
                    case 'r': plain.Append('\r'); break;
                    case 's': plain.Append('/'); break;
                    case 'o': plain.Append('{'); break;
                    case 'c': plain.Append('}'); break;
                    default: plain.Append(text[i]); break;
                }
            }
            return plain.ToString();
        }

        private static string Get(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) && value != null ? value : "";
        }

        private static MissionStage ParseStage(string name)
        {
            MissionStage stage;
            return Enum.TryParse(name, out stage) ? stage : MissionStage.None;
        }

        /// <summary>The plans as JSON in the structured-output shape of <see cref="MissionReply"/>.</summary>
        private static string WritePlans(MissionPlan mission, SequencePlan sequence)
        {
            var json = new JsonWriter().BeginObject();
            json.Name("mission_plan");
            if (mission == null) json.Null();
            else
            {
                json.BeginObject().Field("why", mission.Why).Name("sequences").BeginArray();
                foreach (var s in mission.Sequences)
                    json.BeginObject().Field("name", s.Name).Field("intent", s.Intent).Field("delta_v", s.DeltaV).Field("timing", s.Timing).Field("due_time_ut", s.DueTimeUt).Field("manual", s.Manual).Field("vessel", s.Vessel).EndObject();
                json.EndArray().Field("total_delta_v", mission.TotalDeltaV).Field("expected_duration", mission.ExpectedDuration)
                    .Field("mission_time_s", mission.MissionTimeSeconds).Field("new_goal", mission.NewGoal).Name("child_vessels").BeginArray();
                foreach (var c in mission.ChildVessels)
                    json.BeginObject().Field("name", c.Name).Field("separation_stage", c.SeparationStage).Field("vessel_id", c.VesselId).EndObject();
                json.EndArray().EndObject();
            }
            json.Name("sequence_plan");
            if (sequence == null) json.Null();
            else
            {
                json.BeginObject().Field("name", sequence.Name).Field("intent", sequence.Intent).Field("why", sequence.Why)
                    .Field("target", sequence.Target).Field("tolerances", sequence.Tolerances)
                    .Field("expected_delta_v", sequence.ExpectedDeltaV).Field("expected_duration_s", sequence.ExpectedDurationSeconds);
                Strings(json, "abort_conditions", sequence.AbortConditions);
                json.Name("telemetry_checks").BeginArray();
                foreach (var check in sequence.TelemetryChecks)
                    json.BeginObject().Field("quantity", TelemetryCheck.Key(check.Quantity)).Field("resource", check.Resource)
                        .Field("direction", check.Below ? "below" : "above").Field("threshold", check.Threshold).Field("why", check.Why).EndObject();
                json.EndArray();
                Strings(json, "risks", sequence.Risks);
                json.Field("custom_script", sequence.CustomScript).Field("custom_script_reason", sequence.CustomScriptReason)
                    .Field("script", sequence.Script).Field("due_time_ut", sequence.DueTimeUt)
                    .Field("window_closes_ut", sequence.WindowClosesUt).Field("late_window_dv", sequence.LateWindowDeltaV).Field("manual", sequence.Manual).Field("vessel", sequence.Vessel).EndObject();
            }
            return json.EndObject().ToString();
        }

        private static void Strings(JsonWriter json, string name, IEnumerable<string> items)
        {
            json.Name(name).BeginArray();
            foreach (var item in items) json.String(item);
            json.EndArray();
        }
    }

    /// <summary>
    /// A save was loaded (or reverted to, or quickloaded) in the game: these are the Mission snapshots it
    /// holds, possibly none. Hand over what the save file had, even when it is the save already in play.
    /// </summary>
    public sealed class MissionsRestored : MissionInput
    {
        public readonly string SaveDirectory;
        public readonly IList<MissionSaveState> States;

        public MissionsRestored(string saveDirectory, IList<MissionSaveState> states)
        {
            SaveDirectory = saveDirectory;
            States = states ?? new List<MissionSaveState>();
        }
    }

    /// <summary>
    /// Resume: a Paused Mission carries on (a Mission folder the loaded save has no snapshot for is offered
    /// this, or End Mission). A fresh Craft Check will run here once it exists.
    /// </summary>
    public sealed class Resume : MissionInput
    {
    }
}
