// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Roster;

namespace ClaudiaKermin.Core.Missions
{
    public sealed class SnapshotPart
    {
        /// <summary>The part's <c>persistentId</c>.</summary>
        public uint Id;
        /// <summary>
        /// The part's <c>craftID</c>: set in the editor and carried through launch (the proto part copies it), unlike
        /// <c>persistentId</c>, which launch may reassign. 0: unknown.
        /// </summary>
        public uint CraftId;
        /// <summary>The parent part's <c>persistentId</c>; null for the root.</summary>
        public uint? ParentId;
        /// <summary>The part's internal name.</summary>
        public string Name = "";
        /// <summary>The part's title as the player sees it.</summary>
        public string Title = "";
        /// <summary>The part has a kOS processor.</summary>
        public bool HasKosProcessor;
        /// <summary>The stage the part is in (its inverse stage number): 0 is the last to go.</summary>
        public int Stage;
        /// <summary>What the part can do, as the adapter reads it: command, crew_seat, probe_core, parachute, landing_leg,
        /// heat_shield, antenna, electric_generator, reaction_wheel, rcs, aero_stabiliser, gimbal, engine, experiment, kos_processor, decoupler.</summary>
        public List<string> Tags = new List<string>();
        public int CrewCapacity;
        /// <summary>An antenna's power; 0 for other parts.</summary>
        public double AntennaPower;
        /// <summary>The boot file set on a kOS processor; "" for none.</summary>
        public string BootFile = "";

        // ---- Child Vessel facts (#100): enough to work out a separated vessel's own figures ----

        /// <summary>Mass with its resources, tonnes.</summary>
        public double MassT;
        /// <summary>Mass without its resources, tonnes; 0 means it carries none (the same as <see cref="MassT"/>).</summary>
        public double DryMassT;
        /// <summary>An engine's thrust in vacuum and at sea level (kN) and vacuum Isp (s); 0 for other parts.</summary>
        public double ThrustVacKn;
        public double ThrustAslKn;
        public double IspVac;
        /// <summary>An engine's Isp against pressure: (atmospheres, seconds) keys of its atmosphere curve; empty when not read.</summary>
        public List<double[]> IspCurve = new List<double[]>();
        /// <summary>The electric charge it can store.</summary>
        public double ChargeMax;
    }

    public sealed class SnapshotCraft
    {
        public string Name = "";
        public double MassTonnes;
        /// <summary>The current stage number.</summary>
        public int Stage;
        /// <summary>Crew seats on the craft.</summary>
        public int Seats;
        /// <summary>The vessel's id (its persistent GUID) in Flight, for the Go to button; "" in the editor.</summary>
        public string VesselId = "";
        public List<SnapshotPart> Parts = new List<SnapshotPart>();
        /// <summary>
        /// The craft's drag flying nose first: (Mach, m²) keys, drag (N) = ½ρv² × the area, stock's multipliers included and
        /// its pseudo-Reynolds curve (1 in the air a launch climbs through) left out. Read once for the whole craft as it
        /// stands; empty when not read (an older recording). Only the recording JSON carries it: Planning Tools read it.
        /// </summary>
        public List<double[]> NoseDrag = new List<double[]>();
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
        /// <summary>Longitude of the ascending node, degrees; with the next four it lets a tool propagate the orbit.</summary>
        public double Lan;
        /// <summary>Argument of periapsis, degrees.</summary>
        public double ArgumentOfPeriapsis;
        /// <summary>Mean anomaly at <see cref="Epoch"/>, radians (KSP's own unit).</summary>
        public double MeanAnomalyAtEpoch;
        /// <summary>The UT the mean anomaly is given at.</summary>
        public double Epoch;
        /// <summary>The body the orbit is round; "" when not read (an older recording).</summary>
        public string Body = "";
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
        /// <summary>Altitude above sea level, m; 0 in the editor.</summary>
        public double Altitude;
        /// <summary>Vertical speed, m/s, negative going down; 0 in the editor.</summary>
        public double VerticalSpeed;
        /// <summary>Null when there is no orbit to read (e.g. in the editor).</summary>
        public SnapshotOrbit Orbit;
        public List<SnapshotResource> Resources = new List<SnapshotResource>();
        /// <summary>The RCS switch on the craft: true on, false off, null when it isn't read (the editor). Claudia plans around it; the mod never changes it.</summary>
        public bool? RcsOn;

        /// <summary>
        /// What the Mission Settings give a Planning Tool for the inputs Claudia leaves out: daylight landing on, and the time cap in
        /// hours. The Mission sets them on the turn's snapshot before a tool runs; they are not part of the snapshot's JSON.
        /// </summary>
        public bool DaylightLandingDefault = true;
        public double TimeCapHours = MissionSettingsValues.BalancedWaitHours;

        // ---- Craft Check facts, computed by the adapter from the game ----

        /// <summary>SANDBOX, SCIENCE_SANDBOX or CAREER.</summary>
        public string GameMode = "";
        /// <summary>The stock delta-v simulator is on, so <see cref="Stages"/> is real.</summary>
        public bool DeltaVSimulator;
        /// <summary>Per-stage figures from the stock simulator.</summary>
        public List<SnapshotStage> Stages = new List<SnapshotStage>();
        /// <summary>The main throttle, 0 to 1; 0 when it isn't read.</summary>
        public double Throttle;
        /// <summary>Some stage's delta-v is unknown: stock reads 0 at a trickle throttle with an engine lit (#234).</summary>
        public bool DeltaVUnreliable
        {
            get { return Stages.Any(s => s.DeltaVUnreliable); }
        }
        public List<SnapshotCrewMember> Crew = new List<SnapshotCrewMember>();

        // ---- Aboard and remote facts (spec stories 41 to 44) ----

        /// <summary>Claudia's standing on the roster: Available, Assigned, Missing or Dead; "" when the adapter didn't read it.</summary>
        public string ClaudiaStanding = "";
        /// <summary>CommNet is on in the save. Off counts as linked.</summary>
        public bool CommNetEnabled = true;
        /// <summary>The craft has a CommNet link to Kerbin. Read as true when CommNet is off.</summary>
        public bool HasLink = true;

        /// <summary>The craft has a link to Kerbin, or the save has no CommNet to lose one.</summary>
        public bool Linked
        {
            get { return !CommNetEnabled || HasLink; }
        }

        /// <summary>Claudia is among the crew aboard (in the editor, in the ship's crew manifest).</summary>
        public bool ClaudiaAboard
        {
            get { return Crew.Any(c => c.Name == ClaudiaIdentity.Name); }
        }

        /// <summary>Crew seats not taken by anyone aboard.</summary>
        public int FreeSeats
        {
            get { return System.Math.Max(0, Craft.Seats - Crew.Count); }
        }

        /// <summary>The crew aboard other than Claudia: they ride as passengers while she flies.</summary>
        public int Passengers
        {
            get { return Crew.Count(c => c.Name != ClaudiaIdentity.Name); }
        }
        public List<SnapshotExperiment> Experiments = new List<SnapshotExperiment>();
        /// <summary>The game's bodies, for the Delta-v Map and thrust over weight.</summary>
        public List<SnapshotBody> Bodies = new List<SnapshotBody>();

        /// <summary>Crew aboard other than Claudia herself and Tourists: the Kerbals who could really fly the craft.</summary>
        public bool HasOtherCrew
        {
            get { return Crew.Any(c => c.Trait != Roster.ClaudiaIdentity.TraitName && c.Trait != "Tourist"); }
        }

        /// <summary>The kOS processor Claudia would use: the one nearest the root. Null if the craft has none.</summary>
        public uint? ProcessorPartId
        {
            get { return ProcessorFor(null); }
        }

        /// <summary>The player's chosen processor if it is still a processor on the craft, otherwise the one nearest the root.</summary>
        public uint? ProcessorFor(uint? preferred)
        {
            return ProcessorChoice.Choose(Craft.Parts.Select(p => new VesselPart(p.Id, p.ParentId, p.HasKosProcessor)).ToList(), preferred);
        }

        /// <summary>The snapshot as JSON, with the game's bodies (the recording format).</summary>
        public string ToJson()
        {
            return ToJson(true);
        }

        /// <summary>
        /// The same; Claudia's turns leave out the bodies (they already carry the Delta-v Map) and the craft's drag table (only
        /// Planning Tools read it).
        /// </summary>
        public string ToJson(bool withBodies)
        {
            var json = new JsonWriter().BeginObject()
                .Field("ut", Ut)
                .Field("situation", Situation)
                .Field("body", Body)
                .Field("mode", GameMode)
                .Field("dv_sim", DeltaVSimulator)
                .Field("throttle", Throttle)
                .Field("claudia_standing", ClaudiaStanding)
                .Field("commnet", CommNetEnabled)
                .Field("link", HasLink)
                .Field("altitude", Altitude)
                .Field("vertical_speed", VerticalSpeed);
            if (RcsOn.HasValue) json.Field("rcs_on", RcsOn.Value);
            if (DeltaVUnreliable)
                json.Field("dv_reading", "unreliable at low throttle: stock delta-v reads 0 while an engine is lit at a trickle throttle, so the stages' delta-v is unknown, not 0");
            json.Name("craft").BeginObject()
                .Field("name", Craft.Name)
                .Field("mass_t", Craft.MassTonnes)
                .Field("stage", Craft.Stage)
                .Field("seats", Craft.Seats);
            if (Craft.VesselId.Length > 0) json.Field("vessel_id", Craft.VesselId);
            json.Name("parts").BeginArray();
            foreach (var part in Craft.Parts)
            {
                json.BeginObject().Field("id", part.Id).Name("parent");
                if (part.ParentId.HasValue) json.Number(part.ParentId.Value);
                else json.Null();
                json.Field("name", part.Name).Field("title", part.Title).Field("kos", part.HasKosProcessor)
                    .Field("stage", part.Stage).Field("seats", part.CrewCapacity).Field("antenna", part.AntennaPower).Field("boot", part.BootFile);
                // Written only when set, so a part's line stays short in Claudia's facts.
                if (part.MassT > 0) json.Field("mass_t", part.MassT);
                if (part.DryMassT > 0 && part.DryMassT != part.MassT) json.Field("dry_t", part.DryMassT);
                if (part.ThrustVacKn > 0) json.Field("thrust_vac_kn", part.ThrustVacKn).Field("thrust_asl_kn", part.ThrustAslKn).Field("isp_vac", part.IspVac);
                if (part.IspCurve.Count > 0)
                {
                    json.Name("isp_curve").BeginArray();
                    foreach (var key in part.IspCurve) json.BeginArray().Number(key[0]).Number(key[1]).EndArray();
                    json.EndArray();
                }
                if (part.ChargeMax > 0) json.Field("ec_max", part.ChargeMax);
                json.Name("tags").BeginArray();
                foreach (var tag in part.Tags) json.String(tag);
                json.EndArray().EndObject();
            }
            json.EndArray();
            if (withBodies && Craft.NoseDrag.Count > 0)
            {
                json.Name("nose_drag").BeginArray();
                foreach (var key in Craft.NoseDrag) json.BeginArray().Number(key[0]).Number(key[1]).EndArray();
                json.EndArray();
            }
            json.EndObject();
            json.Name("stages").BeginArray();
            foreach (var stage in Stages)
            {
                json.BeginObject().Field("stage", stage.Number);
                if (stage.DeltaVUnreliable) json.Name("dv_vac").Null().Name("dv_asl").Null().Field("dv_unreliable", true);
                else json.Field("dv_vac", stage.DeltaVVac).Field("dv_asl", stage.DeltaVAsl);
                json.Field("thrust_vac_kn", stage.ThrustVacKn)
                    .Field("start_mass_t", stage.StartMassT).Field("end_mass_t", stage.EndMassT).EndObject();
            }
            json.EndArray();
            json.Name("crew").BeginArray();
            foreach (var member in Crew) json.BeginObject().Field("name", member.Name).Field("trait", member.Trait).EndObject();
            json.EndArray();
            json.Name("experiments").BeginArray();
            foreach (var experiment in Experiments)
            {
                json.BeginObject().Field("part", experiment.PartId).Field("id", experiment.ExperimentId).Field("title", experiment.Title)
                    .Name("science_left");
                if (experiment.ScienceLeft.HasValue) json.Number(experiment.ScienceLeft.Value);
                else json.Null();
                json.EndObject();
            }
            json.EndArray();
            if (withBodies)
            {
                json.Name("bodies").BeginArray();
                foreach (var body in Bodies)
                    json.BeginObject().Field("name", body.Name).Field("parent", body.Parent).Field("gm", body.Gm).Field("radius", body.Radius)
                        .Field("atmosphere_height", body.AtmosphereHeight).Field("sea_level_atm", body.SeaLevelAtm).Field("sma", body.Sma).Field("inclination", body.Inclination)
                        .Field("soi_radius", body.SoiRadius).Field("rotation_period", body.RotationPeriod)
                        .Field("eccentricity", body.Eccentricity).Field("lan", body.Lan).Field("arg_pe", body.ArgumentOfPeriapsis)
                        .Field("mean_anomaly_at_epoch", body.MeanAnomalyAtEpoch).Field("epoch", body.Epoch).EndObject();
                json.EndArray();
            }
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
                    .Field("lan", Orbit.Lan)
                    .Field("arg_pe", Orbit.ArgumentOfPeriapsis)
                    .Field("mean_anomaly_at_epoch", Orbit.MeanAnomalyAtEpoch)
                    .Field("epoch", Orbit.Epoch)
                    .Field("orbit_body", Orbit.Body)
                    .EndObject();
            }
            json.Name("resources").BeginArray();
            foreach (var resource in Resources)
                json.BeginObject().Field("name", resource.Name).Field("amount", resource.Amount).Field("max", resource.Max).EndObject();
            json.EndArray();
            return json.EndObject().ToString();
        }

        private static List<double[]> Keys(JsonNode keys)
        {
            if (keys == null) return new List<double[]>();
            return keys.Items.Where(k => k.Items.Count >= 2)
                .Select(k => new[] { JsonNode.Double(k.Items[0]) ?? 0, JsonNode.Double(k.Items[1]) ?? 0 }).ToList();
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
                GameMode = JsonNode.Str(root["mode"]) ?? "",
                DeltaVSimulator = JsonNode.Bool(root["dv_sim"]) ?? false,
                Throttle = JsonNode.Double(root["throttle"]) ?? 0,
                ClaudiaStanding = JsonNode.Str(root["claudia_standing"]) ?? "",
                CommNetEnabled = JsonNode.Bool(root["commnet"]) ?? true,
                HasLink = JsonNode.Bool(root["link"]) ?? true,
                Altitude = JsonNode.Double(root["altitude"]) ?? 0,
                VerticalSpeed = JsonNode.Double(root["vertical_speed"]) ?? 0,
                RcsOn = JsonNode.Bool(root["rcs_on"]),
            };
            var craft = root["craft"];
            if (craft != null)
            {
                snapshot.Craft.Name = JsonNode.Str(craft["name"]) ?? "";
                snapshot.Craft.MassTonnes = JsonNode.Double(craft["mass_t"]) ?? 0;
                snapshot.Craft.Stage = (int)(JsonNode.Long(craft["stage"]) ?? 0);
                snapshot.Craft.Seats = (int)(JsonNode.Long(craft["seats"]) ?? 0);
                snapshot.Craft.VesselId = JsonNode.Str(craft["vessel_id"]) ?? "";
                snapshot.Craft.NoseDrag = Keys(craft["nose_drag"]);
                var parts = craft["parts"];
                if (parts != null)
                {
                    foreach (var part in parts.Items)
                    {
                        var parent = JsonNode.Long(part["parent"]);
                        var tags = part["tags"];
                        var mass = JsonNode.Double(part["mass_t"]) ?? 0;
                        snapshot.Craft.Parts.Add(new SnapshotPart
                        {
                            Id = (uint)(JsonNode.Long(part["id"]) ?? 0),
                            ParentId = parent.HasValue ? (uint?)parent.Value : null,
                            Name = JsonNode.Str(part["name"]) ?? "",
                            Title = JsonNode.Str(part["title"]) ?? "",
                            HasKosProcessor = JsonNode.Bool(part["kos"]) ?? false,
                            Stage = (int)(JsonNode.Long(part["stage"]) ?? 0),
                            CrewCapacity = (int)(JsonNode.Long(part["seats"]) ?? 0),
                            AntennaPower = JsonNode.Double(part["antenna"]) ?? 0,
                            BootFile = JsonNode.Str(part["boot"]) ?? "",
                            Tags = tags == null ? new List<string>() : tags.Items.Select(JsonNode.Str).Where(t => t != null).ToList(),
                            MassT = mass,
                            DryMassT = JsonNode.Double(part["dry_t"]) ?? mass,
                            ThrustVacKn = JsonNode.Double(part["thrust_vac_kn"]) ?? 0,
                            ThrustAslKn = JsonNode.Double(part["thrust_asl_kn"]) ?? 0,
                            IspVac = JsonNode.Double(part["isp_vac"]) ?? 0,
                            IspCurve = Keys(part["isp_curve"]),
                            ChargeMax = JsonNode.Double(part["ec_max"]) ?? 0,
                        });
                    }
                }
            }
            var stages = root["stages"];
            if (stages != null)
            {
                foreach (var stage in stages.Items)
                {
                    snapshot.Stages.Add(new SnapshotStage
                    {
                        Number = (int)(JsonNode.Long(stage["stage"]) ?? 0),
                        DeltaVUnreliable = JsonNode.Bool(stage["dv_unreliable"]) ?? false,
                        DeltaVVac = JsonNode.Double(stage["dv_vac"]) ?? 0,
                        DeltaVAsl = JsonNode.Double(stage["dv_asl"]) ?? 0,
                        ThrustVacKn = JsonNode.Double(stage["thrust_vac_kn"]) ?? 0,
                        StartMassT = JsonNode.Double(stage["start_mass_t"]) ?? 0,
                        EndMassT = JsonNode.Double(stage["end_mass_t"]) ?? 0,
                    });
                }
            }
            var crew = root["crew"];
            if (crew != null)
            {
                foreach (var member in crew.Items)
                    snapshot.Crew.Add(new SnapshotCrewMember { Name = JsonNode.Str(member["name"]) ?? "", Trait = JsonNode.Str(member["trait"]) ?? "" });
            }
            var experiments = root["experiments"];
            if (experiments != null)
            {
                foreach (var experiment in experiments.Items)
                {
                    snapshot.Experiments.Add(new SnapshotExperiment
                    {
                        PartId = (uint)(JsonNode.Long(experiment["part"]) ?? 0),
                        ExperimentId = JsonNode.Str(experiment["id"]) ?? "",
                        Title = JsonNode.Str(experiment["title"]) ?? "",
                        ScienceLeft = JsonNode.Double(experiment["science_left"]),
                    });
                }
            }
            var bodies = root["bodies"];
            if (bodies != null)
            {
                foreach (var body in bodies.Items)
                {
                    snapshot.Bodies.Add(new SnapshotBody
                    {
                        Name = JsonNode.Str(body["name"]) ?? "",
                        Parent = JsonNode.Str(body["parent"]) ?? "",
                        Gm = JsonNode.Double(body["gm"]) ?? 0,
                        Radius = JsonNode.Double(body["radius"]) ?? 0,
                        AtmosphereHeight = JsonNode.Double(body["atmosphere_height"]) ?? 0,
                        SeaLevelAtm = JsonNode.Double(body["sea_level_atm"]) ?? 0,
                        Sma = JsonNode.Double(body["sma"]) ?? 0,
                        Inclination = JsonNode.Double(body["inclination"]) ?? 0,
                        SoiRadius = JsonNode.Double(body["soi_radius"]) ?? 0,
                        RotationPeriod = JsonNode.Double(body["rotation_period"]) ?? 0,
                        Eccentricity = JsonNode.Double(body["eccentricity"]) ?? 0,
                        Lan = JsonNode.Double(body["lan"]) ?? 0,
                        ArgumentOfPeriapsis = JsonNode.Double(body["arg_pe"]) ?? 0,
                        MeanAnomalyAtEpoch = JsonNode.Double(body["mean_anomaly_at_epoch"]) ?? 0,
                        Epoch = JsonNode.Double(body["epoch"]) ?? 0,
                    });
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
                    Lan = JsonNode.Double(orbit["lan"]) ?? 0,
                    ArgumentOfPeriapsis = JsonNode.Double(orbit["arg_pe"]) ?? 0,
                    MeanAnomalyAtEpoch = JsonNode.Double(orbit["mean_anomaly_at_epoch"]) ?? 0,
                    Epoch = JsonNode.Double(orbit["epoch"]) ?? 0,
                    Body = JsonNode.Str(orbit["orbit_body"]) ?? "",
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
