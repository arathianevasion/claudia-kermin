# How KSP refuses or cuts rails warp, and how to tell a player cancel apart

Answers #300. Everything here was read from KSP 1.12.5 (build 03190) `Assembly-CSharp.dll` and kOS 1.6 `kOS.dll`
from `~/KSP_spike`, decompiled read-only with `ilspycmd` 11.1, the same way as ADR 0004. Method names are the
decompiled ones; private members are marked. Serialized values (warp rate tables, per-body altitude limits) were
read from the game's Unity asset files with UnityPy, by decoding the raw `TimeWarp` and `CelestialBody`
MonoBehaviour bytes (the type trees are stripped, so the byte layout was matched against the field order in the
decompiled class). Localized texts are the `en-us` entries in `GameData/Squad/Localization/dictionary.cfg`.
No claim here comes from the KSP wiki.

## Short answers

1. **Refusals.** All of KSP's own rails-warp limits are in one private method, `TimeWarp.getMaxOnRailsRateIdx`.
   It refuses outright (index 0) for a Kerbal on a ladder, for atmosphere, for acceleration over 0.1 g, and for
   moving over the ground faster than 0.3 m/s while landed. It lowers the rate for altitude (per-body limits), for
   atmosphere one physics frame ahead, and to 50x near an SOI change. Throttle is **not** checked as such.
   `TimeWarp.FixedUpdate` re-runs the check every physics frame while rails-warping off the ground, so a rate that
   was allowed is cut later. A cut lowers `CurrentRateIndex` for good; plain `WARP`/`SetRate` never comes back up.
   The only trace is a screen message and a `GameEvents.onTimeWarpRateChanged` that carries no arguments.
2. **Player vs. not.** There is no direct signal: `onTimeWarpRateChanged` is an `EventVoid` and every source ends
   in the same private `setRate`. The player's own paths are few and separate, though: the warp keys (read in
   `TimeWarp.Update` and in the warp-to coroutine), the eight `UITimeWarpController.ClickTimewarpN` buttons, and the
   stock warp-to buttons. A Harmony mod can tell a player's change apart by patching those entry points, or more
   cheaply by stamping which `TimeWarp` method a rate change happened inside (recipe below).
3. **`WarpTo`.** It is deferred one frame, then a coroutine raises the rate step by step up to a cap it picked at
   the start. If KSP lowers the rate partway (the SOI clamp, an altitude limit), the coroutine raises it again on its
   own once the limit allows: so it **does** resume after an SOI change. If the rate ever reaches index 0 (a
   refusal for atmosphere, acceleration or a ladder, or a refusal at the start), the coroutine **ends** for good,
   short of its target, and never retries. Any warp key, any warp button, a stock alarm, or deleting the node from
   the navball cancels it.
4. **Physics warp.** 1x, 2x, 3x, 4x, with no altitude, atmosphere, acceleration or SOI limit at all. The mode can
   only change at 1x, except that kOS writes the mode field directly. The physics step grows with the rate
   (0.02 s x rate), and stock warns that this "can affect the structural stability of the vessel".

## The rate tables

From the serialized `TimeWarp` component (identical in the flight, tracking-station and space-center scenes):

| Field | Value |
|---|---|
| `warpRates` (rails) | 1, 5, 10, 50, 100, 1000, 10000, 100000 |
| `physicsWarpRates` | 1, 2, 3, 4 |
| `maxPhysicsRate_index` | 0 (any rails index above 0 goes through the on-rails checks) |
| `maxModeSwitchRate_index` | 0 (the mode changes only at 1x) |
| `altitudeLimits` | 0, 0.05, 0.05, 0.1, 0.2, 0.4, 0.8, 1.0 (fractions of body radius, used only as defaults) |
| `GThreshold` (static) | 0.1 |

The limits actually used are per body: `GetAltitudeLimit(i, body)` returns `body.timeWarpAltitudeLimits[i]`, metres
above sea level, for rate index `i`. Stock values, decoded from the serialized `CelestialBody` objects:

| Body | 5x | 10x | 50x | 100x | 1000x | 10000x | 100000x |
|---|---|---|---|---|---|---|---|
| Kerbin, Eve, Laythe, Tylo | 30 km | 30 km | 60 km | 120 km | 240 km | 480 km | 600 km |
| Mun, Ike | 5 km | 5 km | 10 km | 25 km | 50 km | 100 km | 200 km |
| Minmus | 3 km | 3 km | 6 km | 12 km | 24 km | 48 km | 60 km |
| Duna | 30 km | 30 km | 60 km | 100 km | 300 km | 600 km | 800 km |
| Moho, Dres | 10 km | 10 km | 30 km | 50 km | 100 km | 200 km | 300 km |
| Gilly | 8 km | 8 km | 8 km | 20 km | 40 km | 80 km | 100 km |
| Pol | 5 km | 5 km | 5 km | 8 km | 12 km | 30 km | 90 km |
| Bop, Vall | 24.5 km | 24.5 km | 24.5 km | 40 km | 60 km | 80 km | 100 km |
| Eeloo | 4 km | 4 km | 20 km | 30 km | 40 km | 70 km | 150 km |
| Jool | 0 | 15 km | 60 km | 150 km | 300 km | 600 km | 1200 km |
| Sun | 3,270 km | 3,270 km | 6,540 km | 13,080 km | 26,160 km | 52,320 km | 65,400 km |

The Jool row came out of a looser byte match than the others and should be confirmed in game. A body whose array
has the wrong length is reset to `altitudeLimits[i] x Radius x ORBIT_WARP_ALTMODE_LIMIT_MODIFIER`
(`CelestialBody.resetTimeWarpLimits`), and a planet pack can replace any of these. A mod can read the live values
from `body.timeWarpAltitudeLimits` (public), and kOS gives the rate tables as `KUNIVERSE:TIMEWARP:RAILSRATELIST`
and `PHYSICSRATELIST`.

## 1. Every way KSP refuses, lowers or cuts rails warp

### The rate path

Every rate change ends in the private `TimeWarp.setRate(rateIdx, instantChange, instantIfLower, forceSwitch,
postScreenMessage)`. The public `TimeWarp.SetRate(index, instant, postScreenMessage)` clamps the index and calls
it; kOS's `WARP` and `RATE` setters call `TimeWarp.SetRate` (`kOS.Suffixed.TimeWarpValue.SetWarpRate`). In
`HIGH` (rails) mode, for an index above 0, `setRate` asks `getMaxOnRailsRateIdx(rateIdx, lookAhead: true, out
reason)` for the highest allowed index and uses that instead. If it is above 0 it also sets the input lock
`TimeWarpLock` (`ALL_SHIP_CONTROLS_ALLOW_UIMODE`: the player's throttle, staging and other ship controls; kOS does
not go through input locks). It then sets `current_rate_index` to the allowed index and calls `assumeWarpRate`,
which fires `GameEvents.onTimeWarpRateChanged`. `setRate` returns `true` only if it got the index it was asked for.

### The checks, in order (`getMaxOnRailsRateIdx`)

Only in flight, for the active vessel, for a request above index 0:

| # | Condition | Effect | `ClearToSaveStatus` reason | Screen message (posted by `setRate`) |
|---|---|---|---|---|
| 1 | Any loaded vessel is an EVA Kerbal on a ladder | index 0 | `NOT_WHILE_ON_A_LADDER` | "Cannot warp faster than 1x while Kerbals are on a ladder." (`#autoLOC_6001929`) |
| 2 | Not landed, and static pressure at the vessel's altitude > 0 | index 0 | `NOT_IN_ATMOSPHERE` | "Cannot warp faster than 1x while in atmosphere." (`#autoLOC_6001925`) |
| 3 | Not landed, and `vessel.geeForce > 0.1` (g) | index 0 | `NOT_UNDER_ACCELERATION` | "Cannot warp faster than 1x while the ship is under acceleration." (`#autoLOC_6001926`) |
| 4 | `ORBIT_WARP_MAXRATE_MODE = VesselAltitude` only: altitude below the body's limit for the asked index | lowered to `GetMaxRateForAltitude` | `NOT_WHILE_ABOUT_TO_CRASH` | "Cannot warp faster than {rate}x while below {limit}m." (`#autoLOC_6001928`) |
| 5 | Look-ahead: the altitude one physics frame ahead at the asked rate (0.02 s x rate of game time, or the periapsis if that comes sooner) is in atmosphere or below sea level | lowered one index at a time until it isn't, down to 0 | `NOT_IN_ATMOSPHERE` | as row 2, with the lowered rate |
| 6 | Look-ahead altitude below the body's limit for the index, **unless** `PeAltitude` mode and Pe > `minPeAllowed` + 250 m | lowered one index at a time until above the limit | `NOT_WHILE_ABOUT_TO_CRASH` | VesselAltitude mode: row 4's text. PeAltitude mode: "Cannot warp faster than {rate}x while below {limit}m and Pe below {min}m" (`#autoLOC_8003368`) |
| 7 | Landed or splashed, and surface speed > 0.3 m/s (`srf_velocity.sqrMagnitude > 0.09`) | index 0 | `NOT_WHILE_MOVING_OVER_SURFACE` | "Cannot warp faster than 1x while moving over terrain." (`#autoLOC_6001927`) |
| 8 | `ORBIT_WARP_DOWN_AT_SOI` (default true), patched conics on, the current patch does not end `FINAL` (an encounter, an escape), index above 3, and the patch ends within about `(TimeScale + 50)/2 + 50` s of game time (`ClampRateToOrbitTransitions(rate, orbit, 3, 50)`) | lowered to index 3 (50x) | `ORBIT_EVENT_IMMINENT` | none; logs "Orbit event imminent. Dropping TimeWarp to max limit: 50x" |

`minPeAllowed` is the largest of the body's highest terrain (`pqsController.radiusMax - radius`), its
`meshVertMax`, and its atmosphere depth. The defaults are in `GameSettings`: `ORBIT_WARP_MAXRATE_MODE =
PeAltitude`, `ORBIT_WARP_PEMODE_SURFACE_MARGIN = 250`, `ORBIT_WARP_ALTMODE_LIMIT_MODIFIER = 1`,
`ORBIT_WARP_DOWN_AT_SOI = true`, all of which a player can change in `settings.cfg` (`~/KSP_spike/settings.cfg`
has these defaults). So in the default PeAltitude mode a vessel on a safe orbit (Pe above terrain and atmosphere
plus 250 m) gets no altitude limit at all: only the atmosphere look-ahead and the SOI clamp apply. On a low or
impact orbit the per-body limits apply to where the vessel will be one frame ahead.

Every rate change shows "Time Warp: {rate}x" (`#autoLOC_7003002`) as well, unless `postScreenMessage` is false.

### Refusals outside that method

| Where | Condition | What happens |
|---|---|---|
| `TimeWarp.Update`, `btnSetHighRate`, `UITimeWarpController.CanSetHighRate` | Game parameter `Flight.CanTimeWarpHigh` (or `CanTimeWarpLow`) false | "Cannot warp time in this scenario." (`#autoLOC_481056`); nothing changes. The kOS path does not check this. |
| `TimeWarp.Update`, warp buttons | Any input lock that includes `ControlTypes.TIMEWARP` (pause, planting a flag, the tutorials) | The keys and buttons do nothing. kOS ignores input locks. |
| `TimeWarp.setMode` (keys, buttons) | Changing mode above 1x | "Cannot switch time rate modes while above 1.0x Warp." (`#autoLOC_6001930`). kOS's `MODE` setter writes `TimeWarp.fetch.Mode` directly and skips this check. |
| `TimeWarp.WarpTo` | A warp-to already running | "Warp already in progress" (`#autoLOC_6001931`); the new target is ignored. |

Throttle is not a check in itself. `ClearToSaveStatus.NOT_WHILE_THROTTLED_UP` exists but is used only for saving
and the pause menu (`FlightGlobals`, `PauseMenu`), never in `TimeWarp`. A burning engine refuses warp only through
row 3, the 0.1 g acceleration test. Nothing about the vessel being packed or "settled" is checked: vessels go on
rails as a result of the rate being above 1x (`OrbitPhysicsManager`), not as a condition for it. KSP does ignore
g-forces for a while after a reference-frame change (`FlightGlobals.PostOrbitSet` calls
`activeVessel.IgnoreGForces(20)`), and every rate change calls `FlightLogger.IgnoreGeeForces(1f)`.

### Cuts after the rate was allowed

- **The continuous clamp.** `TimeWarp.FixedUpdate`, every physics frame: in HIGH mode, if the current rate is above
  1x and the active vessel is not landed or splashed, it calls `setRate(current_rate_index, instantChange: false,
  instantIfLower: true, forceSwitch: false)`. That runs every check above again; if the allowed index is now lower
  it switches down, at once for atmosphere or altitude reasons and smoothly otherwise. This is how an SOI approach,
  a dip toward atmosphere or a low periapsis cuts warp in mid-flight. `setRate` has no memory of the index that was
  asked for: after the cut `current_rate_index` stays at the lower value. Nothing in KSP raises it again, except a
  running warp-to (section 3).
- **Auto mode switch.** `TimeWarp.Update` at 1x only: in rails mode it switches to physics when the vessel is in
  atmosphere (not landed) or moving on the ground (`ship_srfVelocity.sqrMagnitude > 0.1`); in physics mode it
  switches back to rails in vacuum, or landed and still. This changes the mode, not the rate, but it means a kOS
  `set kuniverse:timewarp:mode to "RAILS".` in atmosphere is undone on the next frame.
- **Stock alarms.** `AlarmTypeBase` (an alarm firing with "kill warp" or "pause"): `CancelAutoWarp()` then
  `SetRate(0, instant: true)`. `AlarmClockScenario` (an alarm coming up): `CancelAutoWarp()` then one index down,
  repeatedly, until it is safe for the time left.
- **Unpausing.** `FlightDriver.SetPause(false)` re-applies the same index with `SetRate(CurrentRateIndex, instant:
  true)`. Pausing itself only sets `Time.timeScale = 0`.
- **The stock "kill warp" from the maneuver UI.** Deleting the next node from the navball burn indicator
  (`NavBallBurnVector.OnDismissButton`) calls `CancelAutoWarp(0)` if a warp-to is running.

### What it looks like to a mod

| Surface | Use |
|---|---|
| `TimeWarp.CurrentRateIndex` (public static, the `current_rate_index` field) | The index after the clamp: lower than asked when refused or cut. kOS: `KUNIVERSE:TIMEWARP:WARP`. |
| `TimeWarp.CurrentRate` (`curr_rate`) and `fetch.tgt_rate` | The rate now and the one being lerped to. kOS: `RATE`; `ISSETTLED` is true when `RATE` is within 0.05 of the table entry for `WARP`. |
| `TimeWarp.WarpMode` / `fetch.Mode` | HIGH (rails) or LOW (physics). |
| `fetch.warpRates`, `physicsWarpRates`, `altitudeLimits`, `body.timeWarpAltitudeLimits` | The tables above. |
| `GameEvents.onTimeWarpRateChanged` | `EventVoid`, no arguments. Fired by `assumeWarpRate` (every rate change, including KSP's own clamps) and by every successful `setMode`, even one that did not change the mode. It says that something changed, never who or why. |
| `ScreenMessages` | The texts above. The rate messages go to one reused `ScreenMessage` in `UPPER_CENTER`; warp-to's go to a separate one in `LOWER_CENTER`. A mod can't ask for the reason, though the private `getMaxOnRailsRateIdx` can be called by reflection to get the `ClearToSaveStatus` (it only writes the private `minPeAllowed` field). |
| `KSP.log` | Warp-to logs "Warping to UT:…", "Warping down…" and "Real-time resumed… (Undershot/Overshot)"; the SOI clamp logs "Orbit event imminent…". Plain refusals are not logged. |

## 2. Telling the player's own change apart

**No direct signal.** All sources reach the same private `setRate`, and the only event has no arguments.

The player's own entry points, from the decompiled code:

| Player action | Code path |
|---|---|
| `.` / `,` (and Alt+ for physics) | `TimeWarp.Update`: `GameSettings.TIME_WARP_INCREASE` / `TIME_WARP_DECREASE` `.GetKeyDown()` then `setRate(current ± 1)`; the physics path goes through `setMode(LOW)` and the physics-warp warning dialog at index 2. |
| `/` | `TimeWarp.Update`: `TIME_WARP_STOP.GetKeyDown()` then `CancelAutoWarp()` and `setRate(0)`. |
| Any of those keys during a warp-to | The warp-to coroutine reads the same keys (`/` cancels to 0; `,` or `.` cancel the warp-to and step the rate one index). `TimeWarp.Update` also still runs, since `WARPTO_LOCK` excludes `TIMEWARP`. |
| The eight warp arrows | `KSP.UI.UITimeWarpController.ClickTimewarp0` … `ClickTimewarp7`: `CancelAutoWarp()` then `TimeWarp.SetRate(n)`. |
| Warp-to from the map context menu | `AutoWarpToUT.OnSelect` → `TimeWarp.fetch.WarpTo` |
| Warp to the next node (navball) | `NavBallBurnVector.OnWarpToButton` → `WarpTo` |
| Warp from the maneuver node editor | `ManeuverNodeEditorManager` → `WarpTo` |
| Warp to an alarm (alarm clock UI) | `AlarmClockUIFrame.OnWarpTo` → `CancelAutoWarp()` then `SetRate(SafeRateToUTPeriod(...))` |
| Deleting the next node from the navball | `NavBallBurnVector.OnDismissButton` → `CancelAutoWarp(0)` if a warp-to is running |

Everything else that changes the rate is KSP or a mod: `TimeWarp.FixedUpdate` (the clamp), `TimeWarp.Update`'s
auto mode switch and its start of a pending warp-to, the warp-to coroutine's own steps, `AlarmTypeBase` and
`AlarmClockScenario`, `FlightDriver.SetPause`, `UISpaceCenter` and `UITimeWarpScrubber` (scene setup), the
tutorials, and kOS (`TimeWarpValue.SetRate`, `SetWarp`, `WarpTo`, `CancelWarp`, `SetModeAsString`, and the
`warpto` function, all ending in `TimeWarp.SetRate`, `TimeWarp.fetch.WarpTo` or `CancelAutoWarp`).

**The cleanest detection for a Harmony mod** is to stamp each rate change with the method it happened inside:

1. Postfix on `TimeWarp.assumeWarpRate` (private; it is called on every real rate change and is where
   `onTimeWarpRateChanged` fires). It records `current_rate_index` and the current "source" scope.
2. Set the scope with prefix/postfix pairs (a `[ThreadStatic]` or plain static, since it is all the main thread):
   - `UITimeWarpController.ClickTimewarp0`…`7`: **player** (button).
   - `TimeWarp.Update`: **player** if `setAutoWarp` was false on entry (the only rate changes left in `Update` are
     the warp keys); otherwise **warp-to start**.
   - The warp-to coroutine's `MoveNext` (`TimeWarp+<autoWarpTo>d__70`, `AccessTools.EnumeratorMoveNext` on
     `autoWarpTo`): **warp-to step**, unless `CancelAutoWarp` ran inside it, which there means a key, so **player**.
   - `TimeWarp.FixedUpdate`: **KSP clamp**.
   - `AlarmTypeBase` / `AlarmClockScenario` methods: **alarm**.
   - The mod's existing kOS patches on `TimeWarpValue` (`RuntimeGatePatches`), plus one on `CancelWarp`: **script**.
3. Who started a warp-to: prefix on `TimeWarp.WarpTo`. Inside the scope set by the `TimeWarpValue.WarpTo` prefix it
   is the script; otherwise the player or another mod.

A smaller version that is enough for "did the player cancel the script's warp": watch for a rate drop with the
mod's own scope not set and none of the KSP scopes set, and in the same frame either a `GameSettings.TIME_WARP_*`
key down (readable from any `Update`) or a `ClickTimewarpN` call. Whether a warp-to is running is public without
reflection: `InputLockManager.IsLocked(ControlTypes.WARPTO_LOCK)`, the `TimeWarpTo` lock the coroutine holds
(`NavBallBurnVector` uses exactly this test). A drop to 0 while that lock is gone and no player scope was seen
means KSP ended the warp-to.

## 3. `TimeWarp.WarpTo`

Read from `TimeWarp.WarpTo`, `Update`, `autoWarpTo` (the compiler-generated coroutine) and `CancelAutoWarp`.

- **Deferred.** `WarpTo(UT, maxTimeWarping = 8, minTimeWarping = 2.5)` only stores the target and sets
  `setAutoWarp`; a call while a warp-to runs is refused ("Warp already in progress"). The next `TimeWarp.Update`
  picks a cap `rateIdx` from the time to go (`getMaxWarpRateForTravel`: under 8 s of game time it is 0, otherwise
  roughly the rate that covers the gap in a few real seconds), raises the rate one index, and starts the coroutine.
  It never changes the mode (as ADR 0004 found).
- **Each physics frame** the coroutine: stops if `autoWarpEngaged` was cleared (a cancel); if the rate is below the
  cap, asks `getMaxOnRailsRateIdx(rateIdx, lookAhead: true)` again and raises to whatever is now allowed (posting
  "TimeWarp Stopped." if that is 0); handles the warp keys; and then carries on only while the time is before the
  deceleration point **and** `current_rate_index > 0`.
- **Partial cuts: it resumes on its own.** The SOI clamp (to 50x), an altitude limit, or an alarm stepping it down
  leave the index above 0, so the coroutine keeps running and raises the rate back to its cap as soon as the limit
  lifts, e.g. once the vessel is through the SOI change. (An alarm usually also calls `CancelAutoWarp`, which ends
  it.)
- **A drop to 1x ends it, for good.** If a check returns 0 (atmosphere, acceleration, a ladder, moving over terrain),
  or the first step was already refused, the loop's `current_rate_index > 0` test fails and the coroutine finishes:
  it logs "Real-time resumed. Current UT is: … TgtUT Error is -…s. (Undershot)" (with no "Warping down" line before
  it), removes the `TimeWarpTo` lock and clears `autoWarpEngaged`. It does not call `CancelAutoWarp`, so there is no
  "Auto-TimeWarp Cancelled." message; the player sees the refusal text and "TimeWarp Stopped.". Nothing retries.
  A refusal at the very start therefore looks like a warp-to that ran for one frame.
- **Normal end.** At the deceleration point it sets the clock back to it (`Planetarium.SetUniversalTime`), drops to
  index 0, posts "Warping... T-…" until the rate has settled, then "TimeWarp Complete", and logs "Warping down…" and
  "Real-time resumed… (Overshot/Undershot)".
- **Cancels.** `CancelAutoWarp(rateIdx = -1)` (public) posts "Auto-TimeWarp Cancelled.", removes the lock, clears
  `autoWarpEngaged` and, if `rateIdx >= 0`, sets that rate; with the default -1 it leaves the rate where it is.
  kOS's `CANCELWARP` calls `CancelAutoWarp()` and then `SetWarp(0)`.

For `claudia_warp_to`, which asks once: an SOI change on the way does not need a second ask, but any refusal that
reaches 1x (most often atmosphere, or acceleration over 0.1 g just after a burn, staging or load) ends the warp-to
and leaves the script at 1x until `ut`. A kOS script can see this as `KUNIVERSE:TIMEWARP:WARP = 0` with no hold and
no player change; telling "no player change" apart needs the mod's help (section 2).

## 4. Physics warp

- **Rates.** `physicsWarpRates` = 1, 2, 3, 4: the maximum is 4x.
- **No limits.** In LOW mode `setRate` only clamps the index to the table; it never calls `getMaxOnRailsRateIdx`,
  and `FixedUpdate`'s clamp only runs in HIGH mode. So there is no altitude, atmosphere, acceleration, ladder or SOI
  limit on physics warp. The only gates are the `CanTimeWarpLow` game parameter, a `TIMEWARP` input lock, and the
  mode switch, which the keys allow only at 1x.
- **How it works.** `updateRate` sets `Time.timeScale = rate` and `Time.fixedDeltaTime = 0.02 x rate` in LOW mode
  (in HIGH mode both stay at 1 and 0.02, and only `Planetarium.TimeScale` changes). So at 4x each physics step is
  0.08 s instead of 0.02 s, and vessels stay unpacked and simulated.
- **Stability.** Stock says so itself: going to physics index 2 (3x) from the keys, with `SHOW_PWARP_WARNING` on,
  pauses the game and shows "Using Physics Warp can affect the structural stability of the vessel. Be careful using
  it with large ships." (`#autoLOC_6003092`, `TimeWarp.ShowPWarpWarning`). The code does nothing else about it. The
  mechanism is the larger fixed step: joints, wheels and landing legs are integrated with four times the step.
  (That large ships wobble or break apart at 3x and 4x is common player experience, not something this code shows.)
- **The auto mode switch** moves rails to physics at 1x in atmosphere or when moving on the ground, so physics is
  KSP's default there; ADR 0003's gate is what keeps a mod-started program out of it.

## Sources

All in the decompiled assemblies, from `~/KSP_spike`:

- KSP `Assembly-CSharp.dll`: `TimeWarp` (`Update`, `FixedUpdate`, `setRate`, `SetRate`, `getMaxOnRailsRateIdx`,
  `GetMaxRateForAltitude`, `ClampRateToOrbitTransitions`, `assumeWarpRate`, `updateRate`, `setMode`, `WarpTo`,
  `CancelAutoWarp`, `autoWarpTo`, `getMaxWarpRateForTravel`, `btnSetHighRate`, `ShowPWarpWarning`),
  `ClearToSaveStatus`, `ControlTypes` (`WARPTO_LOCK`, `TIMEWARP`), `GameSettings` (the `ORBIT_WARP_*` defaults),
  `GameParameters.FlightParams` (`CanTimeWarpHigh/Low`), `CelestialBody` (`timeWarpAltitudeLimits`,
  `resetTimeWarpLimits`), `Orbit.PatchTransitionType`, `GameEvents.onTimeWarpRateChanged`, `FlightDriver.SetPause`,
  `FlightGlobals.PostOrbitSet`, `OrbitDriver`, `OrbitPhysicsManager`, `AlarmTypeBase`, `AlarmClockScenario`,
  `KSP.UI.UITimeWarpController`, `KSP.UI.AlarmClockUIFrame`, `NavBallBurnVector`, `ManeuverNodeEditorManager`,
  `AutoWarpToUT`, `Vessel.IsClearToSave`.
- kOS `kOS.dll`: `kOS.Suffixed.TimeWarpValue` (all suffixes), `kOS.Function.WarpTo`.
- Serialized data: the `TimeWarp` component in `level5`, `level7` and `sharedassets8.assets`, and the
  `CelestialBody` components, in `KSP.app/Contents/Resources/Data`.
- Text: `GameData/Squad/Localization/dictionary.cfg` (en-us).
