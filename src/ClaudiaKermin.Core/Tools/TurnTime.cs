// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// What the turn-time model needs of a craft on a Coast: the torque it can turn with (kN·m, the weaker of the two directions
    /// on each axis) and its moment of inertia (t·m²) about pitch and yaw. Reaction wheels always count; RCS counts only while
    /// it's on. Gimbals and control surfaces don't: there's no thrust or air on a Coast. The adapter reads these from the game.
    /// </summary>
    public sealed class TurnCraft
    {
        public double WheelPitch;
        public double WheelYaw;
        public double RcsPitch;
        public double RcsYaw;
        public bool RcsOn;
        public double MoiPitch;
        public double MoiYaw;

        /// <summary>kOS steering's <c>maxstoppingtime</c> (s): it caps the turn rate at this many seconds of angular acceleration.</summary>
        public double MaxStoppingTime = TurnTime.KosMaxStoppingTime;
    }

    /// <summary>A predicted turn: <see cref="Seconds"/> to leave before a burn, or <see cref="Error"/> saying why there's none.</summary>
    public sealed class TurnPrediction
    {
        /// <summary>The time to leave for the turn (s): the turn, the settle time, and at least <see cref="TurnTime.FloorSeconds"/>.</summary>
        public double Seconds;

        /// <summary>The 190° turn alone (s), before the settle time and the floor.</summary>
        public double TurnSeconds;

        /// <summary>The top turn rate (deg/s): kOS's cap, or the peak half the turn reaches if that's lower.</summary>
        public double TopRateDegS;

        /// <summary>Null, or why the craft can't turn (<see cref="TurnTime.NoTorque"/>).</summary>
        public string Error;
    }

    /// <summary>
    /// How long a craft takes to turn to a burn on a Coast (spec #314), so a block leaves enough time before it. The turn is a
    /// conservative 190° about the craft's slower transverse axis: full torque speeding up for the first half and slowing down
    /// for the second, capped at kOS steering's top turn rate. A settle time for the alignment check (within 2° and turning under
    /// 0.5°/s) goes on top, and the answer is never under 30 s. The settle time is a starting value, tuned by block-test runs.
    /// </summary>
    public static class TurnTime
    {
        public const double TurnDegrees = 190;
        public const double SettleSeconds = 10;
        public const double FloorSeconds = 30;

        /// <summary>kOS's default <c>steeringmanager:maxstoppingtime</c> (s).</summary>
        public const double KosMaxStoppingTime = 2;

        public const string NoTorque = "no usable torque";

        public static TurnPrediction Predict(TurnCraft craft)
        {
            var alpha = Math.Min(Acceleration(craft.WheelPitch, craft.RcsPitch, craft.RcsOn, craft.MoiPitch),
                Acceleration(craft.WheelYaw, craft.RcsYaw, craft.RcsOn, craft.MoiYaw));
            if (!(alpha > 0) || double.IsInfinity(alpha)) return new TurnPrediction { Error = NoTorque };

            var angle = TurnDegrees * Math.PI / 180;
            // kOS's steering asks for no more than it could stop from in maxstoppingtime at full torque.
            var cap = alpha * (craft.MaxStoppingTime > 0 ? craft.MaxStoppingTime : KosMaxStoppingTime);
            var peak = Math.Sqrt(alpha * angle);
            double turn;
            if (peak <= cap)
            {
                turn = 2 * Math.Sqrt(angle / alpha);
            }
            else
            {
                // Up to the cap and down from it take cap / alpha between them; the rest of the angle is at the cap.
                turn = angle / cap + cap / alpha;
                peak = cap;
            }
            return new TurnPrediction
            {
                Seconds = Math.Max(FloorSeconds, turn + SettleSeconds),
                TurnSeconds = turn,
                TopRateDegS = peak * 180 / Math.PI,
            };
        }

        /// <summary>Angular acceleration (rad/s²) on one axis: kN·m over t·m².</summary>
        private static double Acceleration(double wheel, double rcs, bool rcsOn, double moi)
        {
            var torque = Math.Max(0, wheel) + (rcsOn ? Math.Max(0, rcs) : 0);
            return moi > 0 ? torque / moi : 0;
        }
    }
}
