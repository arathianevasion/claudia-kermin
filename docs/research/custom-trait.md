# Research: a custom "Super Intelligence" crew trait for Claudia

Ticket: [#24 Custom Super Intelligence crew trait](https://github.com/arathianevasion/claudia-kermin/issues/24)
Target: KSP 1.12.5
Researched: 2026-10-23
Builds on: [`research/crew-roster`](https://github.com/arathianevasion/claudia-kermin/blob/research/crew-roster/docs/research/crew-roster.md) (#7)

## Answer

- **Yes, a pure config trait works, and it needs no ModuleManager.** KSP reads every top-level `EXPERIENCE_TRAIT` node in GameData and adds it as a new trait. A trait can list any mix of `EFFECT`s. The mod "Kerbals of All Trades" ships a "Mission Leader" trait with all Pilot, Engineer and Scientist effects, written exactly this way. You only need MM to *change* an existing trait (`@EXPERIENCE_TRAIT[...]`) or to patch other mods' configs.
- **Stock parts and modules check effects, not names.** The Mobile Processing Lab, science converter, experiment reset, EVA science, repairs (parachute repack, wheels, legs, solar panels), entering EVA construction mode, KerbNet, probe-free command and resource converters/drills all look for an effect. A Super Intelligence Kerbal satisfies all of them.
- **Name checks that would ignore her:**
  - **Stock:** contract crew requirements (`CrewTraitParameter`, used by comet-sample, vessel-repair and orbital-construction contracts, plus the "CREW_REQUEST" side requests in station, base and satellite contracts), **EVA welding** (`KerbalEVA.Weld` needs an "Engineer"), the EVA-construction "combine mass with nearby engineers" bonus, and the VAB/SPH default crew picker (it prefers a "Pilot").
  - **Mods:** many popular mods match the name too: Contract Configurator `HasCrew`/`HasAstronaut`, Kerbalism crew specs, Kerbal Engineer Redux career readouts, USI tools (auto-repair, Konstruction, cryo), Near Future Electrical reactor repair, SystemHeat, SSPX deployable habitats, StationScience and WildBlue converters.
- **There is a hidden catch: random hires.** Stock gives each new applicant or generated Kerbal a trait chosen by name hash from *every* non-Tourist trait. With a fourth trait, about one in four new hires would also be "Super Intelligence". The mod must remove the trait from that pool (C#), or rewrite stray assignments.
- **Main workaround:** a small C# plugin (we ship one anyway). (1) Remove our trait from `ExperienceConfigs.TraitNamesNoTourist` after MM's post-load reload. (2) Use Harmony postfixes on `FinePrint.Utilities.VesselUtilities.VesselCrewWithTraitCount` / `VesselCrewWithTrait` / `CrewTraitMissionAvailability` so she counts as Pilot, Engineer *and* Scientist. That one patch covers stock contracts, welding and the combine bonus. For mods, ship MM patches where their trait field is configurable (Kerbalism, StationScience, SSPX, WildBlue). Accept or Harmony-patch the ones that hardcode "Engineer".
- **Experience works exactly like a stock trait.** Levels come from the career/flight log and are trait-agnostic. In Career (and whenever "Enable Kerbal Experience" is on) she starts at level 0 unless the mod calls `KerbalRoster.SetExperienceLevel`. In Science and Sandbox, experience is off by default, so every Kerbal, Claudia included, is treated as max level 5 (XP = 99999).
- **Uninstall risk (inferred):** a save that still has `trait = SuperIntelligence` loaded without our cfg leaves her `experienceTrait` null. That will likely throw errors. Keep the cfg in the mod and treat removal as a save-breaking change.

### Recommended config shape

Put this in `GameData/ClaudiaKermin/Experience/Traits.cfg`. It is a new trait, not a patch, so there is no `@` and no MM. It is the union of the stock 1.12.5 Pilot, Engineer and Scientist effects (quoted below), taking the best value where they differ:

```
EXPERIENCE_TRAIT
{
	name = SuperIntelligence            // stored in the save as ProtoCrewMember.trait
	title = Super Intelligence          // shown in UI; ALSO what stock code compares (see "TypeName" below)
	desc = Claudia combines the skills of a pilot, an engineer and a scientist.
	// icon = ClaudiaKermin/Icons/superIntelligence   // optional texture path (no extension)

	// --- Pilot ---
	EFFECT { name = AutopilotSkill }
	EFFECT { name = FullVesselControlSkill }
	// --- Engineer ---
	EFFECT { name = RepairSkill }
	EFFECT { name = FailureRepairSkill }
	EFFECT { name = ConverterSkill }
	EFFECT { name = DrillSkill }
	// --- Scientist ---
	EFFECT
	{
		name = VesselScienceReturn
		modifiers = 1.05, 1.1, 1.15, 1.2, 1.25
	}
	EFFECT
	{
		name = PartScienceReturn
		modifiers = 1.05, 1.1, 1.15, 1.2, 1.25
	}
	EFFECT { name = ScienceSkill }
	EFFECT { name = ScienceResetSkill }
	EFFECT { name = SpecialExperimentSkill }
	EFFECT { name = ExternalExperimentSkill }
	// --- shared, best-of ---
	EFFECT
	{
		name = GeeForceTolerance
		modifiers = 1.3, 1.45, 1.6, 1.85, 2.0   // Pilot's (best) values
	}
	EFFECT
	{
		name = EVAChuteSkill
		level = 0                               // Pilot's: usable from level 0
	}
}
```

Notes on the shape:

- There is **no `RepackSkill`** in KSP 1.12.5. The stock effect classes are AutopilotSkill, ConverterSkill, DeployedScienceExpSkill, DeployedSciencePowerSkill, DrillSkill, EVAChuteSkill, EnginePower, ExternalExperimentSkill, FailureRepairSkill, FuelUsage, FullVesselControlSkill, GeeForceTolerance, HeatProduction, MaxThrottle, PartScienceReturn, RepairSkill, ScienceResetSkill, ScienceSkill, SpecialExperimentSkill and VesselScienceReturn ([decompiled `Experience/Effects/`][effects]). Parachute repacking is gated on `RepairSkill` level >= 1 ([ModuleParachute.cs L252][parachute]), so a level-0 Engineer, or Super Intelligence, cannot repack until level 1.
- Leave out the **Breaking Ground** effects `DeployedScienceExpSkill` / `DeployedSciencePowerSkill` until their stock `modifiers` are copied from an installed copy of the game. `GetValue()` indexes `LevelModifiers`, which is null when no `modifiers` are given ([DeployedScienceExpSkill.cs][dse]). I could not find the Serenity config that adds them to Scientist/Engineer in any public repo **(uncertain where it lives)**. Look in your own `GameData/SquadExpansion/Serenity/`.
- Don't repeat an effect name inside one trait.
- Optionally add `public class SuperIntelligence : Experience.ExperienceTrait {}` in our DLL. Then `TypeName` returns `"SuperIntelligence"` (the class name) instead of the title. That makes it stable across languages, and the same as `trait` (see below).

## Sources and their reliability

The official API docs at `kerbalspaceprogram.com/ksp/api/` 404 (see the #7 research). I used:

1. **Stock `GameData/Squad/Experience/Traits.cfg` (1.12.5)**: from the KSP-UE5 GameData mirror ([link][traitscfg]). It is byte-identical to the copy in [rettoph/KerbalSpaceProgram-Config][traitscfg2] (I checked with `diff`).
2. **KSP-UE5 decompiled `Assembly-CSharp` (1.12.5)**: <https://github.com/petmav/KSP-UE5/tree/main/decompiled/KSP-Decompiled/Assembly-CSharp>. Unofficial decompiled code. Strong evidence of behaviour, but confirm edge cases in ILSpy against your own DLL.
3. **Mod sources on GitHub**: Kerbals of All Trades, Community Trait Icons, ModuleManager, Contract Configurator, Kerbalism, kOS, and the mods listed in the name-check table (links inline).

Line numbers refer to the decompiled files at `main` as of 2026-10-23.

## Detail

### Stock Traits.cfg (1.12.5)

[Source][traitscfg]. Abridged:

```
EXPERIENCE_TRAIT { name = Pilot     title = #autoLOC_500101  // Pilot
	EFFECT { name = AutopilotSkill }  EFFECT { name = FullVesselControlSkill }
	EFFECT { name = GeeForceTolerance  modifiers = 1.3, 1.45, 1.6, 1.85, 2.0 }
	EFFECT { name = EVAChuteSkill  level = 0 } }
EXPERIENCE_TRAIT { name = Engineer  title = #autoLOC_500103  // Engineer
	EFFECT { name = RepairSkill }  EFFECT { name = ConverterSkill }  EFFECT { name = DrillSkill }
	EFFECT { name = FailureRepairSkill }
	EFFECT { name = GeeForceTolerance  modifiers = 1.1, 1.2, 1.3, 1.4, 1.5 }
	EFFECT { name = EVAChuteSkill  level = 1 } }
EXPERIENCE_TRAIT { name = Scientist title = #autoLOC_500105  // Scientist
	EFFECT { name = VesselScienceReturn  modifiers = 1.05, 1.1, 1.15, 1.2, 1.25 }
	EFFECT { name = PartScienceReturn    modifiers = 1.05, 1.1, 1.15, 1.2, 1.25 }
	EFFECT { name = ScienceSkill }  EFFECT { name = ScienceResetSkill }
	EFFECT { name = SpecialExperimentSkill }  EFFECT { name = ExternalExperimentSkill }
	EFFECT { name = GeeForceTolerance  modifiers = 1.1, 1.2, 1.3, 1.4, 1.5 }
	EFFECT { name = EVAChuteSkill  level = 1 } }
EXPERIENCE_TRAIT { name = Tourist   title = #autoLOC_500107
	EFFECT { name = GeeForceTolerance  modifiers = 0.75, 0.75, 0.75, 0.75, 0.75 } }
```

There is prior art for multi-role traits. [Kerbals of All Trades][koat] defines `PilotEngineer`, `PilotScientist`, `EngineerScientist` and `MissionLeader` (all effects) as plain new `EXPERIENCE_TRAIT` nodes. [5thHorseman's jackOfManyTrades.cfg][jack] instead adds effects to the stock traits with MM `@EXPERIENCE_TRAIT[Pilot] { EFFECT {...} }`. That changes every Kerbal of that trait, so it is wrong for us.

### How KSP loads traits (no MM needed)

- `ExperienceSystemConfig.LoadTraitConfigs()` calls `GameDatabase.Instance.GetConfigNodes("EXPERIENCE_TRAIT")`. Each node with a new `name` becomes a trait and is added to `TraitNames` and, unless it is "Tourist", to `TraitNamesNoTourist`. If a node has the *same* name as an earlier one, its effects are **merged** into that trait (duplicate effects are rejected with an error). So even adding effects to a trait works without MM ([ExperienceSystemConfig.cs L25-L78][esc]).
- `ExperienceTraitConfig.Create` reads `name` (required), `title`, `desc` or `description`, `icon` (texture path, loaded with `GameDatabase.GetTexture`) and the `EFFECT` sub-nodes ([ExperienceTraitConfig.cs][etc]). An `EFFECT` takes `name`, optional `modifiers` (one value per level 1..5; level 0 uses the effect's default) and optional `level` ([ExperienceEffect.cs `LoadFromConfig`][ee]).
- `#autoLOC` values are localized when configs load (`GameDatabase.translateLoadedNodes` → `Localizer.TranslateBranch`, [GameDatabase.cs L1087][gdb]). Our title can be a literal string or our own `#autoLOC` key.
- ModuleManager re-runs `GameDatabase.Instance.ExperienceConfigs.LoadTraitConfigs()` after it applies patches ([MM PostPatchLoader.cs][mm]). Any C# that edits the trait lists must therefore run *after* MM's post-load, for example in a `KSPAddon(Startup.MainMenu)`.
- A trait with no C# class is built as the base `ExperienceTrait` (`GetExperienceTraitType` finds no subclass with that name, [KerbalRoster.cs L1019-L1024][kr1019]). Stock Pilot, Engineer and Scientist have no subclasses either: I found no `: ExperienceTrait` subclass anywhere in the decompiled assembly. A custom trait is therefore a first-class citizen, just like the stock ones.

### `trait`, `Name`, `Title` and `TypeName` (important for name checks)

- `ProtoCrewMember.trait` is the trait's config `name` ("SuperIntelligence"). It is saved to the roster.
- `experienceTrait.TypeName` returns **`Config.Title`** when the trait has no C# class, and otherwise the class name ([ExperienceTrait.cs L17-L28][et]). Most stock and mod code compares `TypeName`, not `name`. For stock traits in English, title == name, so this works. For us, `TypeName` would be "Super Intelligence" unless we add the `SuperIntelligence : ExperienceTrait` class.
- Some code compares `pcm.trait` (Kerbalism, the VAB crew picker), and some compares `experienceTrait.Config.Name` (Contract Configurator `HasCrew`, Community Trait Icons). Whatever we do, none of these will ever match "Pilot", "Engineer" or "Scientist".
- (Side note, uncertain impact) Several stock and mod checks compare `TypeName` with an English literal, while `TypeName` is the *localized* title. This suggests those checks may already misbehave in non-English installs. Near Future Electrical and SystemHeat instead compare against `Localizer.Format("#autoLOC_500103")`.

### Stock checks: effect-based (Claudia passes)

| Feature | Check | Source |
|---|---|---|
| Mobile Processing Lab: crew needed to run | `part.GetCrewCountOfExperienceEffect<ScienceSkill>() < crewsRequired` | [ModuleScienceLab.cs L203, L374][lab] |
| Lab/science converter rate bonus | `HasEffect<ScienceSkill>()` | [ModuleScienceConverter.cs L171][conv] |
| Reset experiments on EVA | `HasEffect<ScienceResetSkill>()` | [ModuleScienceExperiment.cs L1072][mse] |
| Surface samples / special experiments | `CrewListHasEffect<SpecialExperimentSkill>` | [ScienceUtil.cs L414, L427][su] |
| Collect external experiment data on EVA | `HasEffect<ExternalExperimentSkill>()` | [ScienceUtil.cs L502][su] |
| Probe-free vessel control | `HasEffect<FullVesselControlSkill>()` | [ModuleCommand.cs L524][cmd] |
| SAS levels | `AutopilotSkill` feeds `PartValues.AutopilotSkill` | [AutopilotSkill.cs][aps] |
| Repack parachutes | `VesselValues.RepairSkill.value >= 1` | [ModuleParachute.cs L252][parachute] |
| Repair wheels / deployables (solar panels, etc.) | `VesselValues.RepairSkill` | [ModuleWheelDamage.cs L454-L464][wheel], [ModuleDeployablePart.cs L409-L419][dep] |
| Enter EVA construction mode | `VesselValues.RepairSkill.value >= 0` | [EVAConstructionModeController.cs L353][evac] |
| Making History failure repair | `VesselValues.FailureRepairSkill.value >= 1` | [AdjusterPartModuleBase.cs L139][adj] |
| Converters and drills (ISRU, drills) | `BaseConverter.ExperienceEffect = "ConverterSkill"` and `HasEffect(effect)` | [BaseConverter.cs L78, L658][bc] |
| KerbNet | `HasEffect(effect)` | [ModuleKerbNetAccess.cs L426][kn] |
| Breaking Ground deployed science | `HasEffect<DeployedScience*Skill>()` | [ModuleInventoryPart.cs L1835-L1847][inv] |

### Stock checks: name-based (Claudia fails without a workaround)

| Feature | Check | Source |
|---|---|---|
| Contract "have a Scientist/Engineer aboard" | `CrewTraitParameter` calls `VesselUtilities.VesselCrewWithTraitCount(targetTrait, ...)`, which compares `experienceTrait.TypeName == trait` | [CrewTraitParameter.cs L73, L179][ctp], [VesselUtilities.cs L901-L917][vu] |
| Comet sample contract (needs a Scientist) | `new CrewTraitParameter("Scientist", 1, ...)` | [CometSampleContract.cs L52][comet] |
| Vessel repair / orbital construction contracts (need an Engineer) | `new CrewTraitParameter("Engineer", 1, ...)` | [VesselRepairContract.cs L244][vrc], [OrbitalConstructionContract.cs L181][occ] |
| Station / base / satellite side requests ("CREW_REQUEST" Pilot/Engineer/Scientist/Tourist) | `CrewTraitMissionAvailability` and `VesselCrewWithTraitCount` on the `Trait` string; availability counts roster Kerbals by `TypeName` | [SystemUtilities.cs L329-L332, L414-L418][sys], [ContractDefs.cs L3439-L3500][cdefs], [VesselUtilities.cs L920-L1000][vu] |
| EVA welding | `if (VesselUtilities.VesselCrewWithTraitCount("Engineer") <= 0) { "Kerbal is not an engineer!"; return; }` | [KerbalEVA.cs L6131][weld] |
| EVA construction combined mass limit | nearby Kerbals count only if `VesselCrewWithTraitCount("Engineer", vessel) > 0`, unless `EVA_CONSTRUCTION_COMBINE_NONENGINEERS` | [EVAConstructionModeEditor.cs L1278-L1285][evae] |
| VAB/SPH default crew fills seats with a Pilot first | `c.trait == pilotTrait` | [KerbalRoster.cs L495, L579, L592][kr495] |
| Making History mission editor crew levels / restrictions | `switch (trait) case "Scientist"/"Engineer"/"Pilot"` | [CrewGenerator.cs L57-L63][cg], `Expansions/Missions/VesselRestriction_Crew.cs` |
| Wheel-damage message names "Engineer" | Message text only, not a gate | [ModuleWheelDamage.cs L478][wheel] |
| Localized trait name helper | Unknown traits fall through to `#autoLOC_8000022` (display only) | [KerbalRoster.cs L1207-L1222][kr1207] |

Display-only uses: the flight portrait shows `TypeName`, the Astronaut Complex and crew list show `Title` ([KerbalPortrait.cs L133][portrait], [CrewListItem.cs L181][cli]). kOS `CREWMEMBER:TRAIT` returns `experienceTrait.Title` ([kOS CrewMember.cs][kos]). Claudia will display as "Super Intelligence" everywhere, which is the goal.

### Random trait assignment (the hidden catch)

`KerbalRoster.SetExperienceTrait(pcm, null)` gives a crew Kerbal with no trait one chosen as `TraitNamesNoTourist[abs(hash(name)) % count]` ([KerbalRoster.cs L982-L1000][kr982]). `CrewGenerator.RandomCrewMemberPrototype` calls exactly that for every new applicant or generated Kerbal ([CrewGenerator.cs][cg]). Our trait sits in `TraitNamesNoTourist` because `LoadTraitConfigs` adds every non-Tourist trait ([ExperienceSystemConfig.cs L64-L71][esc]). So with our cfg installed, about 25% of new Kerbals (rescues, applicants, contract Kerbals that use stock generation) would roll Super Intelligence.

Fixes, in order of preference:

1. After MM's post-load (e.g. `KSPAddon(Startup.MainMenu, once: true)`), run `GameDatabase.Instance.ExperienceConfigs.TraitNamesNoTourist.Remove("SuperIntelligence")`. The property returns the live private list ([ExperienceSystemConfig.cs L18][esc]), and the trait stays in `Categories`/`TraitNames`, so `SetExperienceTrait(claudia, "SuperIntelligence")` still works. **(Uncertain:** other mods iterating `TraitNamesNoTourist` would also stop seeing it. That is probably what we want. I found one mod that reads the list, [KerbalRelations EggManager.cs][kr-egg].)
2. A fallback that needs no list surgery: subscribe to `GameEvents.onKerbalAdded` and re-roll the trait of any non-Claudia Kerbal that got ours. [RosterManager RMLifeSpanAddon.cs][rm] is an example of a mod that sets traits from that event.

### Workarounds for name-based checks

- **Stock (one Harmony patch set):** every stock name gate above except the crew picker goes through `FinePrint.Utilities.VesselUtilities.VesselCrewWithTraitCount`, `VesselCrewWithTrait` or `CrewTraitMissionAvailability` ([VesselUtilities.cs L883-L1000][vu]). Postfix them to also count Super Intelligence crew when `trait` is "Pilot", "Engineer" or "Scientist". That unlocks contract requirements, welding and the combine bonus. Side effect: `CrewTraitMissionAvailability` will count her for all three. Station and base contracts may then generate crew requests a little more often (inferred). Harmony is already on the table from #7 (Astronaut Complex X button).
- **Mods with a configurable trait string:** MM-patch their configs so they accept our trait.
  - Kerbalism crew specs accept a comma-separated OR list (`"Engineer,SuperIntelligence@2"`) and compare `pcm.trait` ([CrewSpecs.cs][kbs]). This is in the current `Kerbalism/Kerbalism` master; older 3.x builds may not support lists **(uncertain)**.
  - StationScience `requiredTrait` ([StationScienceModule.cs][stsci]), SSPX `CrewSkillNeeded` ([ModuleDeployableHabitat.cs][sspx]) and WildBlue `skillRequired` ([WBITemplateConverter.cs][wbi]) compare `TypeName` against a single configurable string. Pointing them at our title would lock out real Engineers, so these need Harmony or an accepted limitation.
  - Contract Configurator `HasCrew` compares `Config.Name` ([HasCrew.cs L165-L166][cc]); `HasAstronaut` compares `TypeName` ([HasAstronaut.cs][cc2]). Contract packs ask for `trait = Engineer`, so she won't count there either.
- **Mods with hardcoded names** (Harmony or accept): Kerbal Engineer Redux's career flight readouts need an "Engineer" aboard ([FlightEngineerCore.cs][ker]). Also USI `ModuleAutoRepairer` / Konstruction foreman / `LogisticsTools` ([USITools][usi1], [Konstruction][usi2]), USI-LS cryo needs a "Scientist" ([ModuleCryoFreeze.cs][usi3]), Near Future Electrical reactor and radioactive storage ([FissionReactor.cs][nfe]), SystemHeat ([ModuleUtils.cs][sh]) and Weather Driven Solar Panel dust cleaning ([WDSPDustCleaning.cs][wdsp]).
- **Icons:** Community Trait Icons maps trait `name` to an icon and colour via a `CommunityTraitIcons { Trait { name = ... icon = ... color = ... } }` node, and skips names that aren't in `TraitNames` ([CommunityTraitIcons.cfg][cti], [CTIAddon.cs][cti2]). We can ship an MM-free `Trait` entry for `SuperIntelligence` (inferred: it is read as a second root node of the same name, so check the merge behaviour), or `:NEEDS[CommunityTraitIcons]` with MM.
- **Rejected alternative:** keep her a stock Pilot and add effects with `@EXPERIENCE_TRAIT[Pilot]`. That gives every Pilot the powers. There is no per-Kerbal effect list in stock: effects hang off the trait config ([ExperienceTraitConfig.cs][etc]).
- **Hacky alternative:** switch her trait at runtime (e.g. to "Engineer" while on EVA) with `KerbalRoster.SetExperienceTrait`. It is fragile and visible in the UI. Not recommended.

### Experience levels by game mode

- The trait has no bearing on XP. XP comes from `careerLog` entries (Flight, Orbit, Land, PlantFlag, and so on, per body) and `CalculateExperienceLevel`. The level thresholds are the same for every trait ([KerbalRoster.cs L1086-L1110, L1228+][kr1050]).
- `AdvancedParams.KerbalExperienceEnabled(mode)` is true by default only in Career and Mission, and can be toggled with the difficulty option "Enable Kerbal Experience" ([GameParameters.cs L334-L346][gp]). When it is off (default Science and Sandbox), `CalculateExperiencePoints` returns 99999, so she is level 5 with every effect at its maximum. When it is on, she starts at 0 like a new hire ([ProtoCrewMember.cs L1283-L1294][pcm]).
- The mod can give her a starting level with `KerbalRoster.SetExperienceLevel(pcm, level)`. It writes Training1..5 entries into her career log ([KerbalRoster.cs L1054-L1083][kr1050]). The difficulty option "Kerbals level up immediately" (`ImmediateLevelUp`) also counts the current flight log.
- Effect scaling: `AutopilotSkill` and `RepairSkill` use the raw level (SAS modes and repair tiers unlock by level). `GeeForceTolerance` and the science returns use `modifiers[level]`. `EVAChuteSkill` uses `level` as a minimum ([AutopilotSkill.cs][aps], [RepairSkill.cs][rs], [EVAChuteSkill.cs][evachute]). A level-0 Super Intelligence Kerbal in Career can fly with stability assist only, cannot repack chutes or fix wheels, and has a +0% science bonus, the same as a level-0 Pilot, Engineer or Scientist.

### Uninstall and save compatibility (inferred)

When a save is loaded, `ProtoCrewMember` calls `SetExperienceTrait(this)` with its stored `trait` ([ProtoCrewMember.cs L615][pcm]). If no category has that name, the method returns early and leaves `experienceTrait` null ([KerbalRoster.cs L1003-L1018][kr982]). Many callers then dereference `experienceTrait.TypeName` (for example `VesselUtilities`). Removing the mod therefore probably throws NullReferenceExceptions until her trait is edited in the save. **(Not tested.)**

## Open questions for a decision ticket

1. Should we accept Harmony as a dependency (it already came up in #7), so the stock `VesselUtilities` name checks count her as Pilot, Engineer and Scientist? Or should we accept that contracts and welding ignore her?
2. Which mods do we support with compatibility patches (Kerbalism, KER, USI, Contract Configurator, NFE, SystemHeat)? Is support MM `:NEEDS` patches only, or Harmony too?
3. Trait `name` and `title`: should `name = SuperIntelligence` with a C# `SuperIntelligence : ExperienceTrait` class (stable `TypeName`), or should name and title both be "Super Intelligence"? MM selectors then need `Super?Intelligence`.
4. What is her starting experience level in Career? Level 0 makes her weak: no repack, SAS stability only. Pre-training her to 3 to 5 has a balance cost.
5. Should we include the Breaking Ground effects (`DeployedScienceExpSkill` / `DeployedSciencePowerSkill`), and with which `modifiers`? Someone needs to read them from an installed `SquadExpansion/Serenity` config.
6. How much is she worth? A one-Kerbal crew of Super Intelligence makes Pilots, Engineers and Scientists redundant. Balance or "cheat" option?
7. What is the uninstall story: a cleanup that resets her trait to Pilot before removal, or a documented save-breaking change?

[traitscfg]: https://github.com/petmav/KSP-UE5/blob/main/core/testdata/GameData-mirror/GameData/Squad/Experience/Traits.cfg
[traitscfg2]: https://github.com/rettoph/KerbalSpaceProgram-Config/blob/HEAD/GameData/Squad/Experience/Traits.cfg
[koat]: https://github.com/zer0Kerbal/KerbalsofAllTrades/blob/HEAD/GameData/ExodusSolis/KerbalsOfAllTrades/Experience/Traits.cfg
[jack]: https://github.com/Kerbal-Workshop/MyKSPMods/blob/HEAD/My_MMConfigs/AllOthers/jackOfManyTrades.cfg
[effects]: https://github.com/petmav/KSP-UE5/tree/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/Effects
[esc]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/ExperienceSystemConfig.cs
[etc]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/ExperienceTraitConfig.cs
[ee]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/ExperienceEffect.cs
[et]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/ExperienceTrait.cs#L17-L28
[dse]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/Effects/DeployedScienceExpSkill.cs
[aps]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/Effects/AutopilotSkill.cs
[rs]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/Effects/RepairSkill.cs
[evachute]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Experience/Effects/EVAChuteSkill.cs
[gdb]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/GameDatabase.cs#L1087-L1093
[mm]: https://github.com/sarbian/ModuleManager/blob/master/ModuleManager/PostPatchLoader.cs
[kr1019]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs#L1019-L1024
[kr982]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs#L982-L1025
[kr495]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs#L495
[kr1050]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs#L1050-L1110
[kr1207]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs#L1207-L1222
[cg]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/CrewGenerator.cs#L37-L72
[pcm]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ProtoCrewMember.cs#L1283-L1294
[gp]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/GameParameters.cs#L334-L346
[lab]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleScienceLab.cs#L203
[conv]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleScienceConverter.cs#L171
[mse]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleScienceExperiment.cs#L1072
[su]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ScienceUtil.cs#L414
[cmd]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleCommand.cs#L524
[parachute]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleParachute.cs#L252
[wheel]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleWheels/ModuleWheelDamage.cs#L454-L478
[dep]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleDeployablePart.cs#L409-L419
[evac]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/EVAConstructionModeController.cs#L353
[evae]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/EVAConstructionModeEditor.cs#L1278-L1285
[weld]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalEVA.cs#L6127-L6139
[adj]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Expansions/Missions/Adjusters/AdjusterPartModuleBase.cs#L139
[bc]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/BaseConverter.cs#L658
[kn]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleKerbNetAccess.cs#L426
[inv]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/ModuleInventoryPart.cs#L1835-L1847
[ctp]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/FinePrint/Contracts/Parameters/CrewTraitParameter.cs#L73
[vu]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/FinePrint/Utilities/VesselUtilities.cs#L883-L1000
[comet]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/FinePrint/Contracts/CometSampleContract.cs#L52
[vrc]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Contracts/Templates/VesselRepairContract.cs#L244
[occ]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/Contracts/Templates/OrbitalConstructionContract.cs#L181
[sys]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/FinePrint/Utilities/SystemUtilities.cs#L315-L420
[cdefs]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/FinePrint/ContractDefs.cs#L3429-L3500
[portrait]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KSP/UI/Screens/Flight/KerbalPortrait.cs#L133
[cli]: https://github.com/petmav/KSP-UE5/blob/main/decompiled/KSP-Decompiled/Assembly-CSharp/KSP/UI/CrewListItem.cs#L181
[kos]: https://github.com/KSP-KOS/KOS/blob/develop/src/kOS/Suffixed/CrewMember.cs
[kr-egg]: https://github.com/Ni2Be/KerbalRelations/blob/HEAD/Source/KerbalRelations/Reproduction/EggManager.cs
[rm]: https://github.com/PapaJoesSoup/RosterManager/blob/HEAD/RosterManager/RMLifeSpanAddon.cs
[kbs]: https://github.com/Kerbalism/Kerbalism/blob/master/src/Kerbalism/Utility/CrewSpecs.cs
[stsci]: https://github.com/kethernet/StationScience/blob/HEAD/StationScienceModule.cs
[sspx]: https://github.com/post-kerbin-mining-corporation/StationPartsExpansionRedux/blob/HEAD/Source/HabUtils/ModuleDeployableHabitat.cs
[wbi]: https://github.com/Angel-125/WildBlueTools/blob/HEAD/Switchers/WBITemplateConverter.cs
[cc]: https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/Parameter/VesselParameter/HasCrew.cs#L162-L167
[cc2]: https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/Parameter/HasAstronaut.cs
[ker]: https://github.com/CYBUTEK/KerbalEngineer/blob/HEAD/KerbalEngineer/Flight/FlightEngineerCore.cs
[usi1]: https://github.com/UmbraSpaceIndustries/USITools/blob/HEAD/Source/USITools/Logistics/ModuleAutoRepairer.cs
[usi2]: https://github.com/UmbraSpaceIndustries/Konstruction/blob/HEAD/Source/Konstruction/EVA/ModuleKonstructionForeman.cs
[usi3]: https://github.com/UmbraSpaceIndustries/USI-LS/blob/HEAD/Source/USILifeSupport/Cryo/ModuleCryoFreeze.cs
[nfe]: https://github.com/post-kerbin-mining-corporation/NearFutureElectrical/blob/HEAD/Source/NearFutureElectrical/FissionReactor.cs
[sh]: https://github.com/KSPModStewards/SystemHeat/blob/HEAD/Source/ModuleUtils.cs
[wdsp]: https://github.com/Aebestach/WeatherDrivenSolarPanel/blob/HEAD/Source/WDSPDustCleaning.cs
[cti]: https://github.com/cake-pie/CommunityTraitIcons/blob/HEAD/GameData/CommunityTraitIcons/CommunityTraitIcons.cfg
[cti2]: https://github.com/cake-pie/CommunityTraitIcons/blob/HEAD/Source/CTIAddon.cs
