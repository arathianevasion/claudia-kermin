// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class RuntimeGatesTests
    {
        private const uint Running = 100;
        private const uint OtherProcessor = 200;

        private static GatedRuns OneRun(int runId = 7)
        {
            var runs = new GatedRuns();
            runs.Open(Running, runId);
            return runs;
        }

        private static GateCall Call(GateTarget target, string suffix, bool set = false)
        {
            return new GateCall { Target = target, Suffix = suffix, IsSet = set };
        }

        // ---- KUNIVERSE save, load, revert and switch ----

        [Theory]
        [InlineData("QUICKSAVE")]
        [InlineData("QUICKLOAD")]
        [InlineData("QUICKSAVETO")]
        [InlineData("QUICKLOADFROM")]
        [InlineData("REVERTTOLAUNCH")]
        [InlineData("REVERTTOEDITOR")]
        [InlineData("REVERTTO")]
        [InlineData("FORCESETACTIVEVESSEL")]
        [InlineData("FORCEACTIVE")]
        [InlineData("LAUNCHCRAFT")]
        public void KUniverseSaveLoadRevertAndSwitchAreRefused(string suffix)
        {
            Assert.Equal("KUNIVERSE:" + suffix, RuntimeGates.Refuses(Call(GateTarget.KUniverse, suffix)));
        }

        [Theory]
        [InlineData("CANREVERT")]
        [InlineData("CANQUICKSAVE")]
        [InlineData("QUICKSAVELIST")]
        [InlineData("ACTIVEVESSEL")]
        [InlineData("TIMEWARP")]
        [InlineData("HOURSPERDAY")]
        public void ReadingKUniverseIsAllowed(string suffix)
        {
            Assert.Null(RuntimeGates.Refuses(Call(GateTarget.KUniverse, suffix)));
        }

        [Fact]
        public void ChangingTheActiveVesselIsRefusedAsASwitch()
        {
            // Triage case 3: KUNIVERSE:ACTIVEVESSEL read, then changed.
            Assert.Equal("SET KUNIVERSE:ACTIVEVESSEL (switching vessels)",
                RuntimeGates.Refuses(Call(GateTarget.KUniverse, "ACTIVEVESSEL", set: true)));
        }

        // ---- CONFIG, Archive, debugfreezegame ----

        [Fact]
        public void ConfigSettersAreRefusedAndGettersAllowed()
        {
            Assert.Equal("SET CONFIG:IPU", RuntimeGates.Refuses(Call(GateTarget.Config, "IPU", set: true)));
            Assert.Null(RuntimeGates.Refuses(Call(GateTarget.Config, "IPU")));
        }

        [Theory]
        [InlineData("write")]
        [InlineData("delete")]
        [InlineData("createfile")]
        public void ArchiveWritesAndDeletesAreRefused(string operation)
        {
            Assert.Equal("Archive " + operation + " (0:/)", RuntimeGates.Refuses(Call(GateTarget.Archive, operation)));
        }

        [Fact]
        public void DebugFreezeGameIsRefused()
        {
            Assert.Equal("debugfreezegame", RuntimeGates.Refuses(Call(GateTarget.DebugFreezeGame, "debugfreezegame")));
        }

        // ---- Physics warp on Coasts (ADR 0008, superseding ADR 0003's physics-warp bullet) ----

        private static GateCall PhysicsMode(bool allowed = true, bool coast = true, int current = 0)
        {
            var call = Call(GateTarget.TimeWarp, "MODE", set: true);
            call.Value = "physics";
            call.PhysicsWarpAllowed = allowed;
            call.OnCoast = coast;
            call.CurrentWarpIndex = current;
            return call;
        }

        private static GateCall PhysicsWarp(int index, bool allowed = true, bool coast = true)
        {
            var warp = Call(GateTarget.TimeWarp, "WARP", set: true);
            warp.PhysicsWarpMode = true;
            warp.WarpRateIndex = index;
            warp.PhysicsWarpAllowed = allowed;
            warp.OnCoast = coast;
            return warp;
        }

        [Fact]
        public void SwitchingToPhysicsModeIsAllowedAtOneTimesOnACoast()
        {
            Assert.Null(RuntimeGates.Refuses(PhysicsMode()));
        }

        [Fact]
        public void SwitchingToPhysicsModeOffACoastOrAboveOneTimesIsRefused()
        {
            Assert.Equal("physics warp (warp mode PHYSICS, off a Coast)", RuntimeGates.Refuses(PhysicsMode(coast: false)));
            Assert.Equal("physics warp (warp mode PHYSICS, not at 1x)", RuntimeGates.Refuses(PhysicsMode(current: 2)));
        }

        [Fact]
        public void SettingRailsWarpModeIsAllowedWhateverElse()
        {
            var call = Call(GateTarget.TimeWarp, "MODE", set: true);
            call.Value = "RAILS";
            call.CurrentWarpIndex = 5;
            Assert.Null(RuntimeGates.Refuses(call));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void PhysicsWarpUpToFourTimesIsAllowedOnACoast(int index)
        {
            Assert.Null(RuntimeGates.Refuses(PhysicsWarp(index)));
        }

        [Fact]
        public void PhysicsWarpAboveFourTimesIsRefusedEvenOnACoast()
        {
            Assert.Equal("physics warp (warp 4, above the 4x cap)", RuntimeGates.Refuses(PhysicsWarp(4)));
        }

        [Fact]
        public void PhysicsWarpOffACoastIsRefusedButDroppingToOneTimesIsNot()
        {
            Assert.Equal("physics warp (warp 2, off a Coast)", RuntimeGates.Refuses(PhysicsWarp(2, coast: false)));
            Assert.Null(RuntimeGates.Refuses(PhysicsWarp(0, coast: false, allowed: false)));
        }

        [Fact]
        public void EveryPhysicsWarpIsRefusedWithPhysicsWarpOff()
        {
            Assert.Equal("physics warp (warp mode PHYSICS, physics warp is off)", RuntimeGates.Refuses(PhysicsMode(allowed: false)));
            Assert.Equal("physics warp (warp 1, physics warp is off)", RuntimeGates.Refuses(PhysicsWarp(1, allowed: false)));
        }

        [Fact]
        public void RailsWarpIsAllowedWhateverTheSwitchAndCoast()
        {
            var warp = Call(GateTarget.TimeWarp, "WARP", set: true);
            warp.WarpRateIndex = 4;
            Assert.Null(RuntimeGates.Refuses(warp));
        }

        [Fact]
        public void WarpToIsRefusedInPhysicsModeEvenOnACoastAndAllowedOnRails()
        {
            // Issue #133: kOS's TIMEWARP:WARPTO and WARPTO() call KSP's TimeWarp.WarpTo, which warps in the mode
            // already set without calling the WARP setter; in physics mode that would be physics warp past the gate.
            var warpTo = Call(GateTarget.TimeWarp, "WARPTO");
            warpTo.PhysicsWarpMode = true;
            warpTo.PhysicsWarpAllowed = true;
            warpTo.OnCoast = true;
            Assert.Equal("physics warp (WARPTO in warp mode PHYSICS)", RuntimeGates.Refuses(warpTo));

            // The Core Library sets the mode to RAILS first, so its in-script warp (ADR 0004) is allowed.
            warpTo.PhysicsWarpMode = false;
            Assert.Null(RuntimeGates.Refuses(warpTo));
        }

        [Theory]
        [InlineData(new double[0], new bool[0], true)]
        [InlineData(new[] { 0.0, 0.0 }, new[] { false, false }, true)]
        [InlineData(new[] { 0.0, 12.5 }, new[] { false }, false)]
        [InlineData(new[] { 0.0 }, new[] { false, true }, false)]
        public void ACoastIsNoEngineThrustAndNoRcsFiring(double[] thrusts, bool[] rcs, bool coast)
        {
            Assert.Equal(coast, CoastCheck.IsCoast(thrusts, rcs));
        }

        [Fact]
        public void AnEngineAtThrottleZeroWithNoThrustIsACoastAndRcsFiringIsNot()
        {
            Assert.True(CoastCheck.IsCoast(new[] { 0.0 }, null));
            Assert.False(CoastCheck.IsCoast(null, new[] { true }));
        }

        [Fact]
        public void WarpIsDroppedOnlyWhenPhysicsWarpedAboveOneTimesOffACoast()
        {
            Assert.True(CoastCheck.ShouldDrop(true, false));
            Assert.False(CoastCheck.ShouldDrop(true, true));
            Assert.False(CoastCheck.ShouldDrop(false, false));
        }

        // ---- Other-vessel mutators (triage case 2: vessels found by iterating a list) ----

        [Fact]
        public void ADoEventOnAnotherVesselsPartIsRefused()
        {
            var call = Call(GateTarget.PartModule, "DOEVENT");
            call.OtherVessel = true;
            Assert.Equal("DOEVENT on another vessel's part", RuntimeGates.Refuses(call));
        }

        [Fact]
        public void ADoEventOnTheCraftsOwnPartIsAllowed()
        {
            Assert.Null(RuntimeGates.Refuses(Call(GateTarget.PartModule, "DOEVENT")));
        }

        [Fact]
        public void ReadingAnotherVesselsModuleIsAllowed()
        {
            var call = Call(GateTarget.PartModule, "GETFIELD");
            call.OtherVessel = true;
            Assert.Null(RuntimeGates.Refuses(call));
        }

        [Fact]
        public void RenamingAnotherVesselIsRefusedAndReadingItIsNot()
        {
            var rename = Call(GateTarget.Vessel, "SHIPNAME", set: true);
            rename.OtherVessel = true;
            Assert.Equal("SET SHIPNAME on another vessel", RuntimeGates.Refuses(rename));

            var read = Call(GateTarget.Vessel, "SHIPNAME");
            read.OtherVessel = true;
            Assert.Null(RuntimeGates.Refuses(read));
        }

        [Fact]
        public void RenamingTheCraftItselfIsAllowed()
        {
            Assert.Null(RuntimeGates.Refuses(Call(GateTarget.Vessel, "SHIPNAME", set: true)));
        }

        [Theory]
        [InlineData("SHUTDOWN")]
        [InlineData("UNDOCK")]
        [InlineData("CONTROLFROM")]
        public void PartMutatorsOnAnotherVesselAreRefused(string suffix)
        {
            var call = Call(GateTarget.Part, suffix);
            call.OtherVessel = true;
            Assert.Equal(suffix + " on another vessel's part", RuntimeGates.Refuses(call));

            call.OtherVessel = false;
            Assert.Null(RuntimeGates.Refuses(call));
        }

        // ---- Other processors' power (triage case 4) ----

        [Theory]
        [InlineData("DOEVENT")]
        [InlineData("DOACTION")]
        [InlineData("SETFIELD")]
        [InlineData("DEACTIVATE")]
        public void ActingOnAnotherKosProcessorOnTheSameCraftIsRefused(string suffix)
        {
            var call = Call(GateTarget.PartModule, suffix);
            call.KosProcessorModule = true;
            Assert.Equal(suffix + " on another kOS processor", RuntimeGates.Refuses(call));
        }

        [Fact]
        public void ActingOnAKosProcessorOnAChildVesselIsRefused()
        {
            var call = Call(GateTarget.PartModule, "DOEVENT");
            call.KosProcessorModule = true;
            call.OtherVessel = true;
            Assert.Equal("DOEVENT on another kOS processor", RuntimeGates.Refuses(call));
        }

        [Fact]
        public void SettingAnotherProcessorsBootFileIsRefused()
        {
            var call = Call(GateTarget.PartModule, "BOOTFILENAME", set: true);
            call.KosProcessorModule = true;
            Assert.Equal("SET BOOTFILENAME on another kOS processor", RuntimeGates.Refuses(call));
        }

        [Theory]
        [InlineData("DOEVENT")]
        [InlineData("DEACTIVATE")]
        public void AScriptMayActOnItsOwnProcessor(string suffix)
        {
            var call = Call(GateTarget.PartModule, suffix);
            call.KosProcessorModule = true;
            call.RunningProcessor = true;
            Assert.Null(RuntimeGates.Refuses(call));
        }

        [Fact]
        public void ReadingAnotherProcessorsModeIsAllowed()
        {
            var call = Call(GateTarget.PartModule, "MODE");
            call.KosProcessorModule = true;
            Assert.Null(RuntimeGates.Refuses(call));
        }

        // ---- Only while a mod-started program runs on that processor ----

        [Fact]
        public void AGateTripCarriesTheRunAndWhatItTried()
        {
            var trip = OneRun(7).Check(Running, Call(GateTarget.KUniverse, "QUICKSAVE"));

            Assert.NotNull(trip);
            Assert.Equal(7, trip.RunId);
            Assert.Equal("KUNIVERSE:QUICKSAVE", trip.What);
        }

        [Fact]
        public void ThePlayersTerminalOnAnotherProcessorIsNotGated()
        {
            Assert.Null(OneRun().Check(OtherProcessor, Call(GateTarget.KUniverse, "QUICKSAVE")));
        }

        [Fact]
        public void ACallFromNoProcessorIsNotGated()
        {
            Assert.Null(OneRun().Check(null, Call(GateTarget.Config, "IPU", set: true)));
        }

        [Fact]
        public void TheGatesComeDownWhenTheRunEnds()
        {
            var runs = OneRun(7);
            runs.Close(Running, 7);

            Assert.False(runs.Any);
            Assert.Null(runs.Check(Running, Call(GateTarget.KUniverse, "QUICKSAVE")));
        }

        [Fact]
        public void ALateCloseForAnEarlierRunLeavesTheNewRunGated()
        {
            var runs = OneRun(7);
            runs.Open(Running, 8);
            runs.Close(Running, 7);

            Assert.Equal(8, runs.RunOn(Running));
            Assert.Equal(8, runs.Check(Running, Call(GateTarget.KUniverse, "QUICKLOAD")).RunId);
        }

        [Fact]
        public void AnAllowedCallDuringARunIsNotATrip()
        {
            Assert.Null(OneRun().Check(Running, Call(GateTarget.KUniverse, "CANREVERT")));
        }
    }
}
