# What KSP's C# API offers for Planning Tools

Research for [#246](https://github.com/arathianevasion/claudia-kermin/issues/246), part of map #243. It asks, for each candidate **Planning Tool**, what KSP 1.12's C# API offers to compute it from the mod without kOS, under the rule that Planning Tools only read (ADR 0002), and roughly how big each one is to build.

## Sources

- **Decompiled game code.** `Assembly-CSharp.dll` from `~/KSP_spike/KSP.app/Contents/Resources/Data/Managed/`, decompiled with `ilspycmd -t <Type>`. The output is control-flow obfuscated (`while (true) switch (n)` noise); the method names and bodies quoted here are what remains after filtering that out. Citations give `Type.Method`, not line numbers, because the decompiler's line numbers are not stable. Types read: `Orbit`, `PatchedConics`, `PatchedConicSolver`, `ManeuverNode`, `CelestialBody`, `CBAttributeMapSO`, `ScienceUtil`, `PQS`, `VesselDeltaV`, `DeltaVStageInfo`, `DeltaVEngineInfo`, `CommNet.CommNetwork`, `CommNet.Network.Net`4`, `CommNet.CommRangeModel`, `CommNet.CommNetBody`, `CommNet.CommNetHome`, `CommNet.CommNetVessel`, `CommNet.CommNode`, `CommNet.CommNetParams`.
- **Open-source mods that do the same work**, pinned to a commit:
  - MechJeb2 `2d8fd1f` (MechJeb2 is GPL-3.0; **MechJebLib** files carry a permissive SPDX line, `LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+`):
    - [`OrbitExtensions.cs`](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/OrbitExtensions.cs)
    - [`OrbitalManeuverCalculator.cs`](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/OrbitalManeuverCalculator.cs)
    - [`CelestialBodyExtensions.cs`](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/CelestialBodyExtensions.cs)
    - [`ReentrySimulation.cs`](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/ReentrySimulation.cs)
    - [`MechJebModuleLandingPredictions.cs`](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/MechJebModuleLandingPredictions.cs)
    - [`Maneuver/TransferCalculator.cs`](https://github.com/MuMech/MechJeb2/blob/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJeb2/Maneuver/TransferCalculator.cs)
    - [`MechJebLib/Maneuvers/`](https://github.com/MuMech/MechJeb2/tree/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJebLib/Maneuvers)
    - [`MechJebLib/Lambert/`](https://github.com/MuMech/MechJeb2/tree/2d8fd1fa368fdbe293b62e17c098d9dd56161b7a/MechJebLib/Lambert)
  - Trajectories `1304f46`: [`src/Plugin/Predictor/Trajectory.cs`](https://github.com/Neuoy/KSPTrajectories/blob/1304f469ae8881a3b4658ff07fcb72040890d8b1/src/Plugin/Predictor/Trajectory.cs).
  - kOS `8f281a4`: [`src/kOS/Suffixed/Node.cs`](https://github.com/KSP-KOS/KOS/blob/8f281a459b6ea0ba917c91bbbc117e68bb948199/src/kOS/Suffixed/Node.cs).
  - TransferWindowPlanner `7465e99` (MIT; its Lambert solver is BSD, by Alex Moon): [`SharedStuff/LambertSolver.cs`](https://github.com/TriggerAu/TransferWindowPlanner/blob/7465e99af1d357d070279e3a57d9377758b8a21a/TransferWindowPlanner/SharedStuff/LambertSolver.cs).
  - Kerbal Alarm Clock `60ca80e`: its SOI alarms read the live solver's `patchEndTransition` (`KerbalAlarmClock/KerbalAlarmClock.cs`).
- **The mod's own code**:
  - `src/ClaudiaKermin.Adapter/SnapshotReader.cs`
  - `src/ClaudiaKermin.Core/Missions/DeltaVMap.cs`
  - `src/ClaudiaKermin.Core/Missions/ChildVessels.cs`
  - `src/ClaudiaKermin.Core/Claude/Background.cs`
  - the bundled RSVP in `kerboscript/rsvp/`

## Summary

| Tool | What computes it | Feasibility | Size |
|---|---|---|---|
| `predict_burn` | Stock `Orbit` plus `PatchedConics.CalculatePatch`, on detached `Orbit` objects, using the same node-frame maths as `PatchedConicSolver.CheckNextManeuver` | High. It gives the same result a real node would, without adding one. | M |
| `solve_burn` | Closed form for circularize, Ap and Pe (port from MechJebLib, permissive licence); a root-finder over `predict_burn` for the periapsis on the next patch | High | M |
| `site_info` | `ScienceUtil.GetExperimentBiome` / `CBAttributeMapSO.GetAtt`, `CelestialBody.TerrainAltitude` (PQS), finite-difference slope, sun vector from `getTruePositionAtUT` | High | S |
| `find_landing_site` | `site_info` over a grid or ground track; ground track from `Orbit.getRelativePositionAtUT` minus the body's rotation | High for airless bodies. Landing with an atmosphere is out of reach without an aero sim. | M |
| `transfer_window` | Own maths. Hohmann phase angle for moons (reuse `DeltaVMap`); a Lambert porkchop (port MechJebLib Gooding/Izzo or TWP's BSD solver) for planets. Stock offers `Orbit.SynodicPeriod` and `PhaseAngle` helpers only. | High | S (moons), M to L (interplanetary) |
| `stage_performance` | Stock per-stage masses and Isp from `DeltaVStageInfo`, plus the mod's own Isp curve and gravity per body. Stock TWR and ASL figures are only for the current body. | High | S |
| `link_windows` | Own maths on CommNet's model: `CommRangeModel` (range = √(Pa·Pb)), body occluders (R × 0.9 or 0.75), live antenna powers from `CommNode` | Medium to high | M (direct to home), L (relays) |

**Cheapest first:**
1. `stage_performance`: pure Core maths on data the Game Snapshot already carries.
2. `site_info`: a handful of stock calls.
3. `predict_burn`: medium, but it is the foundation that `solve_burn` and parts of `find_landing_site` and `link_windows` reuse.
4. The closed-form half of `solve_burn`.

## Cross-cutting constraints

**Main thread.** Everything in the game is main-thread only. `SnapshotReader` already says so. Some calls have hard reasons:
- `PQS.GetSurfaceHeight` writes into a shared scratch buffer (`vbData.Reset(); … Mod_OnVertexBuildHeight(vbData, …)`).
- `CelestialBody.GetLatitude` and `GetLongitude` store into fields (`rPos`, `latValue`).

Claude's replies arrive on a worker thread (`Core/Claude/Background.cs`), so a tool call has to be queued to the main thread. Two shapes work:
- **Run stock code on the main thread**, within a bounded budget per frame. Use this for `predict_burn`, terrain and biome.
- **Read inputs on the main thread, then compute on a worker** in Core with plain doubles. Use this for Lambert grids, CommNet sampling and stage maths.

MechJeb uses both shapes. Its `TransferCalculator` copies the origin and target orbits on the main thread (`_origin.UpdateFromOrbitAtUT(o, …)`), then runs the porkchop on `ThreadPool.QueueUserWorkItem`. Its `ReentrySimulation` captures surface basis vectors and `rotationPeriod` at the start, then simulates off-thread. Doing the maths in Core also makes it testable in `ClaudiaKermin.Core.Tests`, since Core has no Unity reference.

**Read-only.** These would break the "Planning Tools only read" rule, and each has a safe alternative:
- **`PatchedConicSolver.AddManeuverNode`.** kOS's `Node.cs` does exactly this (`v.patchedConicSolver.AddManeuverNode(time)`, then `UpdateFlightPlan()`). That is why kOS can't be the engine for `predict_burn`.
- **Passing a live orbit as `p` to `PatchedConics.CalculatePatch`.** It writes to `p`: `activePatch`, `nextPatch`, `patchEndTransition`, `StartUT` and `EndUT`. Passing `vessel.orbit` or a solver patch would clobber the live flight plan. Copy first, with `new Orbit(orbit)` or `UpdateFromOrbitAtUT`.
- **Writing `DeltaVGlobals.DeltaVAppValues`** (the delta-v app's body and situation). `SnapshotReader` already avoids this.
- **`VesselDeltaV.SetCalcsDirty`.** It triggers a recompute; not needed.
- **`ManeuverNode.AttachGizmo` and `Orbit.DrawOrbit`.** These are rendering. `CheckNextManeuver` calls `orbit.DrawOrbit()`, so don't call `CheckNextManeuver`; reproduce its maths instead.

**Physics frames and frames of reference.**
- On rails, `vessel.orbit` is analytic. Off rails, it is re-fitted from physics every frame, so a prediction is good for the frame it was read in.
- Positions in the world frame shift with the floating origin and, at low altitude, with Planetarium inverse rotation. Work in body-relative vectors (`getRelativePositionAtUT`, which MechJeb calls `WorldBCIPositionAtUT`), and read every vector in one frame.
- To get a ground longitude at a future UT, subtract `360·(UT−now)/rotationPeriod` from the longitude under the inertial point. MechJeb does this in `ReentrySimulation.ToAbsolute` and in `CelestialBodyExtensions.GetCurrentSurfacePositionFromUT`. Stock's own rotation is `rotationAngle = (initialRotation + 360·UT/rotationPeriod) % 360` (`CelestialBody`).

**Scenes.** Orbit and patch tools need a `Vessel`, so Flight (or the tracking station) only. Body data (`FlightGlobals.Bodies`) is available in every scene.

## `predict_burn`

**API.**
- `Orbit` (the copy constructor `new Orbit(Orbit)`, `GetOrbitalStateVectorsAtUT(UT, out State)`, `UpdateFromFixedVectors(pos, vel, body, UT)`, `ApR`/`PeR`/`ApA`/`PeA`, `StartUT`/`EndUT`, `patchEndTransition`, `nextPatch`, `closestEncounterBody`, `UTsoi`).
- `PatchedConics.CalculatePatch(p, nextPatch, startEpoch, new PatchedConics.SolverParameters(), targetBody: null)`.

**How stock applies a node.** This is the recipe to copy. `PatchedConicSolver.CheckNextManeuver`:

```
nodePatch.GetOrbitalStateVectorsAtUT(node.UT, out var state);
var pos = state.pos.xzy; var vel = state.vel.xzy;
Vector3d dv = QuaternionD.LookRotation(vel, Vector3d.Cross(-pos, vel)) * node.DeltaV;   // DeltaV = (radial, normal, prograde)
orbit.UpdateFromFixedVectors(pos.xzy, vel.xzy + dv.xzy, nodePatch.referenceBody, node.UT);
orbit.StartUT = node.UT; orbit.EndUT = orbit.eccentricity < 1 ? node.UT + orbit.period : orbit.period;
```

Doing the same on a detached `Orbit` gives the very orbit a real node of that `DeltaV` would give. That includes stock's node-frame convention, where "radial" is perpendicular to velocity in-plane, not the true radial. So a burn Claudia gets from the tool means the same as one she would put on a node.

**Next patches.** Then loop: `ok = PatchedConics.CalculatePatch(o, next = new Orbit(), o.StartUT, pars, null)`; while `ok`, set `o = next`, stopping at a patch limit or a horizon. `_CalculatePatch` checks every celestial `OrbitDriver` in `Planetarium.Orbits` that shares `p.referenceBody` for an SOI encounter (`CheckEncounter`). It also sets `ESCAPE` when `ApR` exceeds the SOI. It writes only to `p` and `nextPatch`.

MechJeb and Trajectories both do exactly this:
- MechJeb: `OrbitalManeuverCalculator.PatchedConicInterceptBody` borrows `Orbit`s, applies dV with `UpdateFromStateVectors`, and loops `CalculatePatch`.
- Trajectories: `Trajectory.cs` builds `new Orbit()`, then calls `PatchedConics.CalculatePatch(orbit, new Orbit(), state.Time, pars, null)`.

**Starting orbit.** Start from a copy of `vessel.orbit` (or the patch containing the burn UT, found by walking `nextPatch` on the copy). Don't start from `patchedConicSolver.flightPlan`. The live solver stops at its `patchLimit` (default 2) and already includes any player nodes. If Claudia wants "after the node already planned", that's a separate input.

**Main thread and physics.** Main thread; cheap (stock runs this every frame for the map view). No vessel state changes.

**Accuracy.**
- Identical to the map view's patched conics.
- Impulsive: a long burn is spread round the node, the same error the stock node has. It's worth returning the burn time (from stage thrust and mass, below) so Claudia can judge.
- No drag: a periapsis inside an atmosphere is reported, but what happens after it is not modelled.

**Gotchas.**
- Hyperbolic `EndUT` is set to `period` (stock quirk: it is not a UT). Mirror stock's handling.
- `Orbit.UpdateFromStateVectors` applies `Planetarium.Zup.LocalToWorld`, while `UpdateFromFixedVectors` doesn't. Use the fixed form with vectors from `GetOrbitalStateVectorsAtUT`, as stock does.
- `CalculatePatch` swallows exceptions and returns false. Treat false with a non-`FINAL` transition as "prediction failed".

**Size: M.** About 150 lines in the Adapter, plus a plain result type in Core: patches with body, Ap, Pe, inclination, eccentricity, start and end UT, transition and encounter. The JSON for the tool is the larger part. Reuse `SnapshotOrbit`'s fields.

## `solve_burn`

**API.** None stock: KSP has no node solver. Build it on `predict_burn`.

**Closed-form goals** (single burn at a given UT, same body):
- circularize at UT
- change Ap at UT, or change Pe at UT
- set both Ap and Pe at UT
- semi-major axis
- inclination at a node

MechJeb wraps these in `OrbitalManeuverCalculator` (`DeltaVToCircularize`, `DeltaVToChangeApoapsis`, `DeltaVToChangePeriapsis`, `DeltaVToEllipticize`, `DeltaVToChangeInclination`). The maths itself lives in `MechJebLib/Maneuvers/Simple.cs` and `ChangeOrbitalElement.cs`, under a permissive SPDX licence, so it can be ported into Core with attribution. These are vis-viva formulas on (μ, r, v); they need no game objects and are testable in Core. `DeltaVMap` already holds the vis-viva and Hohmann maths for a circular-to-circular case.

**Goals on the next patch** (periapsis at the Mun, return periapsis at Kerbin). Search the burn's prograde, normal and radial components, evaluating each guess with `predict_burn`, until the next patch's PeA hits the target. A 1-D secant or Brent search on the prograde component, with the time fixed, covers "tune periapsis on the next patch". That was one of the Custom Scripts in the Mun flight (#243). MechJeb's `DeltaVAndTimeForCourseCorrectionToCelestial` solves the same goal with `MechJebLib/Maneuvers/FineTuneClosestApproachToCelestial`, a two-body shooting method in the parent frame with a closest-approach target.

**Main thread.** Each evaluation is one or two `CalculatePatch` calls on the main thread. Cap the evaluations (for example 50) and spread them across frames if they need more than a few milliseconds. Measure in game. Closed-form goals can run anywhere.

**Accuracy.**
- Closed form: exact for the impulsive model.
- Next-patch search: converges to the stock patched-conic answer. An encounter that appears or vanishes as the dv changes gives the objective a jump, so return "no encounter" rather than a bad root.

**Gotchas.**
- Stock's node frame is not MechJeb's frame. MechJeb converts with `DeltaVToManeuverNodeCoordinates` (radial = `Exclude(prograde, up)`, normal negated). Output dv in stock node coordinates, so a block can create the node exactly.
- Raising Pe above the current radius, or lowering Ap below it, is impossible. MechJeb clamps these (`Clamp(newPeR, 1, radius − 1)`); report it instead.

**Size: M** (S for the closed-form set alone).

## `site_info`

**API.**
- **Biome:**
  - `ScienceUtil.GetExperimentBiome(body, latDeg, lonDeg)` → `body.BiomeMap.GetAtt(latRad, lonRad).name`
  - `ScienceUtil.GetExperimentBiomeLocalized` and `GetBiomedisplayName` for display names
- **Terrain height:**
  - `CelestialBody.TerrainAltitude(lat, lon, allowNegative)` → `pqsController.GetSurfaceHeight(GetRelSurfaceNVector(lat, lon)) − pqsController.radius`
  - With `allowNegative: true` it gives the sea floor. Clamped (the default), 0 means ocean, so check `body.ocean` and the sign.
- **Slope:** no stock call. Use finite differences of `TerrainAltitude`, as MechJeb's `CelestialBodyExtensions.GetPQSSlopeDegrees` does (four samples about 50 m apart).
- **Sun elevation at a UT:**
  - Sun direction: `Planetarium.fetch.Sun.getTruePositionAtUT(UT) − body.getTruePositionAtUT(UT)` (both recurse up the orbit tree analytically).
  - Site normal at UT: `body.GetSurfaceNVector(lat, lon + 360·(UT−now)/body.rotationPeriod)`.
  - Elevation = 90° − angle between the two.
  - `body.solarDayLength` gives the length of the day for "next sunrise". Stock computes it from the planet's orbit round the Sun and the body's rotation period.

**Main thread.** Yes: PQS uses the shared scratch buffer, and `GetLatitude` writes fields.

**Accuracy.**
- **Biome:** exact, the same lookup science uses.
- **Height:** the same PQS function the terrain mesh is built from. It covers terrain that isn't loaded, which Trajectories relies on (`body.pqsController.GetSurfaceHeight(rad) − body.Radius`). It does not include buildings or KSC statics.
- **Sun:** exact for stock, whose bodies all spin about the global up axis. Elevation ignores terrain shadowing (crater rims).

**Gotchas.**
- `GetExperimentBiome` throws when a body has no biome map; MechJeb wraps it (`GetExperimentBiomeSafe`: null or empty `Attributes` gives "").
- KSC mini-biomes (Runway, LaunchPad, and so on) come from colliders, not the map, so they aren't returned.
- Biome lat and lon go in as degrees and are converted with a float-precision constant. That's harmless.

**Size: S.**

## `find_landing_site`

**API.** `site_info` per candidate, plus a candidate generator:
- **Under the ground track:** sample `orbitCopy.getRelativePositionAtUT(t)` over the next N orbits. Convert to lat and lon with `body.GetLatitudeAndLongitude(rel, isRadial: true)`, then subtract the body's rotation since now. This is MechJeb's `ReentrySimulation.ToAbsolute` (`longitude -= 360 * (ut − _epoch) / rotationPeriod`). The track's lat and lon at a UT can then be checked against `TerrainAltitude` for an airless impact point.
- **Area search:** a lat/lon grid round a point, scored on biome (wanted, or not yet visited for science, from `SnapshotReader.ReadExperiment`'s subjects), slope under a limit, and sun above the horizon at arrival.

**Main thread.** Ground-track maths can run anywhere once the orbit elements and rotation are captured. Terrain, slope and biome samples need the main thread. A grid of 1,000 sites × 5 height samples is 5,000 PQS calls; the cost per call is unmeasured, so time it in game and spread the work across frames.

**Accuracy.**
- **Airless:** good. The impulsive deorbit plus a suicide-burn landing comes down close to the track.
- **With an atmosphere:** the impact point needs a drag simulation (MechJeb's `ReentrySimulation` or Trajectories). That is out of scope; give the track and the site facts only.

**Gotchas.**
- Stock `CelestialBody.GetImpactLatitudeAndLongitude` is a straight-line sphere intersection (position plus velocity × t), not an orbit. Don't use it for impact prediction.
- Inverse rotation at low altitude changes the world frame, but not body-relative vectors read in the same frame. Verify ground-track longitudes in game both below and above `inverseRotThresholdAltitude`.

**Size: M.** This was the biome-and-daylight Custom Script in the Mun flight (#243).

## `transfer_window`

**API.** Stock has helpers but no planner:
- `Orbit.SynodicPeriod(o1, o2)`
- `Orbit.PhaseAngle(time)`
- `Orbit.GetRelativeInclination`
- `Orbit.AscendingNodeTrueAnomaly`, `DescendingNodeTrueAnomaly`
- `Orbit.NextCloseApproachTime(p, s, UT)`
- body positions at any UT via `body.orbit.getRelativePositionAtUT(UT)`

Everything else is own maths.

- **Moons of the current body** (Kerbin to Mun or Minmus):
  - The window is set by the vessel's phase relative to the moon. Hohmann phase angle θ = π·(1 − ((r₁+r₂)/(2r₂))^1.5).
  - Time to window = (current phase − θ) / (n_vessel − n_moon), wrapped into one synodic period.
  - `DeltaVMap` already computes this transfer's departure, arrival and plane-change dv from body data. Reuse it.
  - Minmus's 6° inclination: depart at the relative node, or add the plane change as the Map does.
  - Result: departure UT, prograde dv, then `predict_burn` to confirm the encounter, and `solve_burn` to tune the periapsis.
- **Planets:**
  - A Lambert porkchop over departure UT × time of flight, in the Sun's frame, using the planets' orbits.
  - Ejection dv from the parking orbit: v∞ → Δv = √(v∞² + 2μ/r) − √(μ/r). This needs the ejection angle and the plane.
  - Ports, all into Core:
    - MechJebLib `Lambert/Gooding.cs` or `Izzo.cs`, with `Maneuver/TransferCalculator.cs` for the grid (permissive or GPL-3)
    - TransferWindowPlanner's `LambertSolver.cs` (BSD)
    - RSVP's Izzo solver, already bundled in `kerboscript/rsvp/lambert.ks` (a GPL-3 port of ESA's PyKep)

**Compared with RSVP in kOS (#228).** RSVP does the same Lambert search, but in kerboscript, limited by kOS's instructions per tick. #228 found it picking a 1,649 m/s window for the Mun against about 860 m/s for a Hohmann transfer. For moons, a Hohmann phase-angle tool is near optimal and runs in microseconds. In C#, a 100 × 100 porkchop is about 10⁴ Lambert solves, which is milliseconds on a worker. MechJeb runs its grid on `ThreadPool`.

**Main thread.** Read the orbit elements once on the main thread; the search runs on a worker.

**Accuracy.**
- Moons: impulsive, circular and coplanar Hohmann, then a stock patched-conic check. Good to a mid-course correction.
- Planets: Lambert in the Sun's frame. Patched-conic ejection error is corrected later.

**Gotchas.** Two `SnapshotBody` gaps: it carries `Sma` and `Inclination` only, and phase needs `LAN`, `argumentOfPeriapsis`, `eccentricity` and the mean anomaly at the epoch, or simply positions at a UT. Add those, or read the `Orbit` in the Adapter.

**Size:** S for moons (it reuses `DeltaVMap`, `predict_burn` and `solve_burn`); M to L for interplanetary.

## `stage_performance`

**API.** `vessel.VesselDeltaV.OperatingStageInfo[]` (`DeltaVStageInfo`):
- `startMass`, `endMass`, `dryMass`, `fuelMass`
- `ispVac`
- `thrustVac`
- `deltaVinVac`
- `stageBurnTime`

`SnapshotReader.ReadStages` already reads most of these.

**Why not stock TWR or ASL at another body:**
- `DeltaVStageInfo.CalculateTWR` divides by `Vessel.mainBody.GeeASL` in flight, and by `DeltaVGlobals.DeltaVAppValues.body.GeeASL` in the editor.
- `DeltaVEngineInfo.CalculateISP` evaluates `atmosphereCurve` at `vessel.mainBody.atmPressureASL` in flight, or at the app's body in the editor.

So stock gives "another body" only by changing the delta-v app's body, which is state the tool must not touch. Do the maths instead:
- g_body = GM/R² (from `SnapshotBody.Gm`, `Radius`)
- thrust at a pressure P = thrustVac × Isp(P)/IspVac, using each engine's `IspCurve` (already in `SnapshotPart`, in atmospheres and seconds)
- `SnapshotBody.SeaLevelAtm` gives P at sea level
- TWR = thrust / (startMass·g)
- burn time and stage dv from the rocket equation

`ChildVessels.Describe` already does a rough one-burn rocket equation (`ThrustVacKn / flow · G0 · ln(m/dry)`).

**Trickle throttle (#234).** `DeltaVEngineInfo` fills `amountPerSecondCurrentThrottle` from `ctrlState.mainThrottle`, which makes stock's delta-v read 0 with an engine lit at a trickle. For a stage marked `DeltaVUnreliable`, recompute dv as `ispVac · g0 · ln(startMass/endMass)` from stock's masses. Whether `endMass` is also wrong during a trickle is unverified, so check in game. If it is, use the stage's propellant mass from the parts.

**Main thread.** Reading stage info needs it, but that's already done by the Game Snapshot. The maths is plain Core.

**Accuracy.** Stock masses keep crossfeed and drain order. Own Isp at a pressure matches stock's `atmosphereCurve.Evaluate`. TWR ignores the `atmCurveIsp` density effect for jets; fine for rockets.

**Gotchas.**
- Multi-mode engines: the snapshot takes the strongest mode.
- Engines with `thrustPercentage` set below 100: stock's `thrustVac` includes it, but the per-part `ThrustVacKn` does not. Prefer the stage figures.

**Size: S.**

## `link_windows`

**API.** CommNet computes links for now only and has no predictor. The model is public and simple, though, so a predictor can be built on it:
- **Range:** `CommNetScenario.RangeModel` (stock `CommRangeModel`): in range if d² ≤ Pa·Pb; `GetMaximumRange` = √(Pa·Pb); normalized range = 1 − d/√(Pa·Pb).
- **Link strength:** `CommNetwork.TryConnect` takes the √ of each end's `rangeCurve.Evaluate(normalizedRange)`, times the signal multipliers.
- **Powers:** use the live `CommNode` values, which already have antennas combined and `CommNetParams.rangeModifier` applied (`CommNetVessel.UpdateComm`):
  - vessel: `vessel.connection.Comm.antennaTransmit.power` and `antennaRelay.power`
  - ground stations: `antennaRelay.power` (from `GameVariables.GetDSNRange(trackingStationLevel)`, or `antennaPower` for permanent ones)
  - all nodes: `CommNetNetwork.Instance.CommNet[i]` (with `Count`), and `isHome` / `isControlSource`
- **Occlusion:** `CommNetwork.TestOcclusion` raycasts against each body's `OccluderHorizonCulling`. Its radius is `body.Radius × scaledRadiusHorizonMultiplier × occlusionMultiplierVac (0.9) or occlusionMultiplierAtm (0.75)` (`CommNetBody.CreateOccluder`, `CommNetParams`).
- **Ground station positions:**
  - `CommNetHome`'s lat, lon and alt are protected. Read `nodeTransform.position` and run it through `body.GetLatLonAlt`, as `CreateNode` does.
  - Then rotate to the UT like a surface site.

**Prediction.** Sample UT over the horizon (for example every 30 to 60 s, then bisect each edge). At each sample, take the vessel's position from its orbit copy (patched as in `predict_burn`), ground stations rotated with the body, and relay vessels from their `orbit`s. Test range and occlusion pairwise. Direct to home is a check per station. With relays it is a connectivity search over the sampled graph, the same shape as `CommNetwork.FindHome`.

**Main thread.** Read nodes, powers and orbits once on the main thread; the sampling runs on a worker in Core.

**Accuracy.** Matches CommNet geometry. Not included:
- plasma blackout and other `GetSignalStrengthModifier` multipliers
- relays that will change course
- CommNet's update interval (`packedInterval` 0.5 s, `unpackedInterval` 5 s)

**Gotchas.**
- `CommNetScenario.CommNetEnabled == false` means always linked, matching `SnapshotReader.LinkedHome`.
- RemoteTech replaces CommNet. Detect it and say the tool is unsupported.
- `enableGroundStations` off leaves KSC only.
- `rangeModifier` and `DSNModifier` are difficulty settings. Read live powers rather than recomputing them.

**Size:** M (direct to home); L (with relays and multi-hop paths).

## Reuse in this repo

| Existing code | Reused by |
|---|---|
| `SnapshotReader.ReadStages`, `ReadParts` (`IspCurve`, `ThrustVacKn`), `ReadBodies` (`Gm`, `Radius`, `SeaLevelAtm`) | `stage_performance` |
| `SnapshotReader.MarkTrickleStages` (#234) | `stage_performance` (recompute flagged stages) |
| `SnapshotReader.ReadExperiment` (science still to gain per subject) | `find_landing_site` scoring |
| `SnapshotReader.LinkedHome` | `link_windows` (CommNet off) |
| `DeltaVMap` (vis-viva, Hohmann, plane change, low-orbit radii) | `transfer_window` (moons), `solve_burn` closed form |
| `ChildVessels.Describe` (rocket equation, `G0`) | `stage_performance` |
| `Core/Claude/Background` (worker to main-thread queue) | tool dispatch for every tool |

## Open checks in game

1. Cost per call of `PatchedConics.CalculatePatch` and `CelestialBody.TerrainAltitude`, to set the per-frame budgets.
2. `predict_burn` against a real node of the same DeltaV, placed by hand: the next-patch Pe should match.
3. Ground-track and sun-elevation longitudes below and above the inverse-rotation altitude.
4. Whether stock `endMass` is right during a trickle throttle (#234).
