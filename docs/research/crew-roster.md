# Research: adding Claudia to the crew roster

Ticket: [#7 Adding Claudia to the crew roster](https://github.com/arathianevasion/claudia-kermin/issues/7)
Target: KSP 1.12.5 (C# plugin, `Assembly-CSharp`)
Researched: 2026-10-23

## Answer

- **How to add her.** Build a `ProtoCrewMember` of type `Crew`. Set its name, `gender = Female`, `trait = "Pilot"`, `veteran` and `isBadass`. Then call `HighLogic.CurrentGame.CrewRoster.AddCrewMember(pcm)`. The stock Alt+F12 debug "Create Kerbal" screen does exactly this. Contract Configurator uses another way: `GetNewKerbal(type)`, then `ChangeName`, set `gender`, then `KerbalRoster.SetExperienceTrait(pcm, trait)`.
- **Persistence is free.** The roster is written to the save file's `ROSTER` node as one `KERBAL` node per Kerbal, and read back on load. Once she has been added, KSP saves and reloads her like any stock Kerbal. The mod only needs an idempotent "ensure Claudia exists" check, for example in a `ScenarioModule` marked `[KSPScenario(AddToAllGames, ...)]` (the pattern Kerbal Health uses).
- **Watch the first-name check.** `AddCrewMember` silently refuses (returns `false`) if any existing roster key *contains* her first name "Claudia". For example, a randomly generated "Claudia Kerman" applicant would block her. The return value must be checked. A side effect: once she exists, the generator can never add another "Claudia ..." Kerbal.
- **Trait.** Recommend **Pilot**, type **Crew**. Do not make her a Tourist. Tourists are removed from the roster when they die, cannot be fired or reassigned in the usual way, and tourist type forces the Tourist trait.
- **Game modes.** A programmatic add costs no funds in any mode. Hiring cost only applies to the Astronaut Complex "hire" button in Career, and the cost formula depends on the active crew count. In Career she still counts against the Astronaut Complex active-crew limit (5 / 8 / 12 / unlimited by facility level) and raises the price of the next hire. Sandbox and Science have no Funding instance, so no cost is shown there. She appears in the Astronaut Complex "Available" list like any crew member, in all modes.
- **KIA and respawn.** If her part is destroyed, `Die()` sets her to `Dead`. If the difficulty option `MissingCrewsRespawn` is on, KSP immediately sets her to `Missing` with a respawn timer (`RespawnTimer`). She becomes `Available` again when it expires. If the option is off she stays `Dead`, in the KIA list, unless the mod intervenes.
- **Special marks.** `veteran` (orange suit, "badass" icon in the Astronaut Complex) and `isBadass` (courage/panic behaviour) are public fields that are saved. `isHero` is saved but has an `internal` setter and is only used by the Making History editor. **No stock flag prevents dismissal.** The Astronaut Complex shows the "fire" X on every available non-tourist crew member, Jeb included. To prevent dismissal, the mod must hook `GameEvents.OnCrewmemberSacked` and undo it, or patch the UI with Harmony.
- **Detecting her aboard.** In C#, use `FlightGlobals.ActiveVessel.GetVesselCrew()` and match her by name. Re-check on `onVesselChange`, `onVesselCrewWasModified`, `onCrewOnEva`, `onCrewBoardVessel` and `onKerbalNameChanged`. From kOS: `SHIP:CREW()` returns `CrewMember` structures with `NAME`, `GENDER`, `TRAIT`, `EXPERIENCE`, `TOURIST` and `PART`.

## Sources and their reliability

The official API docs at `kerbalspaceprogram.com/ksp/api/` currently return HTTP 404 for every class page (checked 2026-10-23), and the Wayback Machine has no snapshot of the relevant pages. In their place I used:

1. **KSP-Documentation-Effort, stripped signatures**: <https://github.com/ProgrammerFailure/KSP-Documentation-Effort/tree/HEAD/StrippedCSFiles>. Public member signatures only. It includes 1.11+ members (`KerbalSuit.Slim`, `KerbalInventoryModule`), so it matches the 1.12 API surface.
2. **KSP-UE5 decompiled Assembly-CSharp**: <https://github.com/petmav/KSP-UE5/tree/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp>. A public third-party decompilation that its description says is of KSP 1.12.5. I used it to read *behaviour* (method bodies). It is decompiled, unofficial code: treat it as strong evidence, but confirm against your own `Assembly-CSharp.dll` in ILSpy before relying on edge cases.
3. **Real mods**: Contract Configurator, Kerbal Health, and kOS docs (links inline below).

Claims marked **(uncertain)** are inferences I did not verify directly.

## Detail

### API surface (ProtoCrewMember / KerbalRoster)

From the stripped [`ProtoCrewMember.cs`](https://github.com/ProgrammerFailure/KSP-Documentation-Effort/blob/HEAD/StrippedCSFiles/ProtoCrewMember.cs) and [`KerbalRoster.cs`](https://github.com/ProgrammerFailure/KSP-Documentation-Effort/blob/HEAD/StrippedCSFiles/KerbalRoster.cs):

- Enums:
  - `ProtoCrewMember.KerbalType { Crew, Applicant, Unowned, Tourist }`
  - `RosterStatus { Available, Assigned, Dead, Missing }`
  - `Gender { Male, Female }`
  - `KerbalSuit { Default, Vintage, Future, Slim }`
- Constructors: `ProtoCrewMember(KerbalType type, string newName)`, `ProtoCrewMember(Game.Modes, ConfigNode, KerbalType)`.
- Fields and properties: `name`, `gender`, `trait`, `type`, `rosterStatus`, `veteran`, `isBadass`, `isHero` (internal set), `courage`, `stupidity`, `experienceLevel`, `suit`, `inactive`, `KerbalRef`, `seat`, `seatIdx`.
- `ProtoCrewMember` methods: `ChangeName(string)`, `Die()`, `StartRespawnPeriod(double)`, `SetTimeForRespawn(double)`, `SetInactive(double, bool)`.
- `KerbalRoster` instance members: `AddCrewMember`, `GetNewKerbal(KerbalType)`, `Exists(string)`, `Remove(string|pcm)`, `this[string name]`, `Crew`, `Tourist`, `Applicants`, `Unowned`, `Kerbals(type, params status[])`, `GetActiveCrewCount()`, `HireApplicant`, `SackAvailable`, `RemoveMIA`, `RemoveDead`.
- `KerbalRoster` static members: `SetExperienceTrait(pcm, traitName)`, `SetExperienceLevel(pcm, level)`.
- Trait name constants: `KerbalRoster.pilotTrait = "Pilot"`, `engineerTrait = "Engineer"`, `scientistTrait = "Scientist"`, `touristTrait = "Tourist"`. Source: [decompiled KerbalRoster.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs).
- The roster is reached through `HighLogic.CurrentGame.CrewRoster`. Source: [decompiled Game.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/Game.cs).

### Creating her: two proven patterns

**A. Construct and add.** This is the stock debug "Create Kerbal" screen, `ScreenKerbalCreate.OnSubmitClicked` ([source](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KSP/UI/Screens/DebugToolbar/Screens/GamePlay/ScreenKerbalCreate.cs)). It builds `new ProtoCrewMember(KerbalType.Crew, name)`, sets `rosterStatus = Available`, `gender`, `trait`, `courage`, `stupidity`, `veteran`, `isBadass` and `hasToured = false`, then calls `KerbalRoster.SetExperienceLevel(pcm, level)` and `CrewRoster.AddCrewMember(pcm)`.

Before submitting, the same screen also checks `CrewRoster.Exists(name)` and, outside Sandbox, `GetActiveCrewCount() < GameVariables.Instance.GetActiveCrewLimit(astronautComplexLevel)`. That is a good model for our own checks.

Sketch (not compiled):

```csharp
var roster = HighLogic.CurrentGame.CrewRoster;
if (!roster.Exists("Claudia Kermin")) {
    var pcm = new ProtoCrewMember(ProtoCrewMember.KerbalType.Crew, "Claudia Kermin") {
        gender = ProtoCrewMember.Gender.Female,
        trait = KerbalRoster.pilotTrait,
        rosterStatus = ProtoCrewMember.RosterStatus.Available,
        courage = 0.5f, stupidity = 0.1f, veteran = true, isBadass = true,
    };
    if (!roster.AddCrewMember(pcm)) { /* name clash, see below */ }
}
```

**B. Generate, then rename.** This is Contract Configurator's `Kerbal.GenerateKerbal()` ([source](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ExpressionParser/Wrappers/Kerbal.cs)). It calls `CrewRoster.GetNewKerbal(kerbalType)`, sets `gender`, calls `ChangeName(name)`, sets `trait`, then calls `KerbalRoster.SetExperienceTrait(pcm, trait)`.

`GetNewKerbal` loops `CrewGenerator.RandomCrewMemberPrototype(type)` until `AddCrewMember` accepts one ([decompiled](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs)). The Kerbal then gets a random trait, courage and stupidity, which pattern B overrides.

### Name-clash behaviour (important)

In decompiled `KerbalRoster.AddCrewMember`, the method strips the last name from `crew.name` and returns `false` if *any existing roster key contains that first name*. On success it fires `onKerbalAdded`, adds the Kerbal, and fires `onKerbalAddComplete`.

So "Claudia Kermin" is rejected if the roster already holds any name containing "Claudia". Such a name could come from a random applicant, or a Kerbal the player made or renamed.

`ChangeName` only rejects an *exact* duplicate key ([`ChangeNameCalledFromPCM`](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs)). Pattern B therefore gets around the first-name check, but it can still fail on an exact duplicate.

Consequences:

- The mod must handle a `false` return.
- Once Claudia exists, the stock generator cannot create another "Claudia ..." Kerbal, which is a nice side effect.

### Trait choice

`SetExperienceTrait` has these rules ([decompiled](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs)):

- A `Tourist`-type Kerbal is forced to the Tourist trait.
- A non-tourist asking for "Tourist" has the trait cleared.
- An empty trait is picked from the non-tourist traits by hashing the name.

Valid values come from the `EXPERIENCE_TRAIT` configs (`GameDatabase.Instance.ExperienceConfigs`). That means trait mods could add others **(uncertain: I did not check whether a custom "MissionController" trait config is practical)**.

Pilot gives SAS-hold skills. kOS does not need them: steering comes from the kOS processor. So the trait is about flavour and game-mechanics balance, not a requirement **(uncertain: inference, not tested)**.

### Persistence

- `Game.Save` writes `CrewRoster.Save(configNode.AddNode("ROSTER"))`. Loading reads `new KerbalRoster(node.GetNode("ROSTER"), Mode)` and runs `CrewRoster.ValidateAssignments` and `CrewRoster.Init` ([decompiled Game.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/Game.cs)).
- Each Kerbal is saved as a `KERBAL` node. Its keys include `name`, `gender`, `type`, `trait`, `brave`, `dumb`, `badS`, `veteran`, `tour`, `state`, `inactive`, `ToD` (respawn UT), `extraXP`, `suit` and `hero` ([decompiled ProtoCrewMember.Save](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/ProtoCrewMember.cs)).
- `GameEvents.onProtoCrewMemberSave` and `onProtoCrewMemberLoad` fire with the `KERBAL` ConfigNode. A mod could use them to add its own key, for example `claudiaKermin = True`. On load, the event fires at the start of the constructor, before stock parsing. **(uncertain)** Whether unknown keys survive a save cycle without the mod re-adding them: probably not, because Save rebuilds the node from fields.
- **Recommendation.** Identify her by a name or ID stored in our own `ScenarioModule`, rather than relying on custom keys in `KERBAL` nodes. Kerbal Health uses `[KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.FLIGHT, GameScenes.EDITOR)]` and subscribes to the crew events ([KerbalHealthScenario.cs](https://github.com/GarwelGarwel/KerbalHealth/blob/master/KerbalHealthScenario.cs)).

### Sandbox / Science / Career

- **Cost.** `GameVariables.GetRecruitHireCost(currentActive)` uses a base of 2000 plus a log curve on the active crew count, multiplied by `Career.FundsLossMultiplier` ([decompiled GameVariables.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/GameVariables.cs)). It is charged only in the Astronaut Complex hire flow, through `CurrencyModifierQuery(TransactionReasons.CrewRecruited, ...)` ([decompiled AstronautComplex.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KSP/UI/Screens/AstronautComplex.cs)). `AddCrewMember` charges nothing. If we want Claudia to "cost" something in Career, the mod must deduct funds itself.
- **Hire-cost display.** The Astronaut Complex hides the hire-cost field when `Funding.Instance == null`, which is Sandbox and Science (same source).
- **Crew limit.** `GetActiveCrewLimit` returns 5, 8, 12 or `int.MaxValue` depending on the Astronaut Complex level. The debug creator skips the limit only in Sandbox. `GetActiveCrewCount` counts `Crew` Kerbals that are Assigned, Available or Missing, so Claudia uses up a slot and raises the next hire's price. **(uncertain)** How Science mode facility levels behave for this limit.
- **Astronaut Complex lists.** The lists are Applicants, Available (Crew plus Tourists), Assigned and KIA (Crew that are `Dead` or `Missing`). Claudia shows up with no extra work.
- **Experience.** When Kerbal experience is disabled (Sandbox default; `AdvancedParams.KerbalExperienceEnabled(mode)`), Kerbals act as level 5. The debug creator pins level 5 in that case ([ScreenKerbalCreate.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KSP/UI/Screens/DebugToolbar/Screens/GamePlay/ScreenKerbalCreate.cs)). In Career/Science, `KerbalRoster.SetExperienceLevel` can give her starting stars.

### KIA, MIA and respawn

All sources in this section are decompiled.

- `Part` destruction calls `pcm.Die()` for each crew member. If `Difficulty.MissingCrewsRespawn` is on, it then calls `StartRespawnPeriod()`, using the part's `crewRespawnTime` or `Difficulty.RespawnTimer` ([Part.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/Part.cs)).
- `Die()` logs the death, sets `Dead`, and fires `onCrewKilled`. **If the Kerbal is a Tourist, it removes them from the roster.** `StartRespawnPeriod` sets `Missing` and `UTaR`. `CheckRespawnTimer` turns `Missing` into `Available` once the UT passes, or calls `Die()` if respawn is off ([ProtoCrewMember.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/ProtoCrewMember.cs)).
- `ValidateAssignments` on load: an Assigned Kerbal not found in any vessel gets `StartRespawnPeriod(2000)`. A vessel that fails to load starts respawn for its crew ([ProtoVessel.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/ProtoVessel.cs)).
- **Player actions.** `RemoveMIA` (Missing to Dead) and `RemoveDead` (deletes the Kerbal from the roster) are what the Astronaut Complex KIA list uses.
- **What the mod can do.** It can listen to `onKerbalStatusChange` and `onKerbalRemoved` and re-create or revive her, for example by setting `rosterStatus = Available` or calling `SetTimeForRespawn(ut)`. Whether Claudia can permanently die is a design decision.

### Special marking and preventing dismissal

- `veteran` changes the suit texture (`Kerbal` uses `textureVeteran`, the orange suit, when it is true) ([Kerbal.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/Kerbal.cs)). It also makes the Assigned list use the `BADASS` icon ([AstronautComplex.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KSP/UI/Screens/AstronautComplex.cs)). The stock four (Jeb, Bill, Bob, Val) are created with `veteran = true` and `isHero = true`. Jeb and Val also get `isBadass = true` (`GenerateInitialCrewRoster`, [KerbalRoster.cs](https://github.com/petmav/KSP-UE5/blob/HEAD/decompiled/KSP-Decompiled/Assembly-CSharp/KerbalRoster.cs)).
- `isHero` has an `internal` setter. The only other code that reads it is the Making History `MECrewAssignmentDialog`. A mod cannot set it except through reflection or the `hero` save key, and it buys nothing.
- **Dismissal.** `AstronautComplex.AddItem_Available` gives every non-Tourist Kerbal the X (fire) button, with no veteran check. `SackAvailable` turns `type` from Crew to Applicant and fires `OnCrewmemberSacked`. Ways to protect her:
  1. Handle `GameEvents.OnCrewmemberSacked` (or `onKerbalTypeChange`) for Claudia and set `type` back to `Crew`. This is simple, but she briefly flickers into the Applicants list until the UI refreshes. **(uncertain about the UI refresh)**
  2. Use a Harmony postfix on `AstronautComplex.AddItem_Available` to hide her X button. This is cleaner, but adds a dependency on Harmony.
- **Other protection.** Renaming happens via the debug UI or other mods. `onKerbalNameChanged` lets us track or revert a rename.

### Detecting her aboard the active vessel

- C#: `FlightGlobals.ActiveVessel.GetVesselCrew()` returns a `List<ProtoCrewMember>` ([stripped Vessel.cs](https://github.com/ProgrammerFailure/KSP-Documentation-Effort/blob/HEAD/StrippedCSFiles/Vessel.cs)). Contract Configurator finds a Kerbal's vessel with `FlightGlobals.Vessels.Where(v => v.GetVesselCrew().Contains(pcm))` ([Kerbal.cs](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ExpressionParser/Wrappers/Kerbal.cs)).
- **On EVA** she is her own vessel (`isEVA`), and her crew list contains only her.
- **Events to re-evaluate on** ([stripped GameEvents.cs](https://github.com/ProgrammerFailure/KSP-Documentation-Effort/blob/HEAD/StrippedCSFiles/GameEvents.cs)): `onVesselChange`, `onVesselCrewWasModified`, `onCrewTransferred`, `onCrewOnEva`, `onCrewBoardVessel`, `onCrewKilled`.
- **kOS**: `SHIP:CREW()` returns a list of `CrewMember` structures with `NAME`, `GENDER`, `EXPERIENCE`, `TRAIT`, `TOURIST` and `PART` ([crewmember.rst](https://github.com/KSP-KOS/KOS/blob/develop/doc/source/structures/vessels/crewmember.rst), [vessel.rst](https://github.com/KSP-KOS/KOS/blob/develop/doc/source/structures/vessels/vessel.rst)). kOS scripts can therefore check for Claudia without going through C#.

## Open questions for a decision ticket

1. Can Claudia die permanently? Options: honour stock KIA, force her to respawn even when `MissingCrewsRespawn` is off, or make her immune.
2. Should firing her be blocked (event revert vs Harmony), or allowed with the AI persona continuing while she is "off-roster"?
3. Should she cost funds or a crew slot in Career, and what happens if the Astronaut Complex is already full when the mod is installed mid-career?
4. Name clash policy: what to do if the save already has a "Claudia ..." Kerbal (rename it, adopt it, or give up)?
5. Does the AI persona depend on her being aboard? For example, can Claudia "fly" uncrewed probes from mission control, or only vessels she is on?
6. What starting stats should she have: trait (Pilot?), courage/stupidity, veteran/badass, and starting XP level in Career?
