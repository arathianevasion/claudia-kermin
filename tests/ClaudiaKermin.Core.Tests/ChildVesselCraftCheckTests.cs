// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>A probe released from a mothership, and hand-written Claude replies, for the Child Vessel tests.</summary>
    internal static class Mothership
    {
        public const uint ProbeDecoupler = 3105, ProbeCore = 3106, ProbeEngine = 3107;

        /// <summary>
        /// The on-pad craft (Claudia aboard, her processor in part 3102) with a probe on a decoupler fired in stage 2: an
        /// OKTO core with a kOS processor, a 5 km antenna and 10 EC, and a Spark engine with its fuel.
        /// </summary>
        public static GameSnapshot WithProbe(bool processor = true, bool antenna = true)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.VesselId = "craft";
            var parts = snapshot.Craft.Parts;
            var decoupler = new SnapshotPart { Id = ProbeDecoupler, ParentId = 3101, Name = "radialDecoupler", Title = "TT-38K Radial Decoupler", Stage = 2, MassT = 0.03 };
            decoupler.Tags.Add("decoupler");
            parts.Add(decoupler);
            var core = new SnapshotPart { Id = ProbeCore, ParentId = ProbeDecoupler, Name = "probeCoreOcto", Title = "Probodobodyne OKTO", Stage = 1, MassT = 0.1, HasKosProcessor = processor, ChargeMax = 10 };
            core.Tags.AddRange(new[] { "command", "probe_core" });
            if (processor) core.Tags.Add("kos_processor");
            if (antenna)
            {
                core.Tags.Add("antenna");
                core.AntennaPower = 5000;
            }
            parts.Add(core);
            parts.Add(new SnapshotPart { Id = ProbeEngine, ParentId = ProbeCore, Name = "liquidEngineMini", Title = "48-7S \"Spark\"", Stage = 1, MassT = 0.5, DryMassT = 0.2, ThrustVacKn = 20, ThrustAslKn = 16, IspVac = 320 });
            return snapshot;
        }

        /// <summary>A hand-written reply stream whose text is <paramref name="json"/>.</summary>
        public static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = "event: message_start\n"
                + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_c\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n"
                + "event: content_block_start\n"
                + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\n"
                + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_delta\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":200,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3000,\"output_tokens\":200}]}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse) };
        }

        /// <summary>A Craft Check reply with no findings, planning Child Vessels at <paramref name="stages"/>.</summary>
        public static ScriptedResponse CheckPlanning(params int[] stages)
        {
            return Reply("{\"summary\":\"The mothership can release the probe.\",\"findings\":[],\"child_vessel_stages\":["
                + string.Join(",", stages) + "]}");
        }
    }

    /// <summary>
    /// The Craft Check's per-Child-Vessel facts and the child_vessel_problem Blocker it raises from them (#65, #100),
    /// through the core's seam: a snapshot and Run Craft Check in, the request and the view out.
    /// </summary>
    public sealed class ChildVesselCraftCheckTests : IDisposable
    {
        private const string Goal = "Release the probe into orbit and fly it to the Mun.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private MissionControl Checked(StreamingFakeTransport transport, GameSnapshot craft)
        {
            var client = new ClaudeClient(transport, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(craft);
            mission.Send(new RunCraftCheck(Goal, 10m, "claude-sonnet-5-5"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.CraftCheck.State == CraftCheckState.Running && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
            return mission;
        }

        private static string UserFacts(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
            }
        }

        [Fact]
        public void TheCheckCarriesEachSeparationsOwnFiguresAndAsksWhichAreChildVessels()
        {
            var transport = new StreamingFakeTransport(Mothership.CheckPlanning(2));
            var mission = Checked(transport, Mothership.WithProbe());

            var facts = UserFacts(transport.Requests[0]);
            Assert.Contains("Separations", facts);
            Assert.Contains("\"stage\":2,\"name\":\"Probodobodyne OKTO\"", facts);
            Assert.Contains("\"kos\":true", facts);
            Assert.Contains("\"antenna_power\":5000", facts);
            // 320 s x 9.80665 x ln(0.63 / 0.33).
            Assert.Contains("\"dv_vac\":2029", facts);
            Assert.Contains("\"problems\":[]", facts);
            Assert.Contains("child_vessel_stages", transport.Requests[0].Body);

            Assert.Equal(CraftVerdict.Pass, mission.View.CraftCheck.Verdict);
        }

        [Fact]
        public void APlannedChildVesselWithNoProcessorOrAntennaIsABlockerTheModRaisesItself()
        {
            // Claude names the probe as planned but reports nothing wrong with it.
            var transport = new StreamingFakeTransport(Mothership.CheckPlanning(2));
            var mission = Checked(transport, Mothership.WithProbe(processor: false, antenna: false));

            var check = mission.View.CraftCheck;
            Assert.Equal(CraftVerdict.Fail, check.Verdict);
            var finding = Assert.Single(check.Findings);
            Assert.Equal("child_vessel_problem", finding.Code);
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
            Assert.Contains("Probodobodyne OKTO, separating at stage 2: no kOS processor; no antenna to reach Claudia", finding.Explanation);
            Assert.False(check.StartAllowed);
        }

        [Fact]
        public void AProbeTheMissionGoalDoesNotPlanIsNotJudged()
        {
            var transport = new StreamingFakeTransport(Mothership.CheckPlanning());
            var mission = Checked(transport, Mothership.WithProbe(processor: false, antenna: false));
            Assert.Equal(CraftVerdict.Pass, mission.View.CraftCheck.Verdict);
        }

        [Fact]
        public void APlannedStageWithNothingToFlyIsABlocker()
        {
            var transport = new StreamingFakeTransport(Mothership.CheckPlanning(5));
            var mission = Checked(transport, Mothership.WithProbe());
            var finding = Assert.Single(mission.View.CraftCheck.Findings);
            Assert.Equal("child_vessel_problem", finding.Code);
            Assert.Contains("nothing that could be a Child Vessel separates at stage 5", finding.Explanation);
        }

        [Fact]
        public void AStagingPlanThatStrandsClaudiaWithoutAProcessorIsABlockerWhateverIsPlanned()
        {
            // Her only processor is on the probe: releasing it leaves her pod with none.
            var craft = Mothership.WithProbe();
            craft.Craft.Parts.RemoveAll(p => p.Id == 3102);
            craft.Craft.Parts.Single(p => p.Id == 3103).ParentId = 3101;
            var transport = new StreamingFakeTransport(Mothership.CheckPlanning());
            var mission = Checked(transport, craft);

            var finding = Assert.Single(mission.View.CraftCheck.Findings);
            Assert.Equal("child_vessel_problem", finding.Code);
            Assert.Contains("stage 2 separates Probodobodyne OKTO with Claudia's kOS processor, leaving Mk1 Command Pod (where she sits) without one", finding.Explanation);
        }

        [Fact]
        public void ClaudesOwnChildVesselFindingKeepsHerFixAndGainsTheModsFacts()
        {
            var transport = new StreamingFakeTransport(Mothership.Reply("{\"summary\":\"The probe can't fly.\",\"findings\":[{\"code\":\"child_vessel_problem\","
                + "\"explanation\":\"The probe has no way to talk to me.\",\"fix\":\"Add a Communotron 16 to the probe.\"}],\"child_vessel_stages\":[2]}"));
            var mission = Checked(transport, Mothership.WithProbe(antenna: false));

            var finding = Assert.Single(mission.View.CraftCheck.Findings);
            Assert.Equal("Add a Communotron 16 to the probe.", finding.Fix);
            Assert.StartsWith("The probe has no way to talk to me.", finding.Explanation);
            Assert.Contains("no antenna to reach Claudia", finding.Explanation);
        }
    }
}
