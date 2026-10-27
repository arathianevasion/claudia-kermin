// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// One RCS nozzle, as the adapter reads it, in the craft's control frame (x pitch, y roll, z yaw): where it is from the centre
    /// of mass (m), the direction its thrust pushes the craft (a unit vector), and its full thrust (kN).
    /// </summary>
    public sealed class RcsNozzle
    {
        public double X;
        public double Y;
        public double Z;

        public double DirX;
        public double DirY;
        public double DirZ;

        public double Thrust;
    }

    /// <summary>RCS torque (kN·m) about pitch and yaw, the weaker of the two directions on each axis.</summary>
    public sealed class RcsAxes
    {
        public double Pitch;
        public double Yaw;
    }

    /// <summary>
    /// RCS torque the way kOS's steering counts it (<c>CorrectedGetPotentialTorque</c>, kOS #2814), in place of stock
    /// <c>GetPotentialTorque</c>, which kOS calls broken for <c>ModuleRCS</c>: per nozzle, its position from the centre of mass
    /// crossed with its thrust, the positive parts summed one way and the negative parts the other on each axis. Like kOS, it
    /// ignores precision mode. Which nozzles count (enabled, not shielded) is the adapter's call. The answer on each axis is the
    /// weaker direction, as for the wheels, so a lopsided layout budgets the slower way round.
    /// </summary>
    public static class RcsTorque
    {
        public static RcsAxes Sum(IEnumerable<RcsNozzle> nozzles)
        {
            double pitchPos = 0, pitchNeg = 0, yawPos = 0, yawNeg = 0;
            foreach (var t in nozzles)
            {
                // r × F; only x (pitch) and z (yaw) are needed.
                var pitch = (t.Y * t.DirZ - t.Z * t.DirY) * t.Thrust;
                var yaw = (t.X * t.DirY - t.Y * t.DirX) * t.Thrust;
                pitchPos += Math.Max(pitch, 0);
                pitchNeg += Math.Min(pitch, 0);
                yawPos += Math.Max(yaw, 0);
                yawNeg += Math.Min(yaw, 0);
            }
            return new RcsAxes { Pitch = Math.Min(pitchPos, -pitchNeg), Yaw = Math.Min(yawPos, -yawNeg) };
        }
    }
}
