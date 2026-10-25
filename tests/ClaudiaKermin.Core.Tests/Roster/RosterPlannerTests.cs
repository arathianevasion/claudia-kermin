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

        private static KerbalSnapshot Crew(string name, string trait = "Pilot", KerbalStanding standing = KerbalStanding.Available)
        {
            return new KerbalSnapshot(name, trait, KerbalKind.Crew, standing);
        }

        private static KerbalSnapshot Applicant(string name, string trait = "Pilot")
        {
            return new KerbalSnapshot(name, trait, KerbalKind.Applicant, KerbalStanding.Available);
        }

        private static KerbalSnapshot Claudia(KerbalStanding standing = KerbalStanding.Available, string name = ClaudiaIdentity.Name)
        {
            return new KerbalSnapshot(name, ClaudiaIdentity.TraitName, KerbalKind.Crew, standing);
        }

        private static List<RosterAction> Plan(RosterState state, params KerbalSnapshot[] kerbals)
        {
            return RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
        }

        private static RosterAction Single(List<RosterAction> actions, RosterActionType type)
        {
            return Assert.Single(actions, a => a.Type == type);
        }

        private static string[] TargetsOf(List<RosterAction> actions, RosterActionType type)
        {
            return actions.Where(a => a.Type == type).Select(a => a.Target).ToArray();
        }

        [Fact]
        public void AddsClaudiaToARosterWithoutHer()
        {
            var actions = Plan(new RosterState(), Crew("Jebediah Kerman"));
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
        }

        [Fact]
        public void IsIdempotentOnceClaudiaIsOnTheRoster()
        {
            Assert.Empty(Plan(new RosterState(), Crew("Jebediah Kerman"), Claudia()));
        }

        [Fact]
        public void RerollsEveryOtherKerbalWhoGetsSuperIntelligence()
        {
            var actions = Plan(new RosterState(), Claudia(), Crew("Bill Kerman", ClaudiaIdentity.TraitName), Applicant("Bob Kerman", ClaudiaIdentity.TraitName));
            var rerolls = actions.Where(a => a.Type == RosterActionType.RerollTrait).ToList();
            Assert.Equal(new[] { "Bill Kerman", "Bob Kerman" }, rerolls.Select(r => r.Target).ToArray());
            Assert.All(rerolls, r => Assert.Contains(r.Trait, ClaudiaIdentity.HiringTraits));
        }

        [Fact]
        public void RerollNeverLeavesTheHiringTraits()
        {
            foreach (var pick in new[] { 0, 1, 2 })
            {
                var actions = RosterPlanner.Plan(new[] { Claudia(), Crew("Bill Kerman", ClaudiaIdentity.TraitName) }, new RosterState(), Now, Timer, max => pick);
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
            var actions = Plan(new RosterState(), Claudia(), Applicant("Claudia Smith"), Applicant("Bob claudiason"));
            Assert.Equal(new[] { "Claudia Smith", "Bob claudiason" }, TargetsOf(actions, RosterActionType.ReplaceApplicant));
            Assert.DoesNotContain(actions, a => a.Type == RosterActionType.PromptRename);
        }

        [Fact]
        public void AddsClaudiaAlongsideAClashingApplicant()
        {
            var actions = Plan(new RosterState(), Applicant("Claudia Smith"));
            Assert.Contains(actions, a => a.Type == RosterActionType.AddClaudia);
        }

        [Fact]
        public void PromptsOnceForAHiredKerbalNamedClaudiaAndWaitsToAddHer()
        {
            var state = new RosterState();
            var kerbals = new[] { Crew("Claudia Smith") };

            var first = RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
            var prompt = Assert.Single(first);
            Assert.Equal(RosterActionType.PromptRename, prompt.Type);
            Assert.Equal("Claudia Smith", prompt.Target);

            Assert.Empty(RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0));
        }

        [Fact]
        public void AcceptingTheRenameLetsClaudiaBeAdded()
        {
            var state = new RosterState();
            RosterPlanner.Plan(new[] { Crew("Claudia Smith") }, state, Now, Timer, max => 0);
            state.RenameAnswered(false);

            var actions = RosterPlanner.Plan(new[] { Crew("Clara Smith") }, state, Now, Timer, max => 0);
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
        }

        [Fact]
        public void ASkippedRenamePromptIsNoLongerPendingAndIsOfferedAgain()
        {
            var state = new RosterState();
            var kerbals = new[] { Crew("Claudia Smith") };
            RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
            Assert.True(state.PromptPending);

            state.RenameSkipped();

            Assert.False(state.PromptPending);
            Assert.False(state.RenameDeclined);
            Assert.Equal(RosterActionType.PromptRename, Assert.Single(RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0)).Type);
        }

        [Fact]
        public void DecliningTheRenameLeavesClaudiaInactiveAndStopsAsking()
        {
            var state = new RosterState();
            var kerbals = new[] { Crew("Claudia Smith") };
            RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0);
            state.RenameAnswered(true);

            Assert.True(state.RenameDeclined);
            Assert.Empty(RosterPlanner.Plan(kerbals, state, Now, Timer, max => 0));
        }

        [Fact]
        public void AClashDoesNotStopHerBeingLookedAfterOnceSheIsOnTheRoster()
        {
            var state = new RosterState { RenameDeclined = true, RespawnAtUT = Now };
            var actions = Plan(state, Crew("Claudia Smith"), Claudia(KerbalStanding.Missing, "Clod Kerman"));
            Assert.Contains(actions, a => a.Type == RosterActionType.RevertName);
            Assert.Contains(actions, a => a.Type == RosterActionType.Respawn);
            Assert.DoesNotContain(actions, a => a.Type == RosterActionType.AddClaudia);
        }

        [Fact]
        public void OnlyHiredCrewTriggerTheRenameDialog()
        {
            var tourist = new KerbalSnapshot("Claudia Smith", "Tourist", KerbalKind.Tourist, KerbalStanding.Available);
            var actions = Plan(new RosterState(), tourist);
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
        }

        [Fact]
        public void ADeclinedSaveRecoversWhenTheClashGoesAway()
        {
            var state = new RosterState { RenameDeclined = true };
            var actions = Plan(state, Crew("Jebediah Kerman"));
            Assert.Equal(RosterActionType.AddClaudia, Assert.Single(actions).Type);
            Assert.False(state.RenameDeclined);
        }

        [Fact]
        public void RevertsARenameFoundByTrait()
        {
            var state = new RosterState();
            var actions = Plan(state, Claudia(name: "Clod Kerman"));
            var revert = Assert.Single(actions);
            Assert.Equal(RosterActionType.RevertName, revert.Type);
            Assert.Equal("Clod Kerman", revert.Target);
            Assert.Equal(ClaudiaIdentity.Name, revert.NewName);
        }

        [Fact]
        public void ARenamedClaudiaIsFoundByHerTraitAndRevertedWithoutAddingAnother()
        {
            var actions = Plan(new RosterState(), Crew("Jebediah Kerman"), Claudia(name: "Clod Kerman"));
            var revert = Assert.Single(actions);
            Assert.Equal(RosterActionType.RevertName, revert.Type);
            Assert.Equal("Clod Kerman", revert.Target);
            Assert.Equal(ClaudiaIdentity.Name, revert.NewName);
        }

        [Fact]
        public void AKerbalAheadOfClaudiaIsNeverTakenForHer()
        {
            var state = new RosterState();
            var roster = new[] { Crew("Jebediah Kerman"), Claudia(KerbalStanding.Missing) };
            RosterPlanner.Plan(roster, state, Now, Timer, max => 0);
            var actions = RosterPlanner.Plan(roster, state, Now + Timer + 1, Timer, max => 0);
            var respawn = Assert.Single(actions);
            Assert.Equal(RosterActionType.Respawn, respawn.Type);
            Assert.Equal(ClaudiaIdentity.Name, respawn.Target);
        }

        [Fact]
        public void ClaudiaWhoseTraitWasChangedByAModIsFoundByName()
        {
            var modded = new KerbalSnapshot(ClaudiaIdentity.Name, "Pilot", KerbalKind.Crew, KerbalStanding.Available);
            Assert.Empty(Plan(new RosterState(), Crew("Jebediah Kerman"), modded));
        }

        [Fact]
        public void ClaudiaIsTheTraitHolderNamedAfterHerWhenSeveralHaveTheTrait()
        {
            var actions = Plan(new RosterState(), Crew("Bill Kerman", ClaudiaIdentity.TraitName), Claudia());
            Assert.Equal("Bill Kerman", Assert.Single(actions).Target);
        }

        // KSP leaves persistentID at 0 for every Kerbal who is only on the roster, so a snapshot carries no id:
        // each action must name its Kerbal by current name and nothing else.
        [Fact]
        public void EveryActionTargetsItsKerbalByCurrentName()
        {
            var roster = new[]
            {
                Crew("Jebediah Kerman"),
                Claudia(KerbalStanding.Missing, "Clod Kerman"),
                Applicant("Claudia Smith"),
                Applicant("Bob Kerman", ClaudiaIdentity.TraitName),
                Crew("Bill Kerman", ClaudiaIdentity.TraitName),
                Crew("Claudia Jones"),
            };
            var actions = Plan(new RosterState { RespawnAtUT = Now }, roster);

            Assert.Equal(new[] { "Bob Kerman", "Bill Kerman" }, TargetsOf(actions, RosterActionType.RerollTrait));
            Assert.Equal(new[] { "Claudia Smith" }, TargetsOf(actions, RosterActionType.ReplaceApplicant));
            Assert.Equal(new[] { "Claudia Jones" }, TargetsOf(actions, RosterActionType.PromptRename));
            var revert = Single(actions, RosterActionType.RevertName);
            Assert.Equal("Clod Kerman", revert.Target);
            Assert.Equal(ClaudiaIdentity.Name, revert.NewName);
            // Actions are applied in order, so after the revert she is already back under her own name.
            Assert.Equal(ClaudiaIdentity.Name, Single(actions, RosterActionType.Respawn).Target);
        }

        [Fact]
        public void ADeadRenamedClaudiaIsSetMissingUnderTheNameTheRevertRestores()
        {
            var actions = Plan(new RosterState(), Crew("Jebediah Kerman"), Claudia(KerbalStanding.Dead, "Clod Kerman"));
            Assert.Equal("Clod Kerman", Single(actions, RosterActionType.RevertName).Target);
            Assert.Equal(ClaudiaIdentity.Name, Single(actions, RosterActionType.SetMissing).Target);
        }

        [Fact]
        public void ALegacyClaudiaIdIsIgnoredOnLoad()
        {
            var state = RosterState.FromValues(new Dictionary<string, string> { { "claudiaId", "0" } });
            Assert.DoesNotContain("claudiaId", state.ToValues().Keys);
            Assert.Empty(Plan(state, Crew("Jebediah Kerman"), Claudia()));
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
            var actions = Plan(state, Claudia(KerbalStanding.Dead));
            var missing = Single(actions, RosterActionType.SetMissing);
            Assert.Equal(ClaudiaIdentity.Name, missing.Target);
            Assert.Equal(Now + Timer, missing.UT);
            Assert.Equal(Now + Timer, state.RespawnAtUT);
        }

        [Fact]
        public void AMissingClaudiaWaitsForTheTimer()
        {
            var state = new RosterState { RespawnAtUT = Now + 1 };
            Assert.Empty(Plan(state, Claudia(KerbalStanding.Missing)));
        }

        [Fact]
        public void AMissingClaudiaWithNoRecordedTimerGetsOne()
        {
            var state = new RosterState();
            Assert.Empty(Plan(state, Claudia(KerbalStanding.Missing)));
            Assert.Equal(Now + Timer, state.RespawnAtUT);
        }

        [Fact]
        public void AMissingClaudiaReturnsWhenTheTimerEnds()
        {
            var state = new RosterState { RespawnAtUT = Now };
            var actions = Plan(state, Claudia(KerbalStanding.Missing));
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
            var state = new RosterState { RenameDeclined = true, RespawnAtUT = 99.5 };
            var copy = RosterState.FromValues(state.ToValues());
            Assert.True(copy.RenameDeclined);
            Assert.Equal(99.5, copy.RespawnAtUT);
        }

        [Fact]
        public void EmptyValuesGiveAFreshState()
        {
            var copy = RosterState.FromValues(new Dictionary<string, string>());
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
