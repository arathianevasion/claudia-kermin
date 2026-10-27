# RCS Use thresholds and how kOS steering behaves when RCS switches

Answers #387 (map #386). Sources:

- **kOS 1.6.0.1**, the version in `~/KSP_spike` (`KSP.log` line 67: `kOS v1.6.0.1 / v1.6.0.1+d4388439…`). Source read at that
  commit on GitHub (`KSP-KOS/KOS@d4388439`): `src/kOS/Control/SteeringManager.cs`, `src/kOS.Safe/Encapsulation/PIDLoop.cs`,
  `src/kOS/Binding/ActionGroups.cs`, `src/kOS/Suffixed/VesselTarget.cs`, and the docs in the same tree
  (`doc/source/structures/misc/steeringmanager.rst`, `doc/source/commands/flight/cooked.rst`,
  `doc/source/structures/vessels/vessel.rst`, `doc/source/commands/flight/systems.rst`).
- **KSP 1.12.5** `ModuleRCS`, decompiled read-only from `~/KSP_spike/KSP.app/.../Managed/Assembly-CSharp.dll` with
  `ilspycmd` 11.1, as in `ksp-rails-warp-refusals.md`. Names are the decompiled ones.
- **Logs**: `~/KSP_spike/KSP.log` for 27 Oct 2026, 08:39 to 10:16. It covers both missions named in the ticket:
  `m20261027-134420-9cd2` ("Mun Shot", the #377 run) and `m20261027-141128-3c1d` ("Mun Shot v2"). Also each mission's
  `events.jsonl`, `plans.jsonl` and `transcript.json` under `saves/Claudia Test 2/ClaudiaKermin/missions/`.
- Repo code at `451a85c`: `claudia_lib.ks`, `exec_node.ks`, `CoastGuard.cs`, `TurnTime.cs`, `TurnCraftReader.cs`.

## Short answers

1. **Defaults.**
   - On-threshold: **|angle error| > 10°**, and the wheels alone can't make the turn.
   - Off-threshold: **|angle error| < 2° and rotation rate < 0.5°/s**. This is the same test as `claudia_aligned`
     and TurnTime's settle, so a turn that counts as aligned also switches RCS off.
   - **The logs carry no angle-error or rate figures**, so these defaults are reasoned from kOS, KSP and the craft
     figures the logs do carry; the figures are below. The helper should log the figures listed under "What to log".
2. **kOS follows the RCS action group on the next physics frame, both ways.**
   - Every physics frame, cooked steering recomputes its available torque from the parts, and RCS counts only while
     KSP's `ModuleRCS.rcs_active` is true. KSP copies `rcs_active` from the RCS action group every `FixedUpdate`.
   - A mid-turn switch does **not** reset or retune the PIDs. The torque PI's gains depend only on the moment of
     inertia and `*TS`. The torque sets only the turn-rate cap and how a torque request maps to control input.
   - Switching off while the craft is turning faster than the wheels-only cap can overshoot. That is why switching
     off must wait for a low rate.
   - Rate per tick: `ship:angularvel` (rad/s, SHIP-RAW, inertial) or `steeringmanager:angularvelocity` (rad/s, the
     craft's own axes).
3. **Things the helper must respect**:
   - KSP ignores rotation input under 5% on each axis. Thrust is proportional to the input otherwise, unless the
     part is set to full thrust.
   - Translation input (`ship:control:fore` etc.) fires RCS too, and so does throttle on parts with `useThrottle`.
   - SAS on hands kOS steering to stock SAS, which also uses RCS.
   - With no monopropellant, the thrusters flame out: kOS stops counting their torque, and they give no thrust. The
     action group still reads on.
   - kOS ignores precision mode when it sizes RCS torque.
   - `turntime`'s RCS torque comes from the stock call that kOS says is broken for `ModuleRCS`.

## 1. Thresholds

### What the logs hold

- **No angle error or rate anywhere.**
  - Script Results, `events.jsonl` and the Game Snapshots in `transcript.json` carry `rcs_on` (true in all 24
    snapshots across both missions: the player launched with RCS on, and nothing turned it off).
  - The `land trace` lines in `KSP.log` carry height, speeds, throttle and targets, but no attitude.
  - `turn_s` / `turn_time` / `not_aligned` appear only in the block docs in the prompt. No Script Result in these two
    missions reports them.
  - So the thresholds can't be read off a settle in these logs.
- **Craft torque and inertia, from `block call TURNTIME` lines.** Each line logs wheel and RCS torque about pitch/yaw and
  the MOI. Re-running TurnTime's model (190°, `maxstoppingtime` 2 s) on them with RCS removed:

  | Run (KSP.log time) | Wheels kN·m | RCS kN·m (pitch/yaw) | MOI t·m² | Wheels-only α °/s² | Rate cap: wheels / +RCS °/s | 190° turn: wheels / +RCS s |
  |---|---|---|---|---|---|---|
  | 9cd2 Circularize at 80 km (08:51) | 5/5 | 0.27/0.38 | 33/30 | 8.7 | 17.3 / 18.3 | 13.0 / 12.4 |
  | 9cd2 Orbit raise (09:00) | 5/5 | 0.36/0.30 | 33/29 | 8.7 | 17.5 / 18.7 | 12.9 / 12.2 |
  | 3c1d Launch, circularize (09:16) | 5/5 | 1.73/0.67 | 572/560 | 0.50 | 1.00 / 1.16 | 191.8 / 165.9 |
  | 3c1d Mun transfer (09:19) | 5/5 | 1.86/0.18 | 398/398 | 0.72 | 1.44 / 1.49 | 133.9 / 129.3 |
  | 3c1d Mun transfer (09:23) | 5/5 | 0.83/1.33 | 334/334 | 0.86 | 1.71 / 2.00 | 112.8 / 97.0 |
  | 3c1d Mun capture (09:26) | 5/5 | 0.53/0.25 | 333/333 | 0.86 | 1.72 / 1.81 | 112.3 / 107.0 |
  | 3c1d Land (09:31) | 5/5 | 1.61/0.15 | 312/312 | 0.92 | 1.84 / 1.89 | 105.5 / 102.5 |

  The +RCS columns reproduce the logged `turn … at up to …` figures exactly, so the model matches the mod.
  - **On these craft RCS gives 4–37% extra torque on an axis, and cuts a 190° turn by only 3–14%.**
  - With the "wheels alone can't" condition, RCS would seldom come on for either craft. The rule's main effect on
    these missions is that RCS stays off.
- **What the #377 drops show.**
  - `KSP.log` has 615 `physics warp dropped: thrust` lines.
  - **Circularize at 80 km** (9cd2 plan 2, run 08:51:45–08:59:45): 278 drops (the #377 count). The median gap was
    0.57–0.85 s, with p10 at 0.18 s.
  - **Orbit raise** (plan 3, run 09:00:49–09:03:55): 334 drops, median gap 0.22 s. The craft was coasting between
    62 and 80 km in Kerbin's upper air.
  - The Mun Shot v2 had 2 drops, both on its launch coast at 09:15. Its later coasts warped on rails, where KSP's
    `ModuleRCS.FixedUpdate` returns early and RCS never fires.
  - **What firing implies.** KSP fires RCS only when a rotation input is at least 0.05 (see 3).
    - The 9cd2 craft's control torque is about 5.3 kN·m with RCS. A drop therefore means kOS asked for at least
      about 0.27 kN·m on some axis.
    - The drops came every 0.2–0.85 s. So while "holding" in thin air, kOS was over 5% actuation most of the time.
    - The RCS added only about 6% torque, and the wheels could have held the craft alone.
  - The drop line doesn't say whether an engine or RCS tripped it. `CoastGuard.OnCoast` already builds both lists,
    so the log could name which one.

### Recommended defaults

| Setting | Default | Why |
|---|---|---|
| On: angle error | **> 10°** (`abs(steeringmanager:angleerror)`), *and* wheels-only can't make the turn in the budget, or there's no usable wheel torque | Above the 5° bands where hold wobble lives: `claudia_q_rate`'s steering clamp and kOS's `ROLLCONTROLANGLERANGE` (default 5°). 10° is also `exec_node`'s gate for starting the burn, which is the turn where help matters. Below it a turn is short: on the slowest logged craft (α 0.5°/s²), wheels-only covers 10° in about 12 s (capped at 1°/s). |
| Off: angle error | **< 2°** | Same as `claudia_aligned` (claudia_lib.ks:934) and TurnTime's settle check. The on/off gap of 2° to 10° is wide enough that a settled craft doesn't switch back on. |
| Off: rotation rate | **< 0.5°/s**, using `ship:angularvel:mag * constant:radtodeg` | Same as `claudia_aligned`. It sits above the rate needed to follow prograde in a low orbit (Kerbin 80 km: 0.19°/s; Mun 30 km: 0.13°/s; Minmus 20 km: 0.11°/s), and below the wheels-only rate cap of every logged craft (≥ 1.0°/s). |
| Guard on the off rate | `min(0.5, 0.5 * wheels-only cap)`, where the wheels-only cap is α_wheels × `maxstoppingtime` in °/s | Only matters for a craft whose wheels-only cap is under 1°/s. If RCS goes off while the craft turns faster than that cap, the wheels must brake from above it, and the craft overshoots. |
| `rcs_tolerance` (per block) | scales or replaces the on-threshold only | The off test is the alignment test. Changing it would make "aligned" and "RCS off" disagree. |

Caveats to settle in the spec:

- **Off while tracking a moving target.** The rate is inertial. When steering follows something that turns faster
  than 0.5°/s, the rate test never passes, and RCS that came on stays on. Examples: `launch`'s pitch program, and
  `land` following retrograde late in braking.
  - Option 1: also switch off when the error has stayed under 2° for a few seconds.
  - Option 2: measure the rate against the target's own rate (two `steeringmanager:target` samples per tick).
- **Craft with no wheels.** The rule then lets the error grow to 10° before RCS fires, and stops it at 2°/0.5°/s. That
  is a slow, cheap limit cycle. A lower on-threshold for this case (for example 5°) is a tuning choice, not a kOS
  constraint.
- **No dwell is needed for correctness.** Both thresholds have hysteresis, and the rate test means the craft has
  settled. A minimum gap between switches (for example 2 s) would only bound the mission-log lines.

### What to log so the thresholds can be measured

- **One line per switch.** UT, on/off, reason, `abs(steeringmanager:angleerror)`, rate (°/s), the wheels-only turn
  estimate and the budget it was compared with, and monopropellant left. This is the spec's one-line-per-switch log,
  with the figures added.
- **A trace like `land trace`.** At most once a second while steering is locked in a wait or turn: angle error, rate,
  `rcs`, and `steeringmanager:actuation:mag`. Actuation over 0.05 means RCS can fire. This is the trace that would
  give real settle figures.
- **The CoastGuard drop line** names whether an engine or RCS tripped it. This is not product code for this ticket.
  The spec should consider it.

## 2. kOS cooked steering and the RCS switch

- **`RCS ON/OFF` sets the KSP action group** directly: `ActionGroups.cs:28`,
  `shared.Vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, (bool) val)`.
- **KSP copies the group to each thruster every physics frame.** In `ModuleRCS.FixedUpdate`:
  `bool flag = vessel.ActionGroups[KSPActionGroup.RCS]; if (flag != rcs_active) rcs_active = flag;`, and thrusters fire
  only `if (rcs_active)`.
- **kOS re-reads RCS torque every physics frame.**
  - `SteeringManager.Update` (called from `OnFlyByWire`) runs `UpdateTorque` every frame while SAS is off.
  - `CorrectedGetPotentialTorque` gives a `ModuleRCS` zero torque when
    `p.ShieldedFromAirstream || !rcs.rcsEnabled || !rcs.isEnabled || rcs.isJustForShow || rcs.flameout || !rcs.rcs_active`.
  - So `lock steering` starts counting RCS torque on the first physics frame after `rcs on`, and stops on the first
    frame after `rcs off`, with at most a frame of lag behind `ModuleRCS`. Nothing has to be re-locked.
- **Torque is not learned.** The only learned correction is `ENABLETORQUEADJUST`, which defaults to false
  (`ResetToDefault`).
  - With it off, `controlTorque` is the summed potential torque of the parts this frame. There is no stale torque
    estimate for a switch to upset.
  - The part list is rebuilt only when the part count changes (`UpdateControlParts`), but each part's torque is
    re-read every frame.
- **The PIDs are not reset by a switch.**
  - Integrators reset only when control is enabled (`EnableControl`), after a gap of more than 1 s between updates,
    while SAS is on, or for roll when the error is over `ROLLCONTROLANGLERANGE`.
  - The torque PI's gains are `Ki = MOI·(4/Ts)²` and `Kp = 2·√(MOI·Ki)` (`TorquePI.Update`). They don't depend on
    torque.
  - Torque enters in two places only:
    - the turn-rate cap: `maxOmega = controlTorque · MAXSTOPPINGTIME / MOI` (`UpdatePredictionPI`);
    - the output clamp, and the mapping `accPitch = tgtPitchTorque / controlTorque.x` (`UpdateControl`).
  - `PIDLoop` unwinds its integral against the clamp (`iTerm = Output - …`), so a smaller cap doesn't leave windup
    behind.
- **The effect of a mid-turn switch.**
  - **On:** the rate cap rises, and a torque request maps to a smaller control input. This is safe.
  - **Off:**
    - The rate cap drops to the wheels-only value.
    - The control input per torque rises. `UpdateControl` limits a rise to 2× the previous magnitude per frame
      (floor 0.005), so it takes a few frames.
    - If the craft is turning faster than the new cap, the wheels alone must brake it, which takes
      (ω − ω_cap)/α_wheels longer than the stopping time kOS planned for. Near the target, that is an overshoot.
    - That is the reason for the rate test in the off rule.
- **The steeringmanager suffixes the helper needs**:
  - `ENABLED` must be true, or `ANGLEERROR` is stale. It is also stale while SAS is on, because `UpdatePredictionPI`
    is skipped then.
  - `ANGLEERROR` is in degrees: the combined pitch and yaw error, with no roll. It can be **negative** (the code
    flips its sign when the target is more than 90° from the craft's top), so take `abs` as `claudia_warp_rate`
    does.
  - `ROLLERROR` is separate. Roll is controlled only within `ROLLCONTROLANGLERANGE` (5°).
  - `MAXSTOPPINGTIME` defaults to 2 s.
  - `TORQUEEPSILONMIN` and `TORQUEEPSILONMAX` default to 0.0002 and 0.001. They are a dead band on the *rate* error
    in rad/s: about 0.01–0.06°/s, so kOS asks for torque on almost any rate error while holding.
  - There is **no RCS-specific setting**.
  - `ACTUATION`, `CONTROLTORQUE`, `RAWTORQUE` and `MOI` are readable. `RAWTORQUE` includes RCS while it is on.
- **Rate readings that are cheap each tick**:
  - `ship:angularvel`: a `Vector` in rad/s, in SHIP-RAW axes. It is the rigidbody's inertial angular velocity, which
    kOS rotates out of KSP's ship frame (`RawAngularVelFromRelative`). Its `:mag` doesn't depend on the frame. This
    is what `claudia_aligned_to` already uses.
  - `steeringmanager:angularvelocity`: the same rate, in rad/s, in the craft's own pitch/yaw/roll axes. It is only
    current while steering is enabled with SAS off.
  - `steeringmanager:angularacceleration` is a frame-to-frame difference, so it is noisy.

## 3. KSP and kOS behaviour the helper must respect

Read from KSP's `ModuleRCS` unless marked kOS.

- **A 5% input dead zone, then proportional thrust.**
  - `ModuleRCS.Update` zeroes each rotation and translation axis whose input is under `EPSILON = 0.05`.
  - Per thruster, thrust is `max(dot(dir, rot), 0) + max(dot(dir, inputLin), 0)`, clamped to 1.
  - `CalculateThrust` also gives nothing below 0.05.
  - So RCS stays quiet while kOS's actuation stays under about 5%, and fires in proportion above that.
  - A part with **full thrust** toggled on (`fullThrust`, from `fullThrustMin` 0.2) fires at 100%. That makes
    bang-bang yo-yo on fine alignment more likely.
- **Wheels don't get priority.** The same control input drives every torque provider, so wheels and RCS respond
  together. Holding with RCS on burns monoprop whenever the actuation is over 5%, even when the wheels could hold
  alone. This is why the rule must switch RCS off rather than rely on wheels going first.
- **Translation fires RCS whatever the rotation rule says.**
  - `ship:control:fore/starboard/top` become `inputLin`.
  - A part with `useThrottle` set also fires forward with the main throttle.
  - Both work only while the RCS group is on (`rcs_active`), so translation must switch it on, as the map's
    `rcs_trim` rule already says.
  - kOS cooked steering never sets translation: `UpdateControl` writes only pitch, yaw and roll.
  - Rotation firing on an off-centre RCS layout still gives some net translation. Each thruster fires along
    whichever of its directions helps the turn.
- **SAS (kOS source).**
  - With the SAS group on, `SteeringManager.Update` skips its own control. It hands the target to stock SAS
    (`Autopilot.SAS.LockRotation`) and resets every integrator each frame.
  - Stock SAS also drives RCS when the group is on. `ANGLEERROR` is not updated then.
  - The mod turns SAS off at run start (`KSP.log`: "SAS turned off … (run start)"), and `claudia_make_safe` turns it
    back on. With RCS off at the end, SAS then holds on wheels alone.
- **No monopropellant.**
  - `CalculateThrust` sets `flameout = !success` when `RequestPropellant` returns nothing. kOS then counts no torque
    for that module (the `rcs.flameout` check).
  - The thrusters give no force (`thrustForces` stays 0), so CoastGuard sees a Coast.
  - The RCS action group still reads on, so the helper has to check monopropellant itself (`ship:resources`, the
    `MonoPropellant` entry) to apply the map's "no monoprop = no RCS" rule.
  - Note that `flameout` updates only while a thruster is asked to fire.
- **Shielded parts.** A part `ShieldedFromAirstream` (in a fairing or cargo bay) gives no thrust unless
  `shieldedCanThrust`. kOS counts it as zero torque.
- **Precision mode (kOS source).** KSP scales RCS thrust by `precisionFactor` (0.1), or by lever distance, when
  precision mode is on. kOS's comment in `CorrectedGetPotentialTorque` says: "Normally you'd check for precision mode
  to nerf powerFactor here, but kOS doesn't obey that". So if the player leaves precision mode on, kOS overestimates
  RCS torque by up to 10×.
- **Warp.**
  - On rails (`TimeWarp.Modes.HIGH`, rate above 1), `ModuleRCS.FixedUpdate` returns before firing.
  - Under physics warp RCS fires normally, and CoastGuard drops the warp on the first firing frame (ADR 0008).
  - So any RCS that is on during a physics-warp Coast risks the warp. This is the #377 failure.
- **`turntime`'s RCS torque.**
  - `TurnCraftReader` reads RCS torque from stock `GetPotentialTorque`, with no propellant check. kOS replaced that
    call for `ModuleRCS` because "the stock call GetPotentialTorque is completely broken in the case of ModuleRCS"
    (`CorrectedGetPotentialTorque`, kOS issue #2814). kOS sums thruster position × thrust per active nozzle.
  - The logged RCS figures (0.15–1.86 kN·m) may therefore not match what kOS steers with.
  - When `turntime` gains its wheels-only and wheels+RCS times, it should compute RCS torque the way kOS does, or at
    least drop flamed-out and shielded modules.
