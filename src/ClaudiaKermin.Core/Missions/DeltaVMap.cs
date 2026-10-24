// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>One leg of travel in the Delta-v Map.</summary>
    public sealed class DeltaVLeg
    {
        /// <summary>E.g. <c>launch:Kerbin</c>, <c>transfer:Mun</c>, <c>land:Mun</c>, <c>ascend:Mun</c>, <c>return:Mun</c>.</summary>
        public string Id = "";
        public string Description = "";
        /// <summary>m/s, without any margin.</summary>
        public double DeltaV;
        /// <summary>For a transfer: the burn out of the home body's low orbit.</summary>
        public double Departure;
        /// <summary>For a transfer: the capture burn into the moon's low orbit.</summary>
        public double Arrival;
        /// <summary>For a transfer to an inclined moon: the plane change, included in <see cref="DeltaV"/>.</summary>
        public double PlaneChange;
    }

    /// <summary>
    /// The Delta-v Map: the delta-v each leg of travel in the home body's system needs, against which a
    /// Mission Plan is budgeted. Computed from body data (patched-conic Hohmann transfers between low
    /// orbits, plus a simple ascent-loss allowance) rather than copied from community charts, so it follows
    /// whatever planet pack the game runs. The figures carry no margin; the Craft Check adds it.
    /// </summary>
    public sealed class DeltaVMap
    {
        /// <summary>Ascent losses (gravity and drag) as a fraction of low-orbit speed, with an atmosphere.</summary>
        private const double AtmosphericAscentLoss = 0.5;
        /// <summary>The same without an atmosphere.</summary>
        private const double AirlessAscentLoss = 0.1;
        /// <summary>Low orbit above an atmosphere clears it by this much (m).</summary>
        private const double AtmosphereClearance = 10000;
        /// <summary>Low orbit round an airless body, as a fraction of its radius above the surface.</summary>
        private const double AirlessOrbitFraction = 0.07;

        public readonly List<DeltaVLeg> Legs = new List<DeltaVLeg>();
        public readonly List<string> Moons = new List<string>();
        public string Home = "";

        public DeltaVLeg Leg(string id)
        {
            return Legs.FirstOrDefault(l => l.Id == id);
        }

        public static DeltaVMap Compute(IList<SnapshotBody> bodies, string home)
        {
            var map = new DeltaVMap { Home = home };
            var center = bodies.FirstOrDefault(b => b.Name == home);
            if (center == null) return map;

            var lowHome = LowOrbit(center);
            map.Legs.Add(new DeltaVLeg
            {
                Id = "launch:" + home,
                Description = "Launch from " + home + "'s surface to a low orbit (" + Km(lowHome - center.Radius) + ")",
                DeltaV = Circular(center.Gm, lowHome) * (1 + (center.AtmosphereHeight > 0 ? AtmosphericAscentLoss : AirlessAscentLoss)),
            });
            map.Legs.Add(new DeltaVLeg
            {
                Id = "land:" + home,
                Description = "Deorbit from low " + home + " orbit to the surface",
                DeltaV = Landing(center, lowHome),
            });

            foreach (var moon in bodies.Where(b => b.Parent == home && b.Sma > 0))
            {
                map.Moons.Add(moon.Name);
                var lowMoon = LowOrbit(moon);
                var a = (lowHome + moon.Sma) / 2;
                var departure = Math.Sqrt(center.Gm * (2 / lowHome - 1 / a)) - Circular(center.Gm, lowHome);
                var atMoon = Math.Sqrt(center.Gm * (2 / moon.Sma - 1 / a));
                var excess = Math.Abs(Circular(center.Gm, moon.Sma) - atMoon);
                var arrival = Math.Sqrt(excess * excess + 2 * moon.Gm / lowMoon) - Circular(moon.Gm, lowMoon);
                var plane = 2 * atMoon * Math.Sin(moon.Inclination * Math.PI / 360);

                map.Legs.Add(new DeltaVLeg
                {
                    Id = "transfer:" + moon.Name,
                    Description = "Low " + home + " orbit to low " + moon.Name + " orbit (departure, plane change and capture)",
                    Departure = departure,
                    Arrival = arrival,
                    PlaneChange = plane,
                    DeltaV = departure + arrival + plane,
                });
                map.Legs.Add(new DeltaVLeg
                {
                    Id = "land:" + moon.Name,
                    Description = "Low " + moon.Name + " orbit to the surface",
                    DeltaV = Landing(moon, lowMoon),
                });
                map.Legs.Add(new DeltaVLeg
                {
                    Id = "ascend:" + moon.Name,
                    Description = "Surface of " + moon.Name + " to a low orbit",
                    DeltaV = moon.AtmosphereHeight > 0
                        ? Circular(moon.Gm, lowMoon) * (1 + AtmosphericAscentLoss)
                        : Landing(moon, lowMoon),
                });
                map.Legs.Add(new DeltaVLeg
                {
                    Id = "return:" + moon.Name,
                    Description = "Low " + moon.Name + " orbit back to a " + home + " periapsis at low-orbit height",
                    DeltaV = arrival + plane,
                    Arrival = arrival,
                    PlaneChange = plane,
                });
            }
            return map;
        }

        /// <summary>The Map as lines for Claude: id, description and delta-v.</summary>
        public string ToText()
        {
            var text = new StringBuilder();
            foreach (var leg in Legs)
            {
                text.Append("- ").Append(leg.Id).Append(": ").Append(leg.Description).Append(": ")
                    .Append(leg.DeltaV.ToString("#,0", CultureInfo.InvariantCulture)).Append(" m/s\n");
            }
            return text.ToString();
        }

        private static double Circular(double gm, double radius)
        {
            return Math.Sqrt(gm / radius);
        }

        /// <summary>The radius of the body's low orbit: clear of the atmosphere, or a fraction of the radius up.</summary>
        private static double LowOrbit(SnapshotBody body)
        {
            return body.AtmosphereHeight > 0
                ? body.Radius + body.AtmosphereHeight + AtmosphereClearance
                : body.Radius * (1 + AirlessOrbitFraction);
        }

        /// <summary>
        /// From low orbit down to the surface: lower the periapsis, then (airless) stop at it. With an
        /// atmosphere only the periapsis lowering is paid for; drag and parachutes do the rest.
        /// </summary>
        private static double Landing(SnapshotBody body, double lowOrbit)
        {
            var peri = body.AtmosphereHeight > 0 ? body.Radius + body.AtmosphereHeight / 2 : body.Radius;
            var a = (lowOrbit + peri) / 2;
            var lowering = Circular(body.Gm, lowOrbit) - Math.Sqrt(body.Gm * (2 / lowOrbit - 1 / a));
            if (body.AtmosphereHeight > 0) return lowering;
            return lowering + Math.Sqrt(body.Gm * (2 / peri - 1 / a));
        }

        private static string Km(double metres)
        {
            return (metres / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km";
        }
    }
}
