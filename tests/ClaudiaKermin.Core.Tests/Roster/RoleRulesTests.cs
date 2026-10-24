// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Roster;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Roster
{
    public class RoleRulesTests
    {
        [Theory]
        [InlineData("Pilot")]
        [InlineData("Engineer")]
        [InlineData("Scientist")]
        public void SuperIntelligenceCountsAsEachOfThePilotEngineerAndScientistRoles(string role)
        {
            Assert.True(RoleRules.CountsAs(ClaudiaIdentity.TraitName, role));
        }

        [Theory]
        [InlineData("Pilot", "Pilot")]
        [InlineData("Engineer", "Engineer")]
        [InlineData("Scientist", "Scientist")]
        public void AKerbalAlwaysCountsAsTheirOwnRole(string trait, string role)
        {
            Assert.True(RoleRules.CountsAs(trait, role));
        }

        [Theory]
        [InlineData("Pilot", "Engineer")]
        [InlineData("Scientist", "Pilot")]
        [InlineData("Tourist", "Pilot")]
        [InlineData(null, "Pilot")]
        public void OtherKerbalsDoNotCountAsAnotherRole(string trait, string role)
        {
            Assert.False(RoleRules.CountsAs(trait, role));
        }

        [Theory]
        [InlineData("Tourist")]
        [InlineData("Applicant")]
        [InlineData("SuperIntelligence")]
        [InlineData(null)]
        public void SuperIntelligenceDoesNotCountAsRolesOutsideHerThree(string role)
        {
            Assert.False(RoleRules.CountsAs(ClaudiaIdentity.TraitName, role));
        }

        [Fact]
        public void ExtraCountIsTheSuperIntelligenceCrewThatStockChecksMissed()
        {
            var crew = new[] { "Pilot", ClaudiaIdentity.TraitName, "Engineer" };
            Assert.Equal(1, RoleRules.ExtraCount("Pilot", crew));
            Assert.Equal(1, RoleRules.ExtraCount("Scientist", crew));
        }

        [Fact]
        public void ExtraCountIsZeroForAnythingButHerThreeRoles()
        {
            Assert.Equal(0, RoleRules.ExtraCount("Tourist", new[] { ClaudiaIdentity.TraitName }));
            Assert.Equal(0, RoleRules.ExtraCount("Pilot", new[] { "Pilot", "Engineer" }));
            Assert.Equal(0, RoleRules.ExtraCount("Pilot", new string[0]));
        }

        [Fact]
        public void ExtraCountIgnoresTheRoleBeingAskedAbout()
        {
            // A stock check already counts a Pilot as a Pilot; only Claudia is added on top.
            Assert.Equal(0, RoleRules.ExtraCount("Pilot", new[] { "Pilot", "Pilot" }));
        }
    }

    public class UninstallPlannerTests
    {
        private static KerbalSnapshot Kerbal(uint id, string trait, KerbalKind kind = KerbalKind.Crew, KerbalStanding standing = KerbalStanding.Available)
        {
            return new KerbalSnapshot(id, "Kerbal " + id, trait, kind, standing);
        }

        [Fact]
        public void SetsEveryKerbalWithTheSuperIntelligenceRoleToPilot()
        {
            var actions = UninstallPlanner.Plan(new[]
            {
                Kerbal(1, "Engineer"),
                Kerbal(2, ClaudiaIdentity.TraitName),
                Kerbal(3, ClaudiaIdentity.TraitName, standing: KerbalStanding.Missing),
                Kerbal(4, ClaudiaIdentity.TraitName, standing: KerbalStanding.Dead),
            });

            Assert.Equal(new uint[] { 2, 3, 4 }, actions.Select(a => a.KerbalId).ToArray());
            Assert.All(actions, a =>
            {
                Assert.Equal(RosterActionType.RerollTrait, a.Type);
                Assert.Equal("Pilot", a.Trait);
            });
        }

        [Fact]
        public void DoesNothingWhenNobodyHasTheRole()
        {
            Assert.Empty(UninstallPlanner.Plan(new[] { Kerbal(1, "Pilot"), Kerbal(2, "Scientist") }));
        }

        [Fact]
        public void IsIdempotentOnceTheTraitHasBeenSwitched()
        {
            Assert.Empty(UninstallPlanner.Plan(new[] { Kerbal(2, UninstallPlanner.ReplacementTrait) }));
        }

        [Fact]
        public void LeavesApplicantsAndTouristsToTheHiringPool()
        {
            var actions = UninstallPlanner.Plan(new[] { Kerbal(5, ClaudiaIdentity.TraitName, KerbalKind.Applicant) });
            Assert.Single(actions);
        }
    }
}
