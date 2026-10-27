// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The reentry research's model (research-reentry-landing-prediction-sim.py, #247), ported as the predictor's fakes: its
    /// tabulated Kerbin atmosphere, and its Mk1 capsule (heat shield first) with KSP's drag-cube curves from Physics.cfg. The
    /// research's published landing points are the predictor's test oracle, so these must stay as the script has them.
    /// </summary>
    internal static class ReentrySim
    {
        public const double Mu = 3.5316e12;
        public const double R = 600000;
        public const double AtmTop = 70000;
        public const double RotationPeriod = 21549.425;

        /// <summary>Altitude (m), static pressure (kPa), temperature (K).</summary>
        private static readonly double[,] Atm =
        {
            { 0, 101.325, 288 }, { 2500, 75.0, 275 }, { 5000, 54.0, 262 }, { 7500, 38.0, 250 },
            { 10000, 26.0, 238 }, { 15000, 11.5, 222 }, { 20000, 5.0, 216 }, { 25000, 2.2, 220 },
            { 30000, 1.0, 228 }, { 35000, 0.47, 238 }, { 40000, 0.22, 248 }, { 45000, 0.105, 252 },
            { 50000, 0.050, 250 }, { 55000, 0.022, 240 }, { 60000, 0.0090, 225 }, { 65000, 0.0030, 210 },
            { 70000, 0.0, 200 },
        };

        private static double LerpTable(double x, int col)
        {
            var n = Atm.GetLength(0);
            if (x <= Atm[0, 0]) return Atm[0, col];
            for (var i = 0; i < n - 1; i++)
            {
                if (x > Atm[i + 1, 0]) continue;
                var t = (x - Atm[i, 0]) / (Atm[i + 1, 0] - Atm[i, 0]);
                if (col == 1 && Atm[i, 1] > 0 && Atm[i + 1, 1] > 0)
                    return Math.Exp(Math.Log(Atm[i, 1]) * (1 - t) + Math.Log(Atm[i + 1, 1]) * t);
                return Atm[i, col] * (1 - t) + Atm[i + 1, col] * t;
            }
            return Atm[n - 1, col];
        }

        public sealed class Atmosphere : IReentryAir
        {
            public int Calls;

            public void Air(double altitude, double latitude, out double density, out double speedOfSound)
            {
                Calls++;
                if (altitude >= AtmTop)
                {
                    density = 0;
                    speedOfSound = Math.Sqrt(1.4 * 8.314462 * 200 / 0.0289644);
                    return;
                }
                var p = LerpTable(altitude, 1) * 1000;
                var temp = LerpTable(altitude, 2);
                density = p * 0.0289644 / (8.314462 * temp);
                speedOfSound = Math.Sqrt(1.4 * 8.314462 * temp / 0.0289644);
            }
        }

        /// <summary>A Unity AnimationCurve key: time, value, in and out tangents.</summary>
        private static double Hermite(double[][] keys, double x)
        {
            if (x <= keys[0][0]) return keys[0][1];
            if (x >= keys[keys.Length - 1][0]) return keys[keys.Length - 1][1];
            for (var i = 0; i < keys.Length - 1; i++)
            {
                var k0 = keys[i];
                var k1 = keys[i + 1];
                if (x > k1[0]) continue;
                var dt = k1[0] - k0[0];
                var t = (x - k0[0]) / dt;
                var m0 = k0[3] * dt;
                var m1 = k1[2] * dt;
                double t2 = t * t, t3 = t2 * t;
                return (2 * t3 - 3 * t2 + 1) * k0[1] + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * k1[1] + (t3 - t2) * m1;
            }
            return keys[keys.Length - 1][1];
        }

        private static double[][] Keys(params double[] flat)
        {
            var keys = new double[flat.Length / 4][];
            for (var i = 0; i < keys.Length; i++) keys[i] = new[] { flat[4 * i], flat[4 * i + 1], flat[4 * i + 2], flat[4 * i + 3] };
            return keys;
        }

        private static readonly double[][] Tip = Keys(0, 1, 0, 0, 0.85, 1.19, 0.6960422, 0.6960422, 1.1, 2.83, 0.730473, 0.730473, 5, 4, 0, 0);
        private static readonly double[][] Surf = Keys(0, 0.02, 0, 0, 0.85, 0.02, 0, 0, 0.9, 0.0152439, -0.07942077, -0.07942077,
            1.1, 0.0025, -0.005279571, -0.001936768, 2, 0.002083333, -2.314833e-05, -2.314833e-05,
            5, 0.003333333, -0.000180556, -0.000180556, 25, 0.001428571, -7.14286e-05, 0);
        private static readonly double[][] Tail = Keys(0, 1, 0, 0, 0.85, 1, 0, 0, 1.1, 0.25, -0.02215106, -0.02487721,
            1.4, 0.22, -0.03391732, -0.03391732, 5, 0.15, -0.001198566, -0.001198566, 25, 0.14, 0, 0);
        private static readonly double[][] Mult = Keys(0, 0.5, 0, 0, 0.85, 0.5, 0, 0, 1.1, 1.3, 0, -0.008100224, 2, 0.7, -0.1104858, -0.1104858,
            5, 0.6, 0, 0, 10, 0.85, 0.02198264, 0.02198264, 14, 0.9, 0.007694946, 0.007694946, 25, 0.95, 0, 0);
        private static readonly double[][] Cd = Keys(0.05, 0.0025, 0.15, 0.15, 0.4, 0.15, 0.3963967, 0.3963967, 0.7, 0.35, 0.9066986, 0.9066986,
            0.75, 0.45, 3.213604, 3.213604, 0.8, 0.66, 3.49833, 3.49833, 0.85, 0.8, 2.212924, 2.212924,
            0.9, 0.89, 1.1, 1.1, 1, 1, 1, 1);
        private static readonly double[][] CdPow = Keys(0, 1, 0, 0.00715953, 0.85, 1.25, 0.7780356, 0.7780356, 1.1, 2.5, 0.2492796, 0.2492796, 5, 3, 0, 0);
        private static readonly double[][] Pre = Keys(0, 4, 0, -2975.412, 0.0001, 3, -251.1479, -251.1479, 0.01, 2, -19.63584, -19.63584,
            0.1, 1.2, -0.7846036, -0.7846036, 1, 1, 0, 0, 100, 1, 0, 0, 200, 0.82, 0, 0,
            500, 0.86, 0.0001932119, 0.0001932119, 1000, 0.9, 1.54299e-05, 1.54299e-05, 10000, 0.95, 0, 0);

        private static double FaceCd(double cd, double mach)
        {
            return cd >= 1 ? 1 : Math.Pow(Hermite(Cd, cd), Hermite(CdPow, mach));
        }

        /// <summary>The research's capsule: KSP's cube model, Mk1 pod v2 with HeatShield1 and a Mk16 chute, 1240 kg.</summary>
        public sealed class Capsule : IReentryCraft
        {
            /// <summary>Drag ×: the research's sensitivity runs.</summary>
            public double Scale = 1;
            /// <summary>A Mach the drag is always taken at (the one-Cd·A models), or NaN for the full curve.</summary>
            public double FixedMach = double.NaN;

            public double MassKg
            {
                get { return 1240; }
            }

            public double DragArea(double mach, double densitySpeed)
            {
                if (!double.IsNaN(FixedMach))
                {
                    mach = FixedMach;
                    densitySpeed = 1000;
                }
                var m = Hermite(Mult, mach);
                var tip = 1.27 * Hermite(Tip, mach) * m * FaceCd(0.9507, mach);
                var tail = 0.30 * Hermite(Tail, mach) * m * FaceCd(0.50, mach);
                var side = 4 * 1.06 * Hermite(Surf, mach) * m * FaceCd(0.716, mach);
                return (tip + tail + side) * 0.1 * 8 * Hermite(Pre, densitySpeed) * Scale;
            }
        }

        public static SnapshotBody Kerbin(double atmosphere = AtmTop)
        {
            return new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = Mu, Radius = R, AtmosphereHeight = atmosphere, SeaLevelAtm = atmosphere > 0 ? 1 : 0,
                SoiRadius = 84159286.48, RotationPeriod = RotationPeriod, Sma = 13599840256,
            };
        }

        /// <summary>The frame the script works in: x through longitude 0 at UT 0, the body turning about +z.</summary>
        public static BodyFrame Frame()
        {
            return new BodyFrame { LonOffset = 0, Handed = 1, Ut = 0, RotationPeriod = RotationPeriod };
        }

        /// <summary>The script's burn: retrograde at UT 0 on the x axis of an equatorial circular orbit at <paramref name="alt0"/>, to put the periapsis at <paramref name="pe"/>.</summary>
        public static Conic AfterBurn(double alt0 = 80000, double pe = 30000)
        {
            var r0 = R + alt0;
            var rp = R + pe;
            var a = (r0 + rp) / 2;
            var v0 = Math.Sqrt(Mu * (2 / r0 - 1 / a));
            return Conic.FromState(Mu, new Vec3(r0, 0, 0), new Vec3(0, v0, 0), 0);
        }

        /// <summary>km along the equator for degrees of longitude, as the script reports downrange.</summary>
        public static double Km(double degrees)
        {
            return degrees * Math.PI / 180 * R / 1000;
        }
    }
}
