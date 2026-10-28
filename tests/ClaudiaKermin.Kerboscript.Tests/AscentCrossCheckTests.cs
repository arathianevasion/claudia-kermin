// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// Keeping plan_ascent's sim honest (#367): launch_ascent_sim.ks's craft (LV-T45 under an FL-T800) flown by launch.ks's own
    /// functions on kOS and by the C# <see cref="AscentSim"/>, with the same air, drag and options. Their coast_ap, max_q and
    /// pitch every 1000 m must agree: a change to launch.ks's rules that the C# sim doesn't follow fails here.
    /// </summary>
    public class AscentCrossCheckTests
    {
        /// <summary>The kOS sim's air: exponential, scale height 5600 m, 1.225 kg/m³ at sea level, sound slowing to 12 km.</summary>
        private sealed class KosSimAir : IAscentAir
        {
            public void Air(double altitude, out double atm, out double density, out double speedOfSound)
            {
                atm = Math.Exp(-altitude / 5600);
                density = 1.225 * atm;
                speedOfSound = 340 - 0.004 * Math.Min(altitude, 12000);
            }
        }

        /// <summary>The kOS sim's drag area with its transonic rise, as a table every 0.02 Mach.</summary>
        private static List<double[]> DragTable(double area)
        {
            var keys = new List<double[]>();
            for (var mach = 0.0; mach <= 10; mach += 0.02)
            {
                var transonic = (mach - 1.05) / 0.25;
                keys.Add(new[] { mach, area * (1 + 1.2 * Math.Exp(-transonic * transonic)) });
            }
            return keys;
        }

        private static AscentFlight CSharp(double dragArea, double maxQ, double targetAp, double thrustScale, double dry)
        {
            var thrustVac = 215000 * thrustScale;
            var stage = new AscentStage
            {
                Number = 0,
                ThrustVac = thrustVac,
                ThrustSeaLevel = thrustVac * 250 / 320,
                Flow = thrustVac / (320 * AscentSim.G0),
                StartMass = 6400,
                EndMass = dry * 1000,
                IspVac = 320,
                DeltaVVac = 320 * AscentSim.G0 * Math.Log(6.4 / dry),
            };
            var options = new AscentOptions
            {
                Gm = 3.5316e12,
                Radius = 600000,
                AtmosphereHeight = 70000,
                SeaLevelAtm = 1,
                Spin = 174.94,
                StartAltitude = 70,
                TargetAp = targetAp,
                TurnStartSpeed = 60,
                TurnShape = 0.45,
                MaxAoa = 5,
                MaxQ = maxQ,
                Track = true,
            };
            return AscentSim.Fly(options, new KosSimAir(), AscentSim.Table(DragTable(dragArea)), new List<AscentStage> { stage });
        }

        private static (Dictionary<string, string> Result, List<double[]> Track, string Printed) Kos(double dragArea, double maxQ, double targetAp, double thrustScale, double dry)
        {
            var sim = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "launch_ascent_sim.ks"));
            var lines = KosHarness.Run(CoreLibrary.Bundle(sim + "\nsim_ascent(lexicon(\"target_ap\", " + Num(targetAp) + ", \"max_q\", " + Num(maxQ)
                + ", \"turn_start_speed\", 60, \"turn_shape\", 0.45, \"max_aoa\", 5, \"drag_area\", " + Num(dragArea)
                + ", \"hold\", true, \"thin\", true, \"thrust_scale\", " + Num(thrustScale) + ", \"dry\", " + Num(dry) + ")).")).ToList();
            var result = lines.Where(l => !l.StartsWith("track ")).ToDictionary(l => l.Split(' ')[0], l => l.Substring(l.IndexOf(' ') + 1));
            // "track <h> m <t> s vert <degrees from vertical> ..."
            var track = lines.Where(l => l.StartsWith("track ")).Select(l => l.Split(' '))
                .Select(w => new[] { Parse(w[1]), Parse(w[6]) }).ToList();
            return (result, track, string.Join("\n", lines));
        }

        private static string Num(double value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static double Parse(string text)
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        [Theory]
        [InlineData(0.5, 12000, 60000, 1, 2.4)]
        [InlineData(1.0, 12000, 60000, 1, 2.4)]
        [InlineData(1.5, 12000, 60000, 1, 2.4)]
        [InlineData(0.5, -1, 80000, 1, 2.4)]
        [InlineData(0.5, 12000, 400000, 0.7, 2.0)]
        public void The_csharp_sim_flies_launch_like_the_kos_sim(double dragArea, double maxQ, double targetAp, double thrustScale, double dry)
        {
            var (kos, track, printed) = Kos(dragArea, maxQ, targetAp, thrustScale, dry);
            var flight = CSharp(dragArea, maxQ, targetAp, thrustScale, dry);
            var why = printed + "\nC#: coast_ap " + flight.CoastAp + ", max_q " + flight.MaxQ + ", track "
                + string.Join("\n", flight.Track.Select(p => Math.Round(p[0]) + " vert " + Math.Round(p[1], 1) + " prograde " + Math.Round(p[2], 1)
                    + " q " + Math.Round(p[3]) + " throttle " + Math.Round(100 * p[4])));

            // Both cut off at the target (the kOS sim within the top-up band, launch's within half of it).
            Assert.True(Parse(kos["coast_ap"]) > 0 && flight.CoastAp > 0, why);
            Assert.InRange(flight.CoastAp, Parse(kos["coast_ap"]) - 0.01 * targetAp, Parse(kos["coast_ap"]) + 0.01 * targetAp);
            Assert.InRange(flight.MaxQ, Parse(kos["max_q"]) * 0.95, Parse(kos["max_q"]) * 1.05);
            Assert.True(track.Count > 10, why);
            foreach (var point in track)
            {
                var near = flight.Track.OrderBy(p => Math.Abs(p[0] - point[0])).FirstOrDefault();
                Assert.True(near != null && Math.Abs(near[0] - point[0]) < 600, "no C# pitch near " + point[0] + " m\n" + why);
                Assert.True(Math.Abs(near[1] - point[1]) <= 2, "pitch at " + point[0] + " m: kOS " + point[1] + ", C# " + near[1] + "\n" + why);
            }
        }
    }
}
