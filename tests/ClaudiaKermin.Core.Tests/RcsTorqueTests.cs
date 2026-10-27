// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// RCS torque the way kOS's steering counts it (kOS #2814): per nozzle, the cross product of its position from the centre of
    /// mass and its thrust, summed one way and the other on each axis. Frame: the craft's control frame, x pitch, y roll, z yaw.
    /// Expected figures are worked by hand.
    /// </summary>
    public sealed class RcsTorqueTests
    {
        private static RcsNozzle Nozzle(double x, double y, double z, double dirX, double dirY, double dirZ, double thrust = 1)
        {
            return new RcsNozzle { X = x, Y = y, Z = z, DirX = dirX, DirY = dirY, DirZ = dirZ, Thrust = thrust };
        }

        [Fact]
        public void A_nozzle_pointing_through_the_centre_of_mass_gives_no_torque()
        {
            // 2 m up the roll axis, firing along it.
            var torque = RcsTorque.Sum(new[] { Nozzle(0, 2, 0, 0, -1, 0, 5) });
            Assert.Equal(0, torque.Pitch, 9);
            Assert.Equal(0, torque.Yaw, 9);
        }

        [Fact]
        public void A_pair_of_nozzles_firing_either_way_gives_torque_both_ways_about_pitch()
        {
            // 2 m up the roll axis, 1 kN each along +z and -z: r × F = (2·±1, 0, 0) on x, so ±2 kN·m about pitch, none about yaw.
            var torque = RcsTorque.Sum(new[] { Nozzle(0, 2, 0, 0, 0, 1), Nozzle(0, 2, 0, 0, 0, -1) });
            Assert.Equal(2, torque.Pitch, 9);
            Assert.Equal(0, torque.Yaw, 9);
        }

        [Fact]
        public void A_nozzle_firing_one_way_only_gives_no_torque_the_other_way_so_the_weaker_direction_is_zero()
        {
            var torque = RcsTorque.Sum(new[] { Nozzle(0, 2, 0, 0, 0, 1) });
            Assert.Equal(0, torque.Pitch, 9);
        }

        [Fact]
        public void Nozzles_off_to_the_side_turn_about_yaw()
        {
            // 1.5 m out along x, 2 kN each along ±y: r × F = (0, 0, 1.5·±2), so ±3 kN·m about yaw.
            var torque = RcsTorque.Sum(new[] { Nozzle(1.5, 0, 0, 0, 1, 0, 2), Nozzle(1.5, 0, 0, 0, -1, 0, 2) });
            Assert.Equal(0, torque.Pitch, 9);
            Assert.Equal(3, torque.Yaw, 9);
        }

        [Fact]
        public void A_four_way_block_on_each_side_sums_its_nozzles_and_counts_the_weaker_direction()
        {
            // Two RV-105-like blocks 2 m fore and 2 m aft, each with 1 kN nozzles along ±x and ±z. About pitch the fore block's
            // ±z pair gives ±2 and the aft block's ±z pair ∓2: 4 kN·m each way. About yaw the ±x pairs give the same, 4 kN·m.
            var nozzles = new[]
            {
                Nozzle(0, 2, 0, 1, 0, 0), Nozzle(0, 2, 0, -1, 0, 0), Nozzle(0, 2, 0, 0, 0, 1), Nozzle(0, 2, 0, 0, 0, -1),
                Nozzle(0, -2, 0, 1, 0, 0), Nozzle(0, -2, 0, -1, 0, 0), Nozzle(0, -2, 0, 0, 0, 1), Nozzle(0, -2, 0, 0, 0, -1),
            };
            var torque = RcsTorque.Sum(nozzles);
            Assert.Equal(4, torque.Pitch, 9);
            Assert.Equal(4, torque.Yaw, 9);
        }

        [Fact]
        public void An_unbalanced_layout_counts_the_weaker_direction()
        {
            // 3 kN along +z and 1 kN along -z, 2 m up: +6 one way, -2 the other.
            var torque = RcsTorque.Sum(new[] { Nozzle(0, 2, 0, 0, 0, 1, 3), Nozzle(0, 2, 0, 0, 0, -1, 1) });
            Assert.Equal(2, torque.Pitch, 9);
        }

        [Fact]
        public void No_nozzles_give_no_torque()
        {
            var torque = RcsTorque.Sum(new RcsNozzle[0]);
            Assert.Equal(0, torque.Pitch);
            Assert.Equal(0, torque.Yaw);
        }
    }
}
