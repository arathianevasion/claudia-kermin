// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// What a Planning Tool may ask the live game: terrain, biome and the sun. Only these come live; everything else a tool
    /// reads is the turn's Game Snapshot. The adapter implements it with stock calls on the main thread, within a per-frame
    /// budget, so a call may block until a frame has served it; tools call it from a worker thread.
    /// </summary>
    public interface IPlanningBody
    {
        /// <summary>The terrain's height above sea level at the point, metres; NaN when the body or point can't be read.</summary>
        double TerrainAltitude(string body, double latitude, double longitude);

        /// <summary>The biome's name at the point; "" when the body has none or can't be read.</summary>
        string Biome(string body, double latitude, double longitude);

        /// <summary>The sun's elevation above the horizon at the point at <paramref name="ut"/>, degrees; NaN when it can't be read.</summary>
        double SunElevation(string body, double latitude, double longitude, double ut);

        /// <summary>
        /// Reads the <paramref name="facts"/> asked for at every sample in one go (the adapter serves a batch within its frame
        /// budget, so a search never pays a frame per point). A fact that can't be read stays at its default (NaN, "").
        /// </summary>
        void Survey(string body, IList<SiteSample> samples, SiteFacts facts);

        /// <summary>
        /// The latitude and longitude, degrees, of the surface point under a position <paramref name="x"/>, <paramref name="y"/>,
        /// <paramref name="z"/> from the body's centre, given in the frame orbit elements use (the reference plane, z north),
        /// with the body as it is now. False when it can't be read. A search calls it a few times to learn how that frame sits
        /// on the body, then does the ground track itself.
        /// </summary>
        bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude);
    }

    /// <summary>What <see cref="IPlanningBody.Survey"/> reads at a sample.</summary>
    [Flags]
    public enum SiteFacts
    {
        Altitude = 1,
        Biome = 2,
        Sun = 4,
    }

    /// <summary>One point for <see cref="IPlanningBody.Survey"/>: where and when to ask, and the answers it fills in.</summary>
    public sealed class SiteSample
    {
        public double Latitude;
        public double Longitude;
        /// <summary>The UT the sun is asked at.</summary>
        public double Ut;
        /// <summary>Terrain height above sea level, m.</summary>
        public double Altitude = double.NaN;
        public string Biome = "";
        /// <summary>Degrees above the horizon at <see cref="Ut"/>.</summary>
        public double SunElevation = double.NaN;
    }

    /// <summary>A body interface built on the single reads: what a fake in a test derives from.</summary>
    public abstract class PlanningBodyBase : IPlanningBody
    {
        public abstract double TerrainAltitude(string body, double latitude, double longitude);
        public abstract string Biome(string body, double latitude, double longitude);
        public abstract double SunElevation(string body, double latitude, double longitude, double ut);
        public abstract bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude);

        public virtual void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
        {
            foreach (var sample in samples)
            {
                if ((facts & SiteFacts.Altitude) != 0) sample.Altitude = TerrainAltitude(body, sample.Latitude, sample.Longitude);
                if ((facts & SiteFacts.Biome) != 0) sample.Biome = Biome(body, sample.Latitude, sample.Longitude) ?? "";
                if ((facts & SiteFacts.Sun) != 0) sample.SunElevation = SunElevation(body, sample.Latitude, sample.Longitude, sample.Ut);
            }
        }
    }

    /// <summary>A body interface that can read nothing: the stand-in until the adapter passes the real one, and for tools that never ask it.</summary>
    public sealed class NoPlanningBody : PlanningBodyBase
    {
        public override double TerrainAltitude(string body, double latitude, double longitude) { return double.NaN; }
        public override string Biome(string body, double latitude, double longitude) { return ""; }
        public override double SunElevation(string body, double latitude, double longitude, double ut) { return double.NaN; }
        public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
        {
            latitude = double.NaN;
            longitude = double.NaN;
            return false;
        }
    }

    /// <summary>Which requests a tool is offered on (#265 "Tool loop"): every tool is on Mission turns; only some are on the Craft Check and Margin recheck.</summary>
    public enum ToolOffer
    {
        /// <summary>Mission Plan, revised Mission Plan, Sequence Plan, Debrief, answer and Change turns.</summary>
        Mission,
        /// <summary>The Craft Check and the Margin recheck.</summary>
        CraftCheck,
    }

    /// <summary>One call's answer: the JSON that goes back to Claudia as the tool result, and a short line for the Mission log.</summary>
    public sealed class ToolOutcome
    {
        /// <summary>The result JSON, at most about 1k tokens; <c>{"error": "..."}</c> when the call failed.</summary>
        public string Json = "";
        public bool IsError;
        /// <summary>A few words on the result (or the error's reason), for the Mission log line.</summary>
        public string Summary = "";
    }

    /// <summary>
    /// One Planning Tool: a class with its name, description and schema, run against the turn's Game Snapshot. A new tool is
    /// one class added to <see cref="PlanningTools.Default"/> and one test file; the module does the schema check, the size
    /// limit and turning a throw into an error.
    /// </summary>
    internal interface IPlanningTool
    {
        string Name { get; }
        /// <summary>The tool's description for Claude: what it answers, the Building Block it pairs with, and when not to call it.</summary>
        string Description { get; }
        /// <summary>The input schema (JSON text). Non-strict: the module validates the input against it before <see cref="Run"/>.</summary>
        string InputSchemaJson { get; }
        /// <summary>Also offered on the Craft Check and the Margin recheck.</summary>
        bool OnCraftCheck { get; }
        /// <summary>What Claudia is doing while it runs, in plain words for the Capcom strip ("checking stage performance").</summary>
        string Activity { get; }
        /// <summary>Runs the call on input that has passed the schema. Returns the result, or an error built with <see cref="PlanningJson.Error"/>.</summary>
        ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body);
    }

    /// <summary>
    /// The Planning Tools module (#265): one entry point, <see cref="Run"/>, which takes a tool name, its JSON input, the turn's
    /// Game Snapshot and a body interface and returns JSON. Tools only read (they never change the game; only Building Blocks
    /// fly the craft), never touch the live vessel's orbit, its maneuver nodes or the stock delta-v app, and never throw:
    /// every failure comes back as <c>{"error": "&lt;plain reason&gt;"}</c> so Claudia can recover in the same turn.
    /// </summary>
    public sealed class PlanningTools
    {
        /// <summary>About 1k tokens of JSON at the client's 3–4 characters a token.</summary>
        public const int MaxResultChars = 3500;

        private readonly List<IPlanningTool> _tools;

        internal PlanningTools(IEnumerable<IPlanningTool> tools)
        {
            _tools = tools.ToList();
        }

        /// <summary>Every tool the mod ships, in the fixed order the tools list is sent in (so the cached prefix never changes).</summary>
        public static PlanningTools Default(IScriptCheck scriptCheck = null)
        {
            var tools = new List<IPlanningTool> { new PredictBurn(), new SolveBurn(), new StagePerformance(), new PlanAscent(), new FindLandingSite() };
            if (scriptCheck != null) tools.Add(new CheckScript(scriptCheck));
            return new PlanningTools(tools);
        }

        /// <summary>The tools list for a request kind, in registration order: identical on every request of the kind.</summary>
        public IList<ToolDefinition> Definitions(ToolOffer offer)
        {
            return _tools.Where(t => offer == ToolOffer.Mission || t.OnCraftCheck)
                .Select(t => new ToolDefinition { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson, Strict = false })
                .ToList();
        }

        /// <summary>The tool's plain words for the Capcom strip; "calculating" for a name the module doesn't know.</summary>
        public string Activity(string name)
        {
            var tool = Find(name);
            return tool != null ? tool.Activity : "calculating";
        }

        /// <summary>
        /// The entry point: runs <paramref name="name"/> on <paramref name="inputJson"/> against <paramref name="snapshot"/>. Never
        /// throws. An unknown tool, input that fails the schema, a missing snapshot or a tool's own exception is an error result.
        /// </summary>
        public ToolOutcome Run(string name, string inputJson, GameSnapshot snapshot, IPlanningBody body)
        {
            var tool = Find(name);
            if (tool == null) return PlanningJson.Error("there is no tool named " + PlanningJson.Quote(name ?? ""));
            JsonNode input;
            try
            {
                input = JsonReader.Parse(string.IsNullOrEmpty(inputJson) ? "{}" : inputJson);
            }
            catch (JsonException)
            {
                return PlanningJson.Error("the input is not valid JSON");
            }
            var problem = ToolSchema.Check(JsonReader.Parse(tool.InputSchemaJson), input, "input");
            if (problem != null) return PlanningJson.Error(problem);
            if (snapshot == null) return PlanningJson.Error("there is no Game Snapshot of the craft to calculate from");
            ToolOutcome outcome;
            try
            {
                outcome = tool.Run(input, snapshot, body ?? new NoPlanningBody());
            }
            catch (Exception e)
            {
                // A tool's bug must cost Claudia one result, never the turn (or the game).
                return PlanningJson.Error("the calculation failed: " + e.GetType().Name);
            }
            return PlanningJson.Capped(outcome);
        }

        private IPlanningTool Find(string name)
        {
            return _tools.FirstOrDefault(t => t.Name == name);
        }
    }

    /// <summary>The tool conventions shared by every tool: errors, the size limit, UT with "in N s", and numbers rounded for reading.</summary>
    internal static class PlanningJson
    {
        public static ToolOutcome Error(string reason)
        {
            return new ToolOutcome { Json = new JsonWriter().BeginObject().Field("error", reason).EndObject().ToString(), IsError = true, Summary = reason };
        }

        public static string Quote(string text)
        {
            return new JsonWriter().String(text).ToString();
        }

        /// <summary>
        /// Keeps a result within <see cref="PlanningTools.MaxResultChars"/>: one over it goes back as its first part, as a string,
        /// with a note saying it was cut and to ask for less, so the result is still JSON.
        /// </summary>
        public static ToolOutcome Capped(ToolOutcome outcome)
        {
            if (outcome.Json.Length <= PlanningTools.MaxResultChars) return outcome;
            var note = "truncated: the result was " + outcome.Json.Length.ToString(CultureInfo.InvariantCulture)
                + " characters, over the limit of about 1k tokens; ask for less";
            var keep = PlanningTools.MaxResultChars - note.Length - 64;
            // Escaping can grow the kept text; trim until the whole fits.
            while (true)
            {
                var attempt = new JsonWriter().BeginObject().Field("note", note).Field("partial", outcome.Json.Substring(0, Math.Max(0, keep))).EndObject().ToString();
                if (attempt.Length <= PlanningTools.MaxResultChars || keep <= 0)
                    return new ToolOutcome { Json = attempt, IsError = outcome.IsError, Summary = outcome.Summary + " (truncated)" };
                keep -= attempt.Length - PlanningTools.MaxResultChars;
            }
        }

        /// <summary>A time as absolute UT with "in N s" alongside (<c>"&lt;name&gt;_ut"</c> and <c>"&lt;name&gt;_in"</c>), from the snapshot's UT.</summary>
        public static JsonWriter Time(JsonWriter json, string name, double ut, double now)
        {
            var seconds = Math.Round(ut - now);
            return json.Field(name + "_ut", Math.Round(ut, 1))
                .Field(name + "_in", seconds >= 0 ? "in " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s"
                    : (-seconds).ToString("0", CultureInfo.InvariantCulture) + " s ago");
        }

        /// <summary>A figure rounded to <paramref name="digits"/> decimals, so results stay short.</summary>
        public static double Round(double value, int digits)
        {
            return Math.Round(value, digits, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// The module's own check of a tool's input against its non-strict schema: the subset tools use (object with properties,
    /// required and additionalProperties false; string with enum; number, integer, boolean; arrays with items and min/max items).
    /// </summary>
    internal static class ToolSchema
    {
        /// <summary>Why <paramref name="value"/> doesn't fit <paramref name="schema"/>, naming the field; null when it does.</summary>
        public static string Check(JsonNode schema, JsonNode value, string path)
        {
            var type = JsonNode.Str(schema["type"]);
            switch (type)
            {
                case "object":
                    if (value.Kind != JsonKind.Object) return path + " must be an object";
                    var properties = schema["properties"];
                    var required = schema["required"];
                    if (required != null)
                        foreach (var name in required.Items.Select(JsonNode.Str))
                            if (value[name] == null) return path + "." + name + " is required";
                    foreach (var member in value.Members)
                    {
                        var property = properties != null ? properties[member.Key] : null;
                        if (property == null)
                        {
                            if (JsonNode.Bool(schema["additionalProperties"]) == false) return path + "." + member.Key + " is not a field this tool takes";
                            continue;
                        }
                        var problem = Check(property, member.Value, path + "." + member.Key);
                        if (problem != null) return problem;
                    }
                    return null;
                case "string":
                    if (value.Kind != JsonKind.String) return path + " must be a string";
                    var choices = schema["enum"];
                    if (choices != null && !choices.Items.Any(c => JsonNode.Str(c) == value.StringValue))
                        return path + " must be one of " + string.Join(", ", choices.Items.Select(c => JsonNode.Str(c)).ToArray());
                    return null;
                case "number":
                    return value.Kind == JsonKind.Number ? null : path + " must be a number";
                case "integer":
                    var number = JsonNode.Double(value);
                    return value.Kind == JsonKind.Number && number.HasValue && Math.Floor(number.Value) == number.Value ? null : path + " must be a whole number";
                case "boolean":
                    return value.Kind == JsonKind.Bool ? null : path + " must be true or false";
                case "array":
                    if (value.Kind != JsonKind.Array) return path + " must be a list";
                    var min = JsonNode.Long(schema["minItems"]);
                    var max = JsonNode.Long(schema["maxItems"]);
                    if (min.HasValue && value.Items.Count < min.Value) return path + " needs at least " + min.Value.ToString(CultureInfo.InvariantCulture) + " items";
                    if (max.HasValue && value.Items.Count > max.Value) return path + " takes at most " + max.Value.ToString(CultureInfo.InvariantCulture) + " items";
                    var items = schema["items"];
                    if (items == null) return null;
                    for (var i = 0; i < value.Items.Count; i++)
                    {
                        var problem = Check(items, value.Items[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                        if (problem != null) return problem;
                    }
                    return null;
                default:
                    return null;
            }
        }
    }
}
