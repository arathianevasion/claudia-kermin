// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// The Core Library's shared helpers: every Building Block is built on these. The mod bundles this
// file, every block and the Mission Script into one compile unit (Mission Script first, so its line
// numbers stay right), and adds claudia_config() with the player's wait caps. Lines starting "//|"
// are the docs Claudia reads in her prompt; keep them true.
//
// Conventions every block follows (the contract later blocks copy):
// - A block is one global function named as in the spec (exec_node, launch, ...) taking one options
//   lexicon, in its own file kerboscript/library/<block>.ks. Options are snake_case, SI units, with
//   documented defaults returned by <block>_defaults(); an unknown option fails at once with reason
//   "unknown option <name>". Block-only helpers are prefixed <block>_.
// - Every block also takes the common options from claudia_common_defaults(): min_ec_pct,
//   max_duration, abort_on_part_loss and stage_limit.
// - A block returns one result lexicon built by claudia_finish(): the Script Result's common fields
//   plus the block's extra results. It never reports: the Mission Script reports once.
// - An abort makes the craft safe (claudia_make_safe) and returns success false with reason
//   "abort <option>: <detail>"; there is no in-block recovery.
// - Block loops call claudia_tick() each physics tick: it measures delta-v used, auto-stages and
//   checks the common aborts. Waits go through claudia_wait_until().
// - Helpers are prefixed claudia_. Pure helpers (no ship, no time) take plain values so they can be
//   unit-tested on kOS.Safe outside KSP.

@lazyglobal off.

//| Building Blocks: shared rules. Each block is a function taking one options lexicon, e.g.
//| `local res is exec_node(lexicon("max_residual", 0.2)).` Options are snake_case in SI units (m, m/s,
//| s, degrees); leave an option out to get its default. Don't name a variable after a kOS built-in
//| (r, v, q, node, transfer, ...): kOS won't compile the script. An unknown option fails the block at once
//| with reason "unknown option <name>". Every block also takes the common options:
//|   min_ec_pct [10]: abort when electric charge falls below this percent of capacity.
//|   max_duration [per block]: abort when the block has run this many seconds, waits included.
//|   abort_on_part_loss [true]: abort when the craft loses a part other than by the block's own staging.
//|   stage_limit [0]: auto-staging stages on flameout but never below this stage number, and never
//|     fires a stage holding parachutes or heat-shield separation. Out of fuel at the limit fails.
//| A block returns a result lexicon (it does not report). Common fields: success (boolean), reason
//| ("" on success), dv_used (m/s), dv_remaining (m/s, stock estimate), duration (s), body,
//| situation (e.g. "ORBITING"), orbit (lexicon ap, pe in m, inc in degrees) or, when landed,
//| splashed or on the pad, geo (lexicon lat, lng, alt), and stage (current stage number); plus the
//| block's extra results. An abort leaves the craft safe (throttle 0, SAS holding attitude) and
//| sets reason "abort <option>: <detail>". Waits use on-rails warp only, dropping to 1x 30 s before
//| a burn; the player may cancel warp. A wait longer than one orbit (3 h on the ground) fails the
//| block: plan a Due Time instead. Report the Script Result yourself, once, at the end, e.g.
//| `addons:claudia:report(r).`, or a lexicon of your own with at least success and reason.

// ---- Options ----

// The common options every block takes. max_duration is the block's own default.
function claudia_common_defaults {
  parameter max_duration.
  return lexicon("min_ec_pct", 10, "max_duration", max_duration, "abort_on_part_loss", true, "stage_limit", 0).
}

// "" if every key in opts is a known option (a key of defaults), else "unknown option <key>".
function claudia_unknown_option {
  parameter opts, defaults.
  for key in opts:keys {
    if not defaults:haskey(key) {
      return "unknown option " + key.
    }
  }
  return "".
}

// A new lexicon: every default, overridden by opts.
function claudia_merge_options {
  parameter opts, defaults.
  local merged is lexicon().
  for key in defaults:keys {
    set merged[key] to defaults[key].
  }
  for key in opts:keys {
    set merged[key] to opts[key].
  }
  return merged.
}

// A block's options: lexicon("error", "" or "unknown option <key>", "opt", every option with its
// value). block_defaults: the block's own options (from <block>_defaults()); max_duration: the
// block's default for the common option.
function claudia_options {
  parameter opts, block_defaults, max_duration.
  local defaults is claudia_merge_options(block_defaults, claudia_common_defaults(max_duration)).
  return lexicon("error", claudia_unknown_option(opts, defaults), "opt", claudia_merge_options(opts, defaults)).
}

// ---- Aborts ----

// "" or the reason for one of the common aborts. ec_pct: charge left in percent of capacity;
// elapsed: seconds since the block began; parts_lost: parts lost other than by staging.
function claudia_abort_reason {
  parameter opt, ec_pct, elapsed, parts_lost.
  if ec_pct < opt:min_ec_pct {
    return "abort min_ec_pct: electric charge " + round(ec_pct, 1) + "% is below " + opt:min_ec_pct + "%".
  }
  if opt:max_duration:istype("Scalar") and elapsed > opt:max_duration {
    return "abort max_duration: ran " + round(elapsed) + " s, over " + round(opt:max_duration) + " s".
  }
  if opt:abort_on_part_loss and parts_lost > 0 {
    return "abort abort_on_part_loss: " + parts_lost + " part(s) lost".
  }
  return "".
}

// Electric charge left, in percent of capacity; 100 if the craft stores none.
function claudia_ec_pct {
  for res in ship:resources {
    if res:name = "ElectricCharge" and res:capacity > 0 {
      return 100 * res:amount / res:capacity.
    }
  }
  return 100.
}

// Leaves the craft safe: throttle 0, steering released to SAS holding the current attitude.
function claudia_make_safe {
  set ship:control:pilotmainthrottle to 0.
  unlock throttle.
  unlock steering.
  set ship:control:neutralize to true.
  sas on.
}

// ---- Auto-staging ----

// What to do about staging. needs_stage: an engine flamed out or nothing gives thrust;
// has_thrust: some engine still gives thrust; next_unsafe: the next stage holds parachutes or
// heat-shield separation. Returns "none", "stage", "out_of_fuel" (at stage_limit with no thrust
// left) or "blocked" (no thrust left and the next stage is unsafe). At the limit, engines that
// still burn keep the block going.
function claudia_stage_action {
  parameter stage_number, stage_limit, needs_stage, has_thrust, next_unsafe.
  if not needs_stage {
    return "none".
  }
  local at_limit is stage_number <= stage_limit or stage_number <= 0.
  if at_limit or next_unsafe {
    if has_thrust {
      return "none".
    }
    if at_limit {
      return "out_of_fuel".
    }
    return "blocked".
  }
  return "stage".
}

// True if staging into stage n would fire a parachute or separate a heat shield.
function claudia_stage_unsafe {
  parameter n.
  for p in ship:parts {
    if p:stage = n {
      if p:hasmodule("ModuleParachute") or p:hasmodule("RealChuteModule") {
        return true.
      }
      if claudia_is_decoupler(p) {
        if p:hasmodule("ModuleAblator") {
          return true.
        }
        if p:hasparent and p:parent:hasmodule("ModuleAblator") {
          return true.
        }
        for child in p:children {
          if child:hasmodule("ModuleAblator") {
            return true.
          }
        }
      }
    }
  }
  return false.
}

function claudia_is_decoupler {
  parameter p.
  return p:hasmodule("ModuleDecouple") or p:hasmodule("ModuleAnchoredDecoupler") or p:hasmodule("ModuleJettison").
}

// True if an ignited engine has flamed out or no engine gives thrust.
function claudia_needs_stage {
  if ship:availablethrust <= 0 {
    return true.
  }
  local engs is list().
  list engines in engs.
  for e in engs {
    if e:ignition and e:flameout {
      return true.
    }
  }
  return false.
}

// Stages if an engine flamed out (or nothing gives thrust), down to stage_limit.
// Returns "" or the reason the block must fail.
function claudia_autostage {
  parameter ctx.
  if not stage:ready {
    return "".
  }
  local action is claudia_stage_action(stage:number, ctx:opt:stage_limit, claudia_needs_stage(), ship:availablethrust > 0, claudia_stage_unsafe(stage:number - 1)).
  if action = "out_of_fuel" {
    return "out of fuel at stage limit " + ctx:opt:stage_limit.
  }
  if action = "blocked" {
    return "out of fuel: the next stage would fire parachutes or separate a heat shield".
  }
  if action = "stage" {
    stage.
    // Let decoupled parts leave before counting what the craft should still have.
    wait 0.5.
    set ctx:parts to ship:parts:length.
    // Thrust during the half second above isn't known, so don't count it as delta-v used.
    set ctx:last_ut to time:seconds.
  }
  return "".
}

// ---- Waits and warp ----

// The longest wait a block may sit through: orbits_cap orbits, or ground_cap_s on the ground (or
// when there is no closed orbit, period <= 0).
function claudia_wait_cap {
  parameter on_ground, period, orbits_cap, ground_cap_s.
  if on_ground or period <= 0 {
    return ground_cap_s.
  }
  return period * orbits_cap.
}

function claudia_on_ground {
  return status = "LANDED" or status = "SPLASHED" or status = "PRELAUNCH".
}

// "" if a wait of wait_s seconds is allowed now, else the reason the block fails.
function claudia_check_wait {
  parameter wait_s.
  local caps is claudia_config().
  local period is 0.
  if ship:orbit:eccentricity < 1 {
    set period to ship:orbit:period.
  }
  local cap is claudia_wait_cap(claudia_on_ground(), period, caps:wait_cap_orbits, caps:wait_cap_ground_s).
  if wait_s > cap {
    return "wait of " + round(wait_s) + " s exceeds the cap of " + round(cap) + " s: plan a Due Time instead".
  }
  return "".
}

// Waits until ut on rails warp, dropping to 1x 30 s before it. Warp is asked for once, so a player
// who cancels it is never overridden. Keeps ticking, so the common aborts still apply.
// Returns "" or an abort reason.
function claudia_wait_until {
  parameter ctx, ut.
  local warp_end is ut - 30.
  if warp_end > time:seconds + 5 {
    set kuniverse:timewarp:mode to "RAILS".
    kuniverse:timewarp:warpto(warp_end).
  }
  until time:seconds >= ut {
    if time:seconds >= warp_end and kuniverse:timewarp:rate > 1 {
      kuniverse:timewarp:cancelwarp().
    }
    local reason is claudia_tick(ctx, 0).
    if reason <> "" {
      kuniverse:timewarp:cancelwarp().
      return reason.
    }
    wait 0.
  }
  wait until kuniverse:timewarp:issettled and ship:unpacked.
  return "".
}

// ---- Burns ----

// Seconds to burn dv (m/s) from mass (t) at thrust (kN) and isp (s); -1 with no thrust.
function claudia_burn_time {
  parameter dv, mass_t, thrust_kn, isp.
  if thrust_kn <= 0 or isp <= 0 {
    return -1.
  }
  // Literals rather than CONSTANT:G0 and CONSTANT:E, so this stays pure (KSP's g0 is 9.80665).
  local ve is isp * 9.80665.
  return mass_t * ve / thrust_kn * (1 - 2.718281828459045 ^ (-dv / ve)).
}

// The combined isp of engines burning together, from lists of their thrusts and isps.
function claudia_mean_isp {
  parameter thrusts, isps.
  local total is 0.
  local flow is 0.
  for i in range(thrusts:length) {
    if isps[i] > 0 {
      set total to total + thrusts[i].
      set flow to flow + thrusts[i] / isps[i].
    }
  }
  if flow <= 0 {
    return 0.
  }
  return total / flow.
}

// The isp of the engines burning now (ignited, with thrust available).
function claudia_engine_isp {
  local thrusts is list().
  local isps is list().
  local engs is list().
  list engines in engs.
  for e in engs {
    if e:ignition and e:availablethrust > 0 {
      thrusts:add(e:availablethrust).
      isps:add(e:isp).
    }
  }
  return claudia_mean_isp(thrusts, isps).
}

// Throttle for dv_left (m/s) at full acceleration max_accel (m/s^2): full until the last second
// of burn, then easing off (never below 0.5%); 0 while pointing more than 10 degrees off.
function claudia_burn_throttle {
  parameter dv_left, max_accel, angle_off.
  if angle_off > 10 {
    return 0.
  }
  if max_accel <= 0 {
    return 1.
  }
  return min(1, max(0.005, dv_left / max_accel)).
}

// ---- Block lifecycle ----

// Starts a block: checks and merges its options and notes where it started. Returns the block's
// context; ctx:error is "" or why the block must fail at once. block_defaults are the block's own
// options; max_duration is its default for the common option.
function claudia_begin {
  parameter block, opts, block_defaults, max_duration.
  local checked is claudia_options(opts, block_defaults, max_duration).
  return lexicon(
    "block", block,
    "error", checked:error,
    "opt", checked:opt,
    "start_ut", time:seconds,
    "last_ut", time:seconds,
    "dv_used", 0,
    "parts", ship:parts:length).
}

// Once per physics tick in a block's loops. tset: the throttle the block is commanding now.
// Adds up delta-v used, auto-stages while burning and checks the common aborts.
// Returns "" or the reason the block must fail.
function claudia_tick {
  parameter ctx, tset.
  local now is time:seconds.
  if tset > 0 and ship:mass > 0 {
    set ctx:dv_used to ctx:dv_used + tset * ship:availablethrust / ship:mass * (now - ctx:last_ut).
    local staging is claudia_autostage(ctx).
    if staging <> "" {
      return staging.
    }
  }
  // Auto-staging may already have moved last_ut past now.
  set ctx:last_ut to max(now, ctx:last_ut).
  return claudia_abort_reason(ctx:opt, claudia_ec_pct(), now - ctx:start_ut, max(0, ctx:parts - ship:parts:length)).
}

// After a block calls another block (e.g. exec_node for a burn): folds the inner result's delta-v
// into ctx and takes the craft as it now is, so the inner block's staging isn't counted as part loss.
function claudia_add_result {
  parameter ctx, inner.
  set ctx:dv_used to ctx:dv_used + inner:dv_used.
  set ctx:parts to ship:parts:length.
  set ctx:last_ut to time:seconds.
}

// The block's result lexicon: the Script Result's common fields plus extra (the block's own).
function claudia_finish {
  parameter ctx, success, reason, extra.
  local result is lexicon(
    "success", success,
    "reason", reason,
    "dv_used", round(ctx:dv_used, 1),
    "dv_remaining", round(ship:deltav:current, 1),
    "duration", round(time:seconds - ctx:start_ut, 1),
    "body", ship:body:name,
    "situation", status,
    "stage", stage:number).
  if claudia_on_ground() {
    set result["geo"] to lexicon("lat", round(ship:geoposition:lat, 4), "lng", round(ship:geoposition:lng, 4), "alt", round(ship:altitude)).
  } else {
    set result["orbit"] to lexicon("ap", round(ship:apoapsis), "pe", round(ship:periapsis), "inc", round(ship:orbit:inclination, 2)).
  }
  for key in extra:keys {
    set result[key] to extra[key].
  }
  return result.
}
