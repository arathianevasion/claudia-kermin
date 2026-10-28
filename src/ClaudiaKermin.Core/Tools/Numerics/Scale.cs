/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>
    /// The non-dimensionalisation of a two-body problem, after MechJebLib's <c>Primitives/Scale.cs</c> (MechJeb2 a958383): lengths
    /// in units of a reference radius and velocities of the circular speed there, so the body's mu is 1 and a solver's fixed
    /// tolerances mean the same thing round Kerbin and round Dres. Scale the inputs, solve, unscale the answer.
    /// </summary>
    internal struct Scale
    {
        /// <summary>The reference length, m.</summary>
        public readonly double LengthScale;
        /// <summary>The circular speed at the reference length, m/s.</summary>
        public readonly double VelocityScale;

        private Scale(double lengthScale, double velocityScale)
        {
            LengthScale = lengthScale;
            VelocityScale = velocityScale;
        }

        /// <summary>The reference time, s: the time to cover the reference length at the reference speed.</summary>
        public double TimeScale
        {
            get { return LengthScale / VelocityScale; }
        }

        /// <summary>The reference acceleration, m/s²: mu over the reference length squared.</summary>
        public double AccelerationScale
        {
            get { return VelocityScale / TimeScale; }
        }

        /// <summary>The scale for a body of gravitational parameter <paramref name="mu"/> (m³/s²) and reference length <paramref name="lengthScale"/> (m), both positive and finite.</summary>
        public static Scale Create(double mu, double lengthScale)
        {
            if (!SearchInput.Finite(mu) || mu <= 0) throw new ArgumentException("mu must be a positive number", "mu");
            if (!SearchInput.Finite(lengthScale) || lengthScale <= 0) throw new ArgumentException("lengthScale must be a positive number", "lengthScale");
            return new Scale(lengthScale, Math.Sqrt(mu / lengthScale));
        }

        /// <summary>A gravitational parameter in this scale's units: 1 for the mu the scale was made from.</summary>
        public double ScaleMu(double mu)
        {
            return mu / (LengthScale * VelocityScale * VelocityScale);
        }

        public double ScaleLength(double metres) { return metres / LengthScale; }
        public double UnscaleLength(double scaled) { return scaled * LengthScale; }
        public double ScaleVelocity(double metresPerSecond) { return metresPerSecond / VelocityScale; }
        public double UnscaleVelocity(double scaled) { return scaled * VelocityScale; }
        public double ScaleTime(double seconds) { return seconds / TimeScale; }
        public double UnscaleTime(double scaled) { return scaled * TimeScale; }
        public double ScaleAcceleration(double metresPerSecondSquared) { return metresPerSecondSquared / AccelerationScale; }
        public double UnscaleAcceleration(double scaled) { return scaled * AccelerationScale; }
    }
}
