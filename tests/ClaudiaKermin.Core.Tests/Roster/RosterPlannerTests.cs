// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Roster;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Roster
{
    public class RosterPlannerTests
    {
        private const double Now = 1000;
        private const double Timer = 500;

        private static KerbalSnapshot Crew(uint id, string name, string trait = "Pilot", KerbalStanding standing = KerbalStanding.Available)
        {
            return new KerbalSnapshot(id, name, trait, KerbalKind.Crew, standing);
        }

        private static KerbalSnapshot Applicant(uint id, string name, string trait = "Pilot")
        {
            return new KerbalSnapshot(id, name, trait, KerbalKind.Applicant, KerbalStanding.Available);
        }

        private static KerbalSnapshot Claudia(uint id = 7, KerbalStanding standing = KerbalStanding.Available, string name = ClaudiaIdentity.Name)
        {
            return new KerbalSnapshot(id, name, ClaudiaIdentity.TraitName, KerbalKind.Crew, standing);
        }

        private static List<RosterAction> Plan(RosterState state, params KerbalSnapshot[] kerbals)
        {
            return RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
        }

        private static RosterAction Single(List<RosterAction> actions, RosterActionType type)
        {
            return Assert.Single(actions, a => a.Type == type);
        }

        [Fact]
        public void AddsClaudiaToARosterWithoutHer()
        {
            var actions = Plan(new RosterState(), Crew(1, "Jebediah Kerman"));
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
        }

        [Fact]
        public void IsIdempotentOnceClaudiaIsOnTheRoster()
        {
            Assert.Empty(Plan(new RosterState(), Crew(1, "Jebediah Kerman"), Claudia()));
        }

        [Fact]
        public void RerollsEveryOtherKerbalWhoGetsSuperIntelligence()
        {
            var actions = Plan(new RosterState(), Claudia(), Crew(2, "Bill Kerman", ClaudiaIdentity.TraitName), Applicant(3, "Bob Kerman", ClaudiaIdentity.TraitName));
            var rerolls = actions.Where(a => a.Type == RosterActionType.RerollTrait).ToList();
            Assert.Equal(new uint[] { 2, 3 }, rerolls.Select(r => r.KerbalId).ToArray());
            Assert.All(rerolls, r => Assert.Contains(r.Trait, ClaudiaIdentity.HiringTraits));
        }

        [Fact]
        public void RerollNeverLeavesTheHiringTraits()
        {
            foreach (var pick in new[] { 0, 1, 2 })
            {
                var actions = RosterPlanner.Plan(new[] { Claudia(), Crew(2, "Bill Kerman", ClaudiaIdentity.TraitName) }, new RosterState(), Now, Timer, max => pick);
                Assert.Equal(ClaudiaIdentity.HiringTraits[pick], Single(actions, RosterActionType.RerollTrait).Trait);
            }
        }

        [Fact]
        public void DoesNotRerollClaudiaHerself()
        {
            Assert.Empty(Plan(new RosterState(), Claudia()));
        }

        [Fact]
        public void ReplacesApplicantsWhoseNamesContainClaudiaSilently()
        {
            var actions = Plan(new RosterState(), Claudia(), Applicant(4, "Claudia Smith"), Applicant(5, "Bob claudiason"));
            Assert.Equal(new uint[] { 4, 5 }, actions.Where(a => a.Type == RosterActionType.ReplaceApplicant).Select(a => a.KerbalId).ToArray());
            Assert.DoesNotContain(actions, a => a.Type == RosterActionType.PromptRename);
        }

        [Fact]
        public void AddsClaudiaAlongsideAClashingApplicant()
        {
            var actions = Plan(new RosterState(), Applicant(4, "Claudia Smith"));
            Assert.Contains(actions, a => a.Type == RosterActionType.AddClaudia);
        }

        [Fact]
        public void PromptsOnceForAHiredKerbalNamedClaudiaAndWaitsToAddHer()
        {
            var state = new RosterState();
            var kerbals = new[] { Crew(4, "Claudia Smith") };

            var first = RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
            var prompt = Assert.Single(first);
            Assert.Equal(RosterActionType.PromptRename, prompt.Type);
            Assert.Equal(4u, prompt.KerbalId);

            Assert.Empty(RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0));
        }

        [Fact]
        public void AcceptingTheRenameLetsClaudiaBeAdded()
        {
            var state = new RosterState();
            RosterPlanner.Plan(new[] { Crew(4, "Claudia Smith") }, state, Now, Timer, max => 0);
            state.RenameAnswered(false);

            var actions = RosterPlanner.Plan(new[] { Crew(4, "Clara Smith") }, state, Now, Timer, max => 0);
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
        }

        [Fact]
        public void DecliningTheRenameLeavesClaudiaInactiveAndStopsAsking()
        {
            var state = new RosterState();
            var kerbals = new[] { Crew(4, "Claudia Smith") };
            RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
            state.RenameAnswered(true);

            Assert.True(state.RenameDeclined);
            Assert.Empty(RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0));
        }

        [Fact]
        public void ADeclinedSaveRecoversWhenTheClashGoesAway()
        {
            var state = new RosterState { RenameDeclined = true };
            var actions = Plan(state, Crew(1, "Jebediah Kerman"));
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
            Assert.False(state.RenameDeclined);
        }

        [Fact]
        public void RevertsARenameFoundByPersistentId()
        {
            var state = new RosterState { ClaudiaId = 7 };
            var actions = Plan(state, Claudia(7, name: "Clod Kerman"));
            var revert = Assert.Single(actions);
            Assert.Equal(RosterActionType.RevertName, revert.Type);
            Assert.Equal(ClaudiaIdentity.Name, revert.Name);
        }

        [Fact]
        public void RemembersTheIdOfTheClaudiaItFinds()
        {
            var state = new RosterState();
            Plan(state, Claudia(9));
            Assert.Equal(9u, state.ClaudiaId);
        }

        [Fact]
        public void ClaudiaDoesNotClashWithHerOwnName()
        {
            var state = new RosterState();
            Assert.Empty(Plan(state, Claudia()));
            Assert.False(state.RenameDeclined);
        }

        [Fact]
        public void ADeadClaudiaGoesMissingUntilTheRespawnTimerEnds()
        {
            var state = new RosterState();
            var actions = Plan(state, Claudia(standing: KerbalStanding.Dead));
            var missing = Single(actions, RosterActionType.SetMissing);
            Assert.Equal(Now + Timer, missing.UT);
            Assert.Equal(Now + Timer, state.RespawnAtUT);
        }

        [Fact]
        public void AMissingClaudiaWaitsForTheTimer()
        {
            var state = new RosterState { RespawnAtUT = Now + 1 };
            Assert.Empty(Plan(state, Claudia(standing: KerbalStanding.Missing)));
        }

        [Fact]
        public void AMissingClaudiaWithNoRecordedTimerGetsOne()
        {
            var state = new RosterState();
            Assert.Empty(Plan(state, Claudia(standing: KerbalStanding.Missing)));
            Assert.Equal(Now + Timer, state.RespawnAtUT);
        }

        [Fact]
        public void AMissingClaudiaReturnsWhenTheTimerEnds()
        {
            var state = new RosterState { RespawnAtUT = Now };
            var actions = Plan(state, Claudia(standing: KerbalStanding.Missing));
            Assert.Equal(RosterActionType.Respawn, Assert.Single(actions).Type);
            Assert.Null(state.RespawnAtUT);
        }

        [Fact]
        public void AnAvailableClaudiaClearsAStaleTimer()
        {
            var state = new RosterState { RespawnAtUT = 5 };
            Assert.Empty(Plan(state, Claudia()));
            Assert.Null(state.RespawnAtUT);
        }

        [Fact]
        public void StateRoundTripsThroughItsPersistedValues()
        {
            var state = new RosterState { ClaudiaId = 12, RenameDeclined = true, RespawnAtUT = 99.5 };
            var copy = RosterState.FromValues(state.ToValues());
            Assert.Equal(12u, copy.ClaudiaId);
            Assert.True(copy.RenameDeclined);
            Assert.Equal(99.5, copy.RespawnAtUT);
        }

        [Fact]
        public void EmptyValuesGiveAFreshState()
        {
            var copy = RosterState.FromValues(new Dictionary<string, string>());
            Assert.Null(copy.ClaudiaId);
            Assert.False(copy.RenameDeclined);
            Assert.Null(copy.RespawnAtUT);
        }

        [Fact]
        public void ClaudiasStartingStatsAreFixed()
        {
            Assert.Equal(1f, ClaudiaIdentity.Courage);
            Assert.Equal(0f, ClaudiaIdentity.Stupidity);
            Assert.True(ClaudiaIdentity.IsBadass);
            Assert.False(ClaudiaIdentity.IsVeteran);
        }
    }
}
