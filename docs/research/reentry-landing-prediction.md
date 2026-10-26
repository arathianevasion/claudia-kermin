# Reentry targeting: predicting where a craft lands in an atmosphere

Research for [#247](https://github.com/arathianevasion/claudia-kermin/issues/247) (map #243). The question: how do existing mods and kOS libraries predict where a craft re-entering an atmosphere will land? Could a Building Block (kerboscript) or a Planning Tool (C#) do it well enough to target a splashdown within 50 km of KSC? And what does that cost?

## Answer in short

- **A vacuum (Kepler) prediction is useless for this.** From low Kerbin orbit with periapsis at 30 km, a Mk1 capsule lands about **630 km short** of the vacuum periapsis point. The gap changes with periapsis and with the craft. A drag integration is the only way to get within 50 km.
- **The integration itself is cheap and easy.** RK4 at a 2 to 5 s step through the atmosphere is within about 10 m of a 0.1 s reference. That takes about 100 to 200 steps per prediction.
- **The craft's drag is what decides the accuracy.** Each 10% error in the hypersonic drag area per unit mass (ballistic coefficient) moves the landing point about **12 km**. A single drag value per craft, taken at its hypersonic Mach and its retrograde attitude, lands within about 2 km of KSP's full Mach-dependent drag-cube model. A guessed value is the problem. kOS exposes no drag, drag cubes or air density, so kerboscript cannot get this number itself. The KSP code that owns it (`DragCubeList`) is C#-only.
- **Execution error.** Each 1 km of periapsis error moves the landing point about **11 km** (at a 30 km periapsis). That is about 1.2 km of periapsis per 1 m/s of burn error from an 80 km orbit.
- **Recommendation: both, sharing one C# predictor.** The mod gets one C# atmospheric-impact predictor, modelled on the Trajectories and MechJeb approach, that reads KSP's own drag cubes. It is exposed two ways:
  1. As a read-only **Planning Tool**: where will this craft land if it burns now or at this time? Which burn puts it at a given latitude and longitude?
  2. As an `ADDONS:CLAUDIA` suffix (a mod-side helper) that a new **deorbit Building Block** calls. The block plans the burn, flies it with `exec_node`, re-predicts, and makes a small correction burn before 70 km.

  Expected accuracy for a capsule splashdown from LKO is **about 5 to 15 km typical, about 25 km worst case**, well inside 50 km. A kerboscript-only version is feasible as a fallback (tens of seconds at the default IPU), but only with a calibrated ballistic coefficient. Uncalibrated, it can miss by more than 50 km.

## What exists in this repo

[`kerboscript/library/reentry.ks`](../../kerboscript/library/reentry.ks) flies the descent but does not plan or burn anything:

- it waits on rails for the atmosphere (`claudia_fall_eta`);
- it holds surface retrograde so the heat shield leads;
- it drops the shield below 400 m/s or 3000 m AGL;
- it opens drogues, then mains, by speed, pressure and AGL;
- it reports the end point (`geo`, `splashed`).

Its header says so: "It does not plan or burn anything." [`return.ks`](../../kerboscript/library/return.ks) only aims at a Kerbin periapsis altitude ("the craft comes down anywhere on Kerbin"). [`land.ks`](../../kerboscript/library/land.ks) targets a site, but only on airless bodies ("No Trajectories: an airless body's fall is plain orbit maths"). Nothing in the repo predicts an atmospheric impact point today. That is why the Mun flight dumped orbit elements and Kerbin's rotation into a Script Result to work it out by hand. Existing addon suffixes that a predictor could sit beside: `HOTTESTPART`, `BIOME`, `SLOPE`, `FLATSPOT` and `HIGHESTTERRAIN` (`src/ClaudiaKermin.KOS/ClaudiaAddon*.cs`).

## How the existing tools do it

### Trajectories (neuoy/KSPTrajectories, C#)

Source read at commit `1304f469ae`.

- **Vacuum, then integrate.** Trajectories follows the Kepler orbit (`patch.SpaceOrbit`) to the atmosphere's edge. From there it integrates gravity plus aero force with **fixed-step RK4**, "assuming a constant angle of attack" from the descent profile ([Trajectory.cs L758-L805](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Predictor/Trajectory.cs#L758-L805), RK4 at [L561-L580](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Predictor/Trajectory.cs#L561-L580)). The impact point is rotated into the body frame (`CalculateRotatedPosition`).
- **Step and limits.** The default `IntegrationStepSize` is **2.0 s** ([Settings.cs L71-L72](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Settings.cs#L71)), set by a slider (`INTEGRATOR_MIN` 0.1 s, `INTEGRATOR_MAX` 5 s). A prediction stops after one hour of flight (`maxIterations = 3600 / dt`). It runs incrementally, at most **2 ms per frame** (`MAX_INCREMENT_TIME`, [L107](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Predictor/Trajectory.cs#L107)).
- **Euler correction.** It adds a correction for KSP's own (Euler-like) physics step: `state.position += 0.5 * TimeWarp.fixedDeltaTime * currentAccel * dt` ([L909-L920](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Predictor/Trajectory.cs#L909-L920)).
- **Drag model.** `StockAeroUtil.SimAeroForce` walks every part of the vessel and calls KSP's own `DragCubeList.AddSurfaceDragDirection(dir, mach)`. It multiplies by `DragCubeMultiplier`, `DragMultiplier` and the pseudo-Reynolds curve, and adds body lift and lifting surfaces ([StockAeroUtil.cs L131-L240](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Predictor/StockAeroUtil.cs#L131)). Air density uses the **average equatorial day/night temperature**, not the true local one ("This is an approximation…", [L74-L100](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/Predictor/StockAeroUtil.cs#L74)).
- **Force cache.** The forces are cached in a **32 (speed, up to 10 km/s) × 33 (AoA, ±180°) × 32 (altitude)** table, filled lazily and trilinearly interpolated ([AeroDynamicModel.cs L80-L95](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/AeroDynamicModels/AeroDynamicModel.cs#L80), [AeroForceCache.cs](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/src/Plugin/AeroDynamicModels/AeroForceCache.cs)).
- **Limits** (from the [README](https://github.com/neuoy/KSPTrajectories/blob/1304f469ae/README.md) FAQ). It predicts only the vessel as it is now: "It's not possible to predict a trajectory for a future stage." "Parachutes are not simulated." The prediction is right only if you "keep that orientation all the time". The README pitches it as able to "help reach a precise point on the ground (for example to land exactly at the Kerbal Space Center)", with a target and navball correction for lifting craft.

### kOS `ADDONS:TR` (kOS binding to Trajectories)

kOS source read at commit `8f281a459b`, [`doc/source/addons/Trajectories.rst`](https://github.com/KSP-KOS/KOS/blob/8f281a459b/doc/source/addons/Trajectories.rst).

- **Suffixes.** `AVAILABLE`, `HASIMPACT`, `IMPACTPOS` (GeoCoordinates), `TIMETILLIMPACT` (TR 2.2+), `SETTARGET`, `PLANNEDVEC` and `CORRECTEDVEC`, plus `PROGRADE`/`RETROGRADE` and the descent-profile lists (`DESCENTANGLES`, `DESCENTGRADES`, `DESCENTMODES`, TR 2.4+).
- **Caveats in the docs.** It works through reflection, so a Trajectories update can break it. It predicts **only the active vessel**. It "does its calculation based on the vessel's current orientation. Any tiny change in orientation will change the prediction. Accuracy is not guaranteed." `IMPACTPOS` throws unless `HASIMPACT` is true.
- **Not installed here.** Trajectories is not in `~/KSP_spike/GameData` (only `000_Harmony`, `ClaudiaKermin`, `kOS` and `Squad`). Using it would add a third-party dependency for players, and it can't answer "what if I burn at T". It only predicts the orbit and attitude the craft has now.

### MechJeb2 (`ReentrySimulation`, landing autopilot)

Source read at commit `2d8fd1fa36`.

- **Integrator.** An **adaptive Bogacki-Shampine 3(4)** integrator (`BS34Step`, `TOL = 0.01`). The step shrinks for parachute openings and near the ground, and is capped at 10 s. The first 1 km of flight always runs at 0.02 s and the next km at ≤0.5 s. A run stops at 50,000 steps or `maxOrbits` ([ReentrySimulation.cs L220-L260, L365-L470](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa36/MechJeb2/ReentrySimulation.cs#L365)).
- **Simulated vessel.** It runs on a **ThreadPool** thread against a `SimulatedVessel`: a copy of each part's `DragCubeList` (`SimulatedPart`, [FlyingSim/](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa36/MechJeb2/FlyingSim/SimulatedPart.cs)). That copy includes simulated parachutes (`SimulatedParachute`), so unlike Trajectories it models chute deployment.
- **Error simulations.** It can run extra "error simulations" with a perturbed parachute multiplier for statistics (`runErrorSimulations`, [MechJebModuleLandingPredictions.cs L111-L160](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa36/MechJeb2/MechJebModuleLandingPredictions.cs#L111)). The default `dt` is 0.2 s.
- **Targeting is closed loop.** `DeorbitBurn` aims a first burn using only freefall time plus body rotation ([DeorbitBurn.cs](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa36/MechJeb2/LandingAutopilot/DeorbitBurn.cs)). It then hands over to `CourseCorrection`, which burns against the **simulated** landing site until the error is under **150 m**. It stops correcting once drag reaches 0.1 m/s² ("Turning while under drag is a bad idea", [CourseCorrection.cs](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa36/MechJeb2/LandingAutopilot/CourseCorrection.cs)). The precision comes from correcting with the predictor in the loop before the atmosphere, not from a perfect first burn. Its final metres come from a powered descent, which a capsule doesn't have.

### kOS community libraries

- **[KSLib](https://github.com/KSP-KOS/KSLib).** Its `library/` has no reentry or atmospheric-impact predictor: navigation, PID, lazcalc, realchute helpers, and so on.
- **Published reentry scripts.** The ones that come up all lean on Trajectories rather than integrating drag themselves:
  - [CalebJ2/kOS-landing-script](https://github.com/CalebJ2/kOS-landing-script), the example in the kOS docs, and its fork [aabbdev/kOS-landing-script](https://github.com/aabbdev/kOS-landing-script);
  - [giuliodondi/kOS-ShuttleEntrySim](https://github.com/giuliodondi/kOS-ShuttleEntrySim), which needs RSS/RO and Trajectories and is calibrated for one Shuttle.
- **Forum opinion.** On the KSP forum's [Falcon-guidance thread](https://forum.kerbalspaceprogram.com/topic/169224-ksp-building-a-falcon-guidance-system), the view is that simulating drag before descent in pure kOS is impractical without an external tool.

**There is no established kerboscript drag integrator to reuse.**

### KSP's own aerodynamics (what any predictor must match)

From `~/KSP_spike/Physics.cfg` and a decompile of `DragCubeList` and `PhysicsGlobals` (Assembly-CSharp.dll).

- **Per-face drag.** `AddSurfaceDragDirection` loops over a part's six cube faces. For each face:
  - `dot = direction · faceNormal`;
  - multiplier = `DragCurveValue(dot, mach)`, which lerps tail → surface → tip and multiplies by `DRAG_MULTIPLIER(mach)`;
  - `areaDrag += areaOccluded × multiplier × DRAG_CD(cubeCd)^DRAG_CD_POWER(mach)`.
- **Drag force.** drag [kN] = q [kPa] × areaDrag × `dragCubeMultiplier` (0.1) × `dragMultiplier` (8) × `DRAG_PSEUDOREYNOLDS(ρv)`.
- **The curves move a lot with Mach:**
  - `DRAG_TIP` goes from 1 subsonic to 4 at Mach ≥ 5;
  - `DRAG_MULTIPLIER` goes 0.5 → 1.3 (Mach 1.1) → 0.6 (Mach 5) → 0.85 (Mach 10);
  - `DRAG_CD_POWER` goes from 1 to 3.

  For the Mk1 pod v2 with HeatShield1 and a Mk16 chute, shield first, these give an effective Cd·A of about **0.5 m² subsonic, 1.9 m² at Mach 1 to 5 and 2.3 to 2.9 m² at Mach 8 to 20**. That is a factor of about 5 across the entry (cube values from `PartDatabase.cfg`: HeatShield1 Y− face 1.27 m², Cd 0.951; mk1pod_v2 sides 1.06 m², Cd 0.72).
- **Atmosphere model.** kOS's `BODY:ATM:ALTITUDETEMPERATURE` returns `celestialBody.GetTemperature(alt)`, the base curve only. The kOS docs call it "only approximate because the temperature will vary depending on the sun position" ([BodyAtmosphere.cs L23-L26](https://github.com/KSP-KOS/KOS/blob/8f281a459b/src/kOS/Suffixed/BodyAtmosphere.cs#L23)). KSP's real density uses the sun- and latitude-adjusted temperature. Trajectories uses an average of it.
- **No drag readouts in kOS.** kOS exposes `ALTITUDEPRESSURE`, `MOLARMASS` and `ADIABATICINDEX` (so a script can work out ρ = pM/RT and the speed of sound) and `SHIP:Q`. It exposes **no drag force, drag cube or Cd**. `SHIP:TERMVELOCITY` throws ("Removed to account for … KSP 1.0").

## Sensitivity study (own model)

[`reentry-landing-prediction-sim.py`](reentry-landing-prediction-sim.py) is a planar, equatorial model of the setup above:

- KSP's drag-cube maths and curves as listed;
- a rotating atmosphere;
- a tabulated Kerbin atmosphere that only approximates stock, so absolute downrange numbers are indicative, but the differences between models are what matter;
- a 1.24 t capsule, retrograde burn from an 80 km circular orbit, no lift.

Run it with `python3 -I docs/research/reentry-landing-prediction-sim.py`.

Reference: periapsis 30 km. Impact is **1097 km downrange of the burn point** (ground frame), 270 s to entry and 430 s from entry to sea level. Mach 1 comes at about 16 km. Only **about 6 km** of downrange is flown after Mach 1, so chutes and the shield drop barely matter to where it lands. Peak about 2.5 g in this atmosphere.

**Landing error for each model, against the full model (RK4, 0.1 s):**

| Model | Landing error |
|---|---|
| Vacuum (Kepler periapsis point) | +634 km |
| One Cd·A, the subsonic value | +214 km |
| Two Cd·A values (hypersonic, subsonic below Mach 1.5) | +7 km |
| One Cd·A, the hypersonic (Mach 8) value | **+2 km** |
| Full model, drag ×0.9 / ×1.1 | +13 / −12 km |
| Full model, drag ×0.8 / ×1.2 | +28 / −23 km |

**Periapsis sweep (impact downrange of the burn point):**

| Periapsis | Downrange | Gap to vacuum | Shift per +1 km periapsis | Shift per +10% drag |
|---|---|---|---|---|
| 10 km | 927 km | 806 km | +6.7 km | −9.1 km |
| 20 km | 1002 km | 731 km | +8.4 km | −10.3 km |
| 30 km | 1097 km | 634 km | +11.0 km | −11.9 km |
| 40 km | 1225 km | 504 km | +15.5 km | −14.2 km |
| 50 km | 1414 km | 313 km | +24.6 km | −17.9 km |

**Integrator cost for one prediction, burn to splash** (the vacuum coast uses 10 s RK4 steps for RK4, the same step for Euler):

| Integrator, step | Landing error | Force evaluations |
|---|---|---|
| RK4, 0.5 s | <0.01 km | 3624 |
| RK4, 2 s | <0.01 km | 984 |
| RK4, 5 s | 0.01 km | 456 |
| Semi-implicit Euler, 0.5 s | −12 km | 1386 |
| Semi-implicit Euler, 2 s | −49 km | 337 |

What this shows:

1. **A steeper entry is more forgiving.** At a 10 to 20 km periapsis, both periapsis and drag errors cost less. A shallow entry (≥ 40 km) amplifies both.
2. **RK4 at 2 to 5 s is enough. Euler is not.** If kerboscript does the integration, it must use RK4 (or at least a midpoint method) and can take big steps.
3. **The step is cheap. The craft's Cd·A per unit mass decides the accuracy.** One hypersonic number captures most of it. That number can only be read from drag cubes in C#. kerboscript would have to estimate it from flight data, after the deorbit burn has already committed the craft.

## The questions

### Approaches

| Approach | What it does | Typical miss from LKO |
|---|---|---|
| Vacuum ballistic | Kepler orbit, landing at periapsis or sea-level crossing | ~300 to 800 km (and no answer if the periapsis is above ground) |
| Vacuum plus a rule-of-thumb lead | "Burn over longitude X" for one craft, orbit and periapsis | ~10 to 30 km for the exact craft and orbit it was calibrated on; 50 to 200+ km once any of them changes |
| Ballistic coefficient plus density integration | RK4 through ρ(h) from kOS pressure and temperature, one Cd·A/m | 10 to 30 km if Cd·A/m is known to 10 to 20%; >50 km if guessed |
| Full drag-cube integration (Trajectories / MechJeb) | Per-part, per-Mach, per-attitude drag from KSP's own `DragCubeList` | a few km from the model; the real-world error is then execution and attitude |
| Full integration plus a correction burn before 70 km (MechJeb) | Re-predict after the burn, trim with a small burn | ~5 km, limited by density and temperature bias and the attitude actually held |

### Accuracy for a capsule splashdown from LKO

These come from the sensitivities above at a 20 to 30 km periapsis:

- **Burn execution.** About 1.2 km of periapsis per 1 m/s, so about 10 to 13 km of landing per m/s. A pre-entry correction (as in `return.ks` and MechJeb `CourseCorrection`) removes most of it. Burn timing costs about 2 km per second of error (the ground speed).
- **Density bias.** Base versus sun-adjusted temperature is of the order of a few to 10% in density. Each 10% costs about 10 to 12 km. A C# predictor can use KSP's own full-temperature functions, as Trajectories does, and shrink this.
- **Attitude.** The prediction assumes surface retrograde, shield first. `reentry.ks` holds exactly that, which is good. A pod that flips nose-first has a very different Cd·A (tip/tail swap) and misses by hundreds of km.
- **Heat shield.** It is the leading face (1.27 m², the largest cube face), so it sets the hypersonic Cd·A. Losing it, or flying without one, changes Cd·A and so the answer. Ablator burned on an LKO entry changes the mass by a few percent at most, a few km.
- **Shield drop and parachutes.** Shield drop (≤400 m/s) and parachutes come after Mach 1, which is ≤6 km of downrange in all. They can be ignored or modelled crudely as a vertical drop.
- **Other bodies.** For a Mun return (about 3.1 km/s at entry) the sensitivities grow, as the 50 km periapsis row hints. The same machinery applies, but the margins are tighter.

**Expected accuracy with a C# predictor plus a pre-entry correction:** about 5 to 15 km typical, about 25 km worst case. **With a kerboscript-only predictor and a calibrated Cd·A/m:** about 10 to 30 km. **Without calibration:** not reliably within 50 km.

### Compute cost per prediction

- **C#.** About 200 to 500 force evaluations (vacuum coast analytic via `Orbit`, RK4 at 2 to 5 s). With a Trajectories-style force table, that is well under a millisecond. Even with per-part `DragCubeList` calls on each evaluation it stays at a few ms for a capsule. A Planning Tool or addon suffix can run it synchronously. A burn-time search that runs it 10 to 20 times stays well under a frame budget. MechJeb's threading isn't needed at this size.
- **kerboscript.** kOS runs `CONFIG:IPU` instructions per physics tick: 200 by default, clamped to [50, 2000] ([kOSCustomParameters.cs L51-L53](https://github.com/KSP-KOS/KOS/blob/8f281a459b/src/kOS/Module/kOSCustomParameters.cs#L51), [config.rst](https://github.com/KSP-KOS/KOS/blob/8f281a459b/doc/source/structures/misc/config.rst)). The Claudia mod never changes IPU (`RunawayStop.cs`). Here is an estimate; it has not been measured in game.
  - A force evaluation (2 atmosphere suffix calls, ρ and Mach, a Cd·A lookup, vector maths) is of the order of 150 to 300 kRISC instructions, so an RK4 step is about 1,000 to 1,500.
  - The atmospheric leg is 430 s: about 90 steps at 5 s or about 215 at 2 s. The vacuum leg is free, from `POSITIONAT`/`VELOCITYAT` at the entry UT.
  - That is **about 100k to 300k instructions per prediction: about 10 to 30 s of game time at IPU 200, or 1 to 3 s at IPU 2000.**
  - A burn-time search (secant, about 4 to 8 predictions) takes about 1 to 4 minutes at the default IPU. That works while coasting in orbit before the burn, but it's slow and costs a Mission Script's tick budget.

### Kerboscript, C#, or both

kerboscript can integrate, but it cannot get the one input that decides the accuracy: Cd·A/m at the attitude it will fly. The workarounds are weak:

- an option the player or Claudia sets by hand, which is what the Mun flight effectively did;
- a per-craft calibration from a previous flight's `reentry` result;
- a fit from `SHIP:Q` and measured deceleration during the entry, which comes too late, because a capsule has no control authority by then.

The C# side can call `DragCubeList.AddSurfaceDragDirection` directly, as Trajectories and MechJeb do. Under ADR 0002 and the map's "Planning Tools only read; Building Blocks fly; Mission Scripts call blocks", that splits naturally:

- **Planning Tool (read-only):** `predict_reentry`, or similar. Input: the current orbit, or a hypothetical burn (UT plus Δv), and an attitude (retrograde by default). Output: the predicted splash lat/lng, UT, peak g and the miss distance to a target. A second form searches for the burn time and Δv that put the impact at a target lat/lng. Claudia uses it to plan and to answer questions without writing a Custom Script.
- **Mod-side helper suffix:** for example `ADDONS:CLAUDIA:IMPACT(burnUT, dv, attitude)`, beside `FLATSPOT` and the others, sharing the same C# core.
- **Building Block:** `deorbit(target)`. It finds the burn through the suffix, flies it with `exec_node`, re-predicts, and makes one small correction burn before 70 km if the miss is over a few km. It then hands over to `reentry`. `reentry` itself needs no change, apart from optionally reporting the miss distance from the target.

### Meeting "splashdown within 50 km of KSC"

1. **Use the C# predictor (Planning Tool plus helper suffix) with the craft's own drag cubes, assuming surface retrograde.** That is what `reentry.ks` flies.
2. **Aim a steep-ish entry (periapsis about 15 to 25 km for a capsule)** to roughly halve the sensitivity to periapsis and drag errors. The g-load for a Mk1 capsule stays modest. Check it against `reentry`'s `max_g`.
3. **Aim offshore, about 20 to 30 km east of KSC.** On a prograde orbit, an undershoot lands short (west, toward KSC and land) and an overshoot lands farther out to sea. An offshore aim point keeps the error spread over water.
4. **Re-predict after the burn and correct before 70 km,** as MechJeb does. This removes burn-execution error, which otherwise rivals the drag error.
5. **Close the loop with flight data.** Log predicted against actual `geo` in `reentry`'s result. Over a few flights, that measures the remaining bias (density and temperature) and can feed a correction factor.

This should land typically within about 5 to 15 km, and within 50 km with a wide margin. Pure kerboscript with a hand-supplied ballistic coefficient is a usable fallback (about 10 to 30 km when calibrated), but is not recommended as the primary design.

## Sources

- Trajectories, commit [`1304f469ae`](https://github.com/neuoy/KSPTrajectories/tree/1304f469ae): `src/Plugin/Predictor/Trajectory.cs`, `StockAeroUtil.cs`, `AeroDynamicModels/AeroDynamicModel.cs`, `AeroForceCache.cs`, `Settings.cs`, `README.md`.
- MechJeb2, commit [`2d8fd1fa36`](https://github.com/MuMech/MechJeb2/tree/2d8fd1fa36): `MechJeb2/ReentrySimulation.cs`, `MechJebModuleLandingPredictions.cs`, `FlyingSim/`, `LandingAutopilot/DeorbitBurn.cs`, `CourseCorrection.cs`.
- kOS, commit [`8f281a459b`](https://github.com/KSP-KOS/KOS/tree/8f281a459b): `doc/source/addons/Trajectories.rst`, `doc/source/structures/misc/config.rst`, `src/kOS/Module/kOSCustomParameters.cs`, `src/kOS/Suffixed/BodyAtmosphere.cs`, `doc/source/structures/vessels/vessel.rst` (`Q`, deprecated `TERMVELOCITY`).
- [KSLib `library/`](https://github.com/KSP-KOS/KSLib/tree/master/library): no reentry predictor.
- KSP 1.12: `~/KSP_spike/Physics.cfg` (drag curves and multipliers), `~/KSP_spike/PartDatabase.cfg` (drag cubes for mk1pod_v2 and HeatShield1), and decompiled `DragCubeList.AddSurfaceDragDirection` and `PhysicsGlobals.DragCurveValue` (Assembly-CSharp.dll, ilspycmd).
- Own model: [`reentry-landing-prediction-sim.py`](reentry-landing-prediction-sim.py). Its atmosphere is approximate, so treat absolute numbers as indicative.
