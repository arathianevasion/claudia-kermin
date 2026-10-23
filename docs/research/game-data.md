# Game data available for Craft Checks and Debriefs

Research for issue #4. Researched 2026-10-23 against KSP 1.12.x APIs.

## Answer

- **The mod (C# plugin) can read everything a Craft Check or Debrief needs, in both the editor and flight.** The game's own delta-v simulator (`VesselDeltaV`) runs on an editor `ShipConstruct` as well as on a flying `Vessel`. It gives per-stage delta-v, TWR, thrust, Isp, burn time and masses for vacuum, sea level and the "current" situation.
- **Per-body figures work by setting a global.** "Current" figures are computed for a body and altitude held in `DeltaVGlobals.DeltaVAppValues` (`body`, `altitude`). To get "TWR on the Mun" you set `body`, call `SetCalcsDirty`, and wait for `IsReady`. The game does this asynchronously, over several frames, so you can't call it synchronously. Its vacuum and ASL figures don't depend on the body, but its TWR does, through gravity.
- **Science:** per part, `ModuleScienceExperiment` gives the experiment ID. `ResearchAndDevelopment` gives the experiment's situation and biome masks, and each subject's science earned so far (`science`) versus the cap (`scienceCap`). `IScienceDataContainer.GetData()` gives the results the craft holds.
- **Parachutes:** `ModuleParachute` has deploy state and a live safe/risky/unsafe rating. **Heat shields:** `ModuleAblator` plus the "Ablator" resource, with `Part.maxTemp` and `skinMaxTemp`.
- **Crew:** in the editor, `CrewAssignmentDialog.Instance.GetManifest()`. In flight, `Vessel.GetVesselCrew()`.
- **Orbit and landing:** `Vessel.orbit` and `Vessel.situation`, `landedAt`, `GameEvents`. **Resources:** `Part.Resources` and `Vessel.GetConnectedResourceTotals`.
- **Editor vs flight:** a Craft Check can run in the VAB/SPH before launch. kRPC and MechJeb both read delta-v from the editor ship, and Kerbalism and RP-1 read the editor crew manifest. Some things only exist in flight: the live orbit, live situation, parachute safety rating, science already stored on board, and temperatures. A pre-launch Craft Check therefore works from design data plus game state such as R&D science. A Craft Check run in flight (for example before each Maneuver Sequence) can use live data too.
- **kOS can read only in flight.** Its scripts run only in the flight scene; its editor code only configures the processor UI. In flight it exposes the same stock delta-v (`SHIP:DELTAV`, `SHIP:STAGEDELTAV(n)`, `STAGE:DELTAV`, but not TWR), plus orbit, status, crew, resources, and experiment results with their science value. It can't read the science cap or what has already been earned, it can't set the delta-v body, and generic part fields are limited to what the right-click menu shows.
- **Suggested split:** the mod reads everything for Craft Checks and Debriefs. kOS reports only what its script measured (for example burn residuals, achieved orbit and abort reasons) back to the mod.

## Source caveats

- **The official KSP API docs (kerbalspaceprogram.com/ksp/api, labelled "1.12.4") are gone.** Pages such as `class_vessel_delta_v.html` and `class_delta_v_calc.html` returned HTTP 404 on 2026-10-23, and the Wayback Machine has no snapshots of them. A web search still shows their summaries, for example "This class stores ModuleEngines Data related to a Vessel or ShipConstruct which is used in DeltaV calculations". The class and member signatures below come from **ProgrammerFailure/KSP-Documentation-Effort**, a stripped decompilation of the KSP assembly (`StrippedCSFiles/`, commit `37fe2fc`, last pushed 2024-11, which implies 1.12.x). Its exact KSP version is **not stated**, and stripped files carry no version constants.
- **The KSP wiki changelogs for 1.7 and 1.12 couldn't be read.** The wiki served a bot-protection page. I couldn't confirm from release notes that 1.12 left these APIs unchanged, though nothing in the mod sources below suggests it changed them.
- **Behaviour claims (asynchronous recalculation, readiness) come from comments in kRPC and kOS source,** not from Squad docs.
- **The kRPC `Editor` service I cite is on kRPC's upstream main branch** (commit `aee8c05`) and may not be in a released kRPC build yet. I cite it as evidence of what the stock API allows, not as a dependency.

Abbreviations for the sources:
- **[S]** = `https://github.com/ProgrammerFailure/KSP-Documentation-Effort/blob/37fe2fc2634223ccc24bb387d342311cba4de978/StrippedCSFiles/<Class>.cs`
- **[kOS]** = `https://github.com/KSP-KOS/KOS/blob/8f281a459b6ea0ba917c91bbbc117e68bb948199/src/kOS/...`
- **[kRPC]** = `https://github.com/krpc/krpc/blob/aee8c057fc233998ce6283ccafac01c29b4f48e8/service/SpaceCenter/src/...`

## Detail

### 1. Stock delta-v API (per stage, per body), TWR

**Classes**

- `VesselDeltaV` (a MonoBehaviour) [S: VesselDeltaV.cs]:
  - Construction: `static Create(Vessel)` and `static Create(ShipConstruct)`.
  - Source objects: `Vessel`, `Ship`.
  - Figures: `OperatingStageInfo : List<DeltaVStageInfo>`, `GetStage(int stage)` (which can return null), `TotalDeltaVVac`, `TotalDeltaVASL`, `TotalDeltaVActual`, `TotalBurnTime`, `GetSituationTotalDeltaV(DeltaVSituationOptions)`.
  - State and control: `IsReady`, `SimulationRunning`, `SetCalcsDirty(bool resetPartCaches, bool syncListInstances)`, `lowestStageWithDeltaV`.
- How to get one:
  - In flight: `Vessel.VesselDeltaV` [S: Vessel.cs].
  - In the editor: `EditorLogic.fetch.ship.vesselDeltaV`, as kRPC does in `Services/EditorDeltaV.cs` [kRPC].
- `DeltaVStageInfo` [S: DeltaVStageInfo.cs]:
  - Stage identity and parts: `stage`, `separationIndex`, `payloadStage`, `parts`, `enginesActiveInStage`, `enginesInStage`.
  - Masses: `stageMass`, `dryMass`, `fuelMass`, `startMass`, `endMass`, `decoupledMass`.
  - Isp: `ispVac`, `ispASL`, `ispActual`.
  - TWR: **`TWRVac`, `TWRASL`, `TWRActual`**.
  - Thrust: `thrustVac`, `thrustASL`, `thrustActual`, and the vectored variants.
  - Delta-v: `deltaVinVac`, `deltaVatASL`, `deltaVActual`.
  - Burn time: `stageBurnTime`.
  - Per-situation getters: `GetSituationTWR`, `GetSituationDeltaV`, `GetSituationISP`, `GetSituationThrust`.
  - It has constructors for both `ShipConstruct` and `Vessel`.
- `DeltaVSituationOptions` is `{ SeaLevel, Altitude, Vaccum }` (the spelling is the game's) [S: DeltaVSituationOptions.cs].

**Choosing the body**

- `DeltaVGlobals.DeltaVAppValues` holds `body : CelestialBody`, `situation`, `altitude`, `atmPressure` and `atmDensity` [S: DeltaVAppValues.cs, DeltaVGlobals.cs].
- kRPC's `Editor.DeltaVBody` and `DeltaVAltitude` write `AppValues.body` and `altitude` and then call `SetCalcsDirty(true)`. Its docs say that the body "sets the gravity the thrust-to-weight ratios are against". With the altitude, it also sets "the atmospheric pressure and density the specific impulses and thrusts are against". They also say this is "global game state rather than editor state … it persists across scene changes". [kRPC: Services/Editor.cs, lines 57-107]
- `situation` only selects which column the stock app displays [kRPC: Services/Editor.cs, lines 109-127].
- **Consequence:** to get TWR on several bodies (Kerbin launch, Mun landing, Kerbin return), the mod must set the body, wait, read, and repeat. It should then restore the player's setting.
- Alternatively, the mod can compute TWR itself: `thrustVac / (startMass × g_body)`, with `g_body = body.gravParameter / body.Radius²`. This uses per-stage thrust and mass that don't depend on the body. That is cheaper and doesn't touch global state. (This is my inference, not tested.)

**Readiness and accuracy**

- Recalculation is asynchronous. kRPC treats figures as current only when `IsReady && !SimulationRunning`, and only after a fixed-update tick has passed since `SetCalcsDirty` [kRPC: Services/EditorDeltaV.cs].
- In flight, the game delays a recalculation by `DELTAV_VESSEL_EVENT_DELAY_SECS` (1 s) after vessel events and spends "a rendered frame per stage". It never raises `IsReady` for a vessel with no engines [kRPC: Services/FlightDeltaV.cs, comments at lines 25-75].
- `GameEvents.onDeltaVCalcsCompleted` (an `EventVoid`) fires when a run completes [S: GameEvents.cs, line 735].
- Players or mods can turn the stock simulation off: `DeltaVGlobals.DoStockSimulations`, `DisableStockSimluations()` [S: DeltaVGlobals.cs]. DMagic's Basic DeltaV, for example, disables all stock calculations (https://forum.kerbalspaceprogram.com/topic/161276-18x-dmagics-basic-mods-basic-orbit-90-basic-deltav-60-11-2-2019).
- kOS's docs warn that the stock numbers have "less-than-accurate traps". They take a few ticks to settle after staging, may mishandle asparagus staging, and are reliable only for the active vessel (https://ksp-kos.github.io/KOS/structures/vessels/deltav.html).

**Alternatives**

- MechJeb runs its own `FuelFlowSimulation` and supports the editor: it uses `EditorLogic.fetch.ship` and a player-chosen `EditorBody` when `HighLogic.LoadedSceneIsEditor` (https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/MechJebModuleStageStats.cs, lines 19 and 117-133).
- Using MechJeb would add a dependency. The stock API is probably good enough for a sanity check, but this is a decision point.

### 2. Science experiments and results

- **Experiment parts:** `ModuleScienceExperiment` (which implements `IScienceDataContainer`) has `experimentID`, `rerunnable`, `resettable` and `useStaging` [S: ModuleScienceExperiment.cs].
  - In flight it also exposes `Deployed`/`Inoperable`, as kOS reads them [kOS: Suffixed/PartModuleField/StockScienceExperimentFields.cs].
- **Experiment definitions:** `ResearchAndDevelopment.GetExperiment(id)` returns a `ScienceExperiment`. That has `experimentTitle`, `baseValue`, `scienceCap`, `situationMask`, `biomeMask`, `requireAtmosphere`, `IsAvailableWhile(ExperimentSituations, CelestialBody)` and `BiomeIsRelevantWhile(...)` [S: ScienceExperiment.cs, ResearchAndDevelopment.cs].
  - A Craft Check can use these to answer "can these 3 experiments run when landed on the Mun?" against the Mission Goal.
- **Science already earned:**
  - `ResearchAndDevelopment.GetExperimentSubject(exp, situation, body, biome, displaybiome)` and `GetSubjectByID(id)` return a `ScienceSubject` with `science` (earned so far), `scienceCap`, `scientificValue` and `subjectValue` [S: ScienceSubject.cs].
  - `GetScienceValue(dataAmount, subject, xmitScalar)` and `GetNextScienceValue(...)` give the expected return [S: ResearchAndDevelopment.cs].
- **Results held on board (flight):** `IScienceDataContainer.GetData()` returns `ScienceData[]`. Each `ScienceData` has `title`, `subjectID`, `dataAmount`, `baseTransmitValue`, `transmitBonus`, `labValue` and `scienceValueRatio`. `ModuleScienceContainer.GetScienceCount()` gives the count [S: ScienceData.cs, ModuleScienceContainer.cs].
- **Events for a Debrief:** `GameEvents.OnExperimentDeployed(ScienceData)` and `GameEvents.OnScienceRecieved(float, ScienceSubject, ProtoVessel, bool)`. The second one fires on transmission or recovery [S: GameEvents.cs, lines 932-936].
- **Uncertainty:** I expect `ResearchAndDevelopment.Instance` to be absent in Sandbox games, where science doesn't exist. I haven't verified this, so treat R&D lookups as career/science-mode only until it's tested.

### 3. Parachutes and heat shields

- **Parachutes:** `ModuleParachute` [S: ModuleParachute.cs] has:
  - `deploymentState` (`STOWED`, `ACTIVE`, `SEMIDEPLOYED`, `DEPLOYED` or `CUT`).
  - `deploymentSafeState` (`SAFE`, `RISKY`, `UNSAFE` or `NONE`), plus a `deploySafe` string. This is the stock in-flight safety indicator.
  - `deployAltitude`, `minAirPressureToOpen`, `chuteMaxTemp`, `fullyDeployedDrag`, `areaDeployed` and `autoCutSpeed`.
  - In the editor, a Craft Check can count chutes and read their settings. Descent speed under canopy isn't given by the API; the mod would have to estimate it from drag area and mass. kOS itself enumerates `ModuleParachute` for its `CHUTES` and `CHUTESSAFE` handling [kOS: Utilities/VesselUtils.cs, lines 358-390].
- **Heat shields:** `ModuleAblator` has `ablativeResource` (stock value "Ablator"), `ablationTempThresh`, `useNode` and `nodeName` (the shielded node), and live `loss` and `flux` [S: ModuleAblator.cs].
  - The amount of ablator is in `Part.Resources`.
  - Part limits are `Part.maxTemp` and `Part.skinMaxTemp`. Live values are `Part.temperature` and `Part.skinTemperature` [S: Part.cs, lines 697-832].
  - Whether the shield faces the right way can only be inferred from geometry and nodes. I found no API that answers it.
- **Debrief events:** `GameEvents.onOverheat`, `onPartDie`, `onPartExplode`, `onCrash`, `onCrashSplashdown` and `onCollision` [S: GameEvents.cs].

### 4. Crew seating

- **Per part:** `Part.CrewCapacity` and `Part.protoModuleCrew`. **Command parts:** `ModuleCommand.minimumCrew` [S: Part.cs, ModuleCommand.cs].
- **Editor:**
  - `ShipConstruct` has an obsolete crew method whose `[Obsolete]` text reads: "In the editor you have to check the manifest for isControlSource parts" [S: ShipConstruct.cs, line 157].
  - The manifest is `KSP.UI.CrewAssignmentDialog.Instance.GetManifest()`, which returns a `VesselCrewManifest`. That has `PartManifests`, `GetAllCrew(bool)` and `CrewCount`, and each `PartCrewManifest.GetPartCrew()` returns `ProtoCrewMember[]` [S: VesselCrewManifest.cs, PartCrewManifest.cs].
  - Mods using it in the editor include Kerbalism's planner (https://github.com/ShotgunNinja/Kerbalism/blob/df950e24872712f1ecc3be3545c4b74a913913b4/src/UI/Planner.cs) and RP-1 (https://github.com/KSP-RO/RP-1/blob/934682525ccfb16a9505a956e7186fcbaab822b0/Source/RP0/UI/KCT/GUI_Simulation.cs).
  - From a saved craft file: `VesselCrewManifest.FromConfigNode(craftNode)` [S].
- **Flight:** `Vessel.GetVesselCrew()`, `GetCrewCount()` and `GetCrewCapacity()` [S: Vessel.cs].
- **Each `ProtoCrewMember`** has `name`, `trait`, `experienceLevel`, `type`, `rosterStatus`, `seat`, `flightLog` and `careerLog` [S: ProtoCrewMember.cs].
  - This is how to check whether Claudia herself is aboard, or whether a scientist is aboard to reset experiments.
  - `FlightLog.EntryType` has `Land`, `Flyby`, `Orbit`, `Suborbit`, `Escape`, `Launch`, `PlantFlag`, `Recover` and `Die`. Those entries are a ready-made per-Kerbal achievement record for a Debrief [S: FlightLog.cs].

### 5. Orbit and landing state

- **`Vessel`** [S: Vessel.cs]:
  - `situation` (`Situations`: `LANDED`, `SPLASHED`, `PRELAUNCH`, `FLYING`, `SUB_ORBITAL`, `ORBITING`, `ESCAPING`, `DOCKED`).
  - `Landed`, `Splashed`, `LandedOrSplashed`, `landedAt`, `displaylandedAt`.
  - `altitude`, `heightFromTerrain`, `srfSpeed`.
  - `mainBody`, `missionTime`, `launchTime`, `currentStage`, `totalMass`.
- **`Orbit`** [S: Orbit.cs]: `referenceBody`, `ApA`, `PeA`, `inclination`, `eccentricity`, `semiMajorAxis`, `LAN`, `argumentOfPeriapsis`, `period`, `timeToAp`, `timeToPe`, `nextPatch`, `patchEndTransition`, `UTsoi`, and `GetRelativeInclination`.
- **Events for a Debrief:** `GameEvents.onVesselSituationChange(Vessel, from, to)`, `onVesselSOIChanged`, `onStageSeparation`, `onStageActivate`, `onPartJointBreak`, `onCrewKilled` and `onVesselRecovered` [S: GameEvents.cs].
- **Biome:** I didn't look up the stock call for the current biome (the `ScienceUtil`/`CBAttributeMapSO` family). It still needs checking.

### 6. Resource levels

- **Per part:** `Part.Resources` (a `PartResourceList` of `amount`/`maxAmount`). In the editor, there is also `Part.SimulationResources` [S: Part.cs, lines 1513-1522].
- **Vessel-wide:** `Vessel.GetConnectedResourceTotals(int resourceId, out amount, out maxAmount, bool pulling)`, which takes crossfeed into account, and `Vessel.resourcePartSet` [S: Vessel.cs].
- **Per stage:** the delta-v simulator reports `fuelMass` per stage [S: DeltaVStageInfo.cs].
- Life support, if Claudia needs it, is not a stock resource.

### 7. Editor vs flight availability (summary)

| Data | Editor (VAB/SPH) | Flight |
|---|---|---|
| Per-stage dV, TWR, Isp, burn time | Yes (`ship.vesselDeltaV`, body via `DeltaVAppValues`) | Yes (`Vessel.VesselDeltaV`) |
| Part list, modules, resources (capacity and loaded) | Yes (`ShipConstruct.parts`) | Yes |
| Experiments fitted and where they can run | Yes (module + `ResearchAndDevelopment`) | Yes |
| Science already earned per subject | Yes (R&D, career/science only) | Yes |
| Science data held on board | No (nothing collected yet) | Yes (`GetData()`) |
| Parachute and heat shield presence and settings | Yes | Yes |
| Parachute safe/risky, temperatures, ablation rate | No | Yes |
| Crew assignment | Yes (`CrewAssignmentDialog` manifest) | Yes (`GetVesselCrew()`) |
| Orbit, situation, landedAt | No | Yes |
| Event history for a Debrief | No | Yes (`GameEvents`, the mod must log them itself) |

### 8. What kOS can read

kOS scripts run only in flight. In `kOSProcessor.Update()`, the `GameScenes.EDITOR` branch only refreshes the boot-file UI and disk settings [kOS: Module/kOSProcessor.cs, lines 803-815]. In flight, kOS can read:

- **Delta-v:** `SHIP:DELTAV`, `SHIP:STAGEDELTAV(n)` and `STAGE:DELTAV`, each with `CURRENT`, `ASL`, `VACUUM`, `DURATION` and `FORCECALC()`. These are thin wrappers over `VesselDeltaV`/`DeltaVStageInfo` [kOS: Suffixed/DeltaVCalc.cs; Suffixed/VesselTarget.cs, lines 299-300 and 406-410; Suffixed/StageValues.cs, line 50].
  - There is **no TWR suffix** and no way to pick the body. Scripts compute TWR from `SHIP:AVAILABLETHRUST`/`MAXTHRUST` and mass.
- **Stage and resources:** `STAGE:NUMBER`, `READY`, `RESOURCES`/`RESOURCESLEX`, `NEXTDECOUPLER` (https://ksp-kos.github.io/KOS/structures/vessels/stage.html). There is also `SHIP:RESOURCES` [kOS: VesselTarget.cs, line 294].
- **Status, orbit and position:** `SHIP:STATUS` (`Vessel.situation` as a string) [kOS: VesselTarget.cs, line 297]. Orbit and position come through `SHIP:OBT`, `ALTITUDE`, `GEOPOSITION` and `BODY` [kOS: Suffixed/Orbitable.cs].
- **Crew:** `SHIP:CREW()` and `CREWCAPACITY`. Each crew member gives `NAME`, `TRAIT`, `EXPERIENCE`, `PART` and `STATUS` [kOS: Suffixed/CrewMember.cs].
- **Science:** `DEPLOY`, `RESET`, `TRANSMIT`, `DUMP`, `HASDATA`, `DEPLOYED`, `INOPERABLE` and `RERUNNABLE`. `DATA` gives `DATAAMOUNT`, `SCIENCEVALUE`, `TRANSMITVALUE` and `TITLE` [kOS: Suffixed/PartModuleField/ScienceExperimentFields.cs; Suffixed/ScienceDataValue.cs].
  - There is no access to `scienceCap`, to science already earned per subject, or to situation and biome availability.
- **Any part module:** `GETMODULE(...):GETFIELD/DOEVENT/DOACTION`. Only fields visible in the right-click menu are exposed, by their GUI names (https://ksp-kos.github.io/KOS/structures/vessels/partmodule.html). That reaches parachute state, for example, but not `ModuleAblator` internals or `ModuleParachute.deploymentSafeState` unless they are shown in the menu.

**Implication:** kOS can fly with what it reads, but a Debrief needs mod-side data. That means R&D science, the event history, editor data, per-body TWR and the parachute safety rating. kOS's job in a Debrief should be to report its own execution results (achieved orbit, remaining dV, abort reasons) back to the mod.

## Open questions for later decisions

1. Should a Craft Check use the stock delta-v simulator (which is global and asynchronous, and may be disabled by the player or a mod), compute per-body TWR itself from per-stage thrust and mass, or depend on MechJeb or KER? It also has to handle the player having stock delta-v disabled.
2. Is changing `DeltaVAppValues.body` acceptable, given that it also changes the player's own delta-v readout? If so, it must be saved and restored.
3. Does the mod support Sandbox mode, where R&D science isn't available? If so, the Craft Check needs a science-less path.
4. When does a Craft Check run? Pre-launch only (from editor data), or again in flight before each Maneuver Sequence (from live data)?
5. Debrief needs a mod-side event log (`GameEvents` subscriptions) for the length of each Maneuver Sequence. What gets logged, and how is it persisted across scene changes and saves?
6. How does kOS report back to the mod (for example a log file, a shared file on the archive volume, or a kOS addon)? This is probably a separate integration ticket.
7. Things not yet confirmed: the stock biome lookup call; whether `ResearchAndDevelopment.Instance` is null in Sandbox; and whether anything in the 1.12 release notes changed these APIs (the wiki was unreachable).
