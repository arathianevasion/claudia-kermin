// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    public sealed class SnapshotPart
    {
        /// <summary>The part's <c>persistentId</c>.</summary>
        public uint Id;
        /// <summary>The parent part's <c>persistentId</c>; null for the root.</summary>
        public uint? ParentId;
        /// <summary>The part's internal name.</summary>
        public string Name = "";
        /// <summary>The part's title as the player sees it.</summary>
        public string Title = "";
        /// <summary>The part has a kOS processor.</summary>
        public bool HasKosProcessor;
    }

    public sealed class SnapshotCraft
    {
        public string Name = "";
        public double MassTonnes;
        /// <summary>The current stage number.</summary>
        public int Stage;
        public List<SnapshotPart> Parts = new List<SnapshotPart>();
    }

    /// <summary>Distances in metres (from sea level), angles in degrees, times in seconds.</summary>
    public sealed class SnapshotOrbit
    {
        public double Apoapsis;
        public double Periapsis;
        public double Inclination;
        public double Eccentricity;
        public double Period;
        public double SemiMajorAxis;
    }

    public sealed class SnapshotResource
    {
        public string Name = "";
        public double Amount;
        public double Max;
    }

    /// <summary>
    /// A Game Snapshot: plain facts about the craft (parts, orbit, situation, resources) read by the
    /// adapter on the main thread. The same JSON is what the debug capture button writes and what
    /// recorded test snapshots hold; vessel and part names inside it reach Claude as quoted JSON data.
    /// </summary>
    public sealed class GameSnapshot : MissionInput
    {
        public double Ut;
        /// <summary>KSP's situation name, e.g. PRELAUNCH, ORBITING, LANDED.</summary>
        public string Situation = "";
        /// <summary>The body the craft is at.</summary>
        public string Body = "";
        public SnapshotCraft Craft = new SnapshotCraft();
        /// <summary>Null when there is no orbit to read (e.g. in the editor).</summary>
        public SnapshotOrbit Orbit;
        public List<SnapshotResource> Resources = new List<SnapshotResource>();

        /// <summary>The kOS processor Claudia would use: the one nearest the root. Null if the craft has none.</summary>
        public uint? ProcessorPartId
        {
            get
            {
                return ProcessorChoice.Choose(Craft.Parts.Select(p => new VesselPart(p.Id, p.ParentId, p.HasKosProcessor)).ToList(), null);
            }
        }

        public string ToJson()
        {
            var json = new JsonWriter().BeginObject()
                .Field("ut", Ut)
                .Field("situation", Situation)
                .Field("body", Body);
            json.Name("craft").BeginObject()
                .Field("name", Craft.Name)
                .Field("mass_t", Craft.MassTonnes)
                .Field("stage", Craft.Stage);
            json.Name("parts").BeginArray();
            foreach (var part in Craft.Parts)
            {
                json.BeginObject().Field("id", part.Id).Name("parent");
                if (part.ParentId.HasValue) json.Number(part.ParentId.Value);
                else json.Null();
                json.Field("name", part.Name).Field("title", part.Title).Field("kos", part.HasKosProcessor).EndObject();
            }
            json.EndArray().EndObject();
            json.Name("orbit");
            if (Orbit == null) json.Null();
            else
            {
                json.BeginObject()
                    .Field("apoapsis", Orbit.Apoapsis)
                    .Field("periapsis", Orbit.Periapsis)
                    .Field("inclination", Orbit.Inclination)
                    .Field("eccentricity", Orbit.Eccentricity)
                    .Field("period", Orbit.Period)
                    .Field("semi_major_axis", Orbit.SemiMajorAxis)
                    .EndObject();
            }
            json.Name("resources").BeginArray();
            foreach (var resource in Resources)
                json.BeginObject().Field("name", resource.Name).Field("amount", resource.Amount).Field("max", resource.Max).EndObject();
            json.EndArray();
            return json.EndObject().ToString();
        }

        /// <summary>Reads snapshot JSON; missing fields keep their defaults. Throws <see cref="System.FormatException"/> on bad JSON.</summary>
        public static GameSnapshot Parse(string text)
        {
            JsonNode root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonException e)
            {
                throw new System.FormatException("not a Game Snapshot: " + e.Message);
            }
            var snapshot = new GameSnapshot
            {
                Ut = JsonNode.Double(root["ut"]) ?? 0,
                Situation = JsonNode.Str(root["situation"]) ?? "",
                Body = JsonNode.Str(root["body"]) ?? "",
            };
            var craft = root["craft"];
            if (craft != null)
            {
                snapshot.Craft.Name = JsonNode.Str(craft["name"]) ?? "";
                snapshot.Craft.MassTonnes = JsonNode.Double(craft["mass_t"]) ?? 0;
                snapshot.Craft.Stage = (int)(JsonNode.Long(craft["stage"]) ?? 0);
                var parts = craft["parts"];
                if (parts != null)
                {
                    foreach (var part in parts.Items)
                    {
                        var parent = JsonNode.Long(part["parent"]);
                        snapshot.Craft.Parts.Add(new SnapshotPart
                        {
                            Id = (uint)(JsonNode.Long(part["id"]) ?? 0),
                            ParentId = parent.HasValue ? (uint?)parent.Value : null,
                            Name = JsonNode.Str(part["name"]) ?? "",
                            Title = JsonNode.Str(part["title"]) ?? "",
                            HasKosProcessor = JsonNode.Bool(part["kos"]) ?? false,
                        });
                    }
                }
            }
            var orbit = root["orbit"];
            if (orbit != null && orbit.Kind == JsonKind.Object)
            {
                snapshot.Orbit = new SnapshotOrbit
                {
                    Apoapsis = JsonNode.Double(orbit["apoapsis"]) ?? 0,
                    Periapsis = JsonNode.Double(orbit["periapsis"]) ?? 0,
                    Inclination = JsonNode.Double(orbit["inclination"]) ?? 0,
                    Eccentricity = JsonNode.Double(orbit["eccentricity"]) ?? 0,
                    Period = JsonNode.Double(orbit["period"]) ?? 0,
                    SemiMajorAxis = JsonNode.Double(orbit["semi_major_axis"]) ?? 0,
                };
            }
            var resources = root["resources"];
            if (resources != null)
            {
                foreach (var resource in resources.Items)
                {
                    snapshot.Resources.Add(new SnapshotResource
                    {
                        Name = JsonNode.Str(resource["name"]) ?? "",
                        Amount = JsonNode.Double(resource["amount"]) ?? 0,
                        Max = JsonNode.Double(resource["max"]) ?? 0,
                    });
                }
            }
            return snapshot;
        }
    }
}
