// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// The Core Library's shared helpers: every Building Block is built on these. The mod bundles this
// file, every block and the Mission Script into one compile unit (Mission Script first, so its line
// numbers stay right). Lines starting "//|" are the docs Claudia reads in her prompt; keep them true.
//
// Conventions every block follows (the contract later blocks copy):
// - A block is one global function named as in the spec (exec_node, launch, transfer_to, ...; never a kOS built-in such as transfer) taking one options
//   lexicon, in its own file kerboscript/library/<block>.ks. Options are snake_case, SI units, with
//   documented defaults returned by <block>_defaults(); an unknown option fails at once with reason
//   "unknown option <name>". Block-only helpers are prefixed <block>_.
// - Every block also takes the common options from claudia_common_defaults(): min_ec_pct,
//   max_duration, abort_on_part_loss, stage_limit and drop_empty.
// - A block returns one result lexicon built by claudia_finish(): the Script Result's common fields
//   plus the block's extra results. It never reports: the Mission Script reports once.
// - claudia_begin and claudia_finish tell the mod a block began and what it returned
//   (ADDONS:CLAUDIA:BLOCKSTART, BLOCKRESULT), for the Capcom strip's progress and the log.
// - An abort makes the craft safe (claudia_make_safe) and returns success false with reason
//   "abort <option>: <detail>"; there is no in-block recovery.
// - Block loops call claudia_tick() each physics tick: it measures delta-v used, auto-stages and
//   checks the common aborts. Waits go through claudia_wait_until() (or claudia_warp_to() for a
//   longer lead), which warp on rails with no cap.
// - A block that flies another block (e.g. a burn through exec_node) passes on its common options
//   and folds the result into its own with claudia_add_result (claudia_fly_node does both for nodes).
// - Helpers are prefixed claudia_. Pure helpers (no ship, no time) take plain values so they can be
//   unit-tested on kOS.Safe outside KSP.

@lazyglobal off.

//| Building Blocks: shared rules. Each block is a function taking one options lexicon, e.g.
//| `local res is exec_node(lexicon("max_residual", 0.2)).` Options are snake_case in SI units (m, m/s,
//| s, degrees); leave an option out to get its default. Don't name a variable after a kOS built-in
//| (r, v, q, node, transfer, ...): kOS won't compile the script. An unknown option fails the block at once
//| with reason "unknown option <name>". Every block also takes the common options:
//|   drop_empty [empty list]: part names or tags (e.g. list("fuelTank", "side tanks")); the auto-staging fires the
//|     decoupler that holds them once every tank it would drop is empty (fuel, oxidizer, solid fuel, mono and
//|     xenon all count), in the very burn that empties them, and never one that still holds fuel, is below
//|     stage_limit or holds the part running the script. Only burning blocks use it (launch, ascend, land,
//|     exec_node, circularize and the blocks that fly them); the result then lists dropped.
//|   min_ec_pct [10]: abort when electric charge falls below this percent of capacity.
//|   max_duration [per block]: abort when the block has run this many seconds, waits included.
//|   abort_on_part_loss [true]: abort when the craft loses a part other than by the block's own staging.
//|   stage_limit [0]: auto-staging stages on flameout but never below this stage number, and never
//|     fires a stage holding parachutes or heat-shield separation. Out of fuel at the limit fails.
//| A block returns a result lexicon (it does not report). Common fields: success (boolean), reason
//| ("" on success), dv_used (m/s), dv_remaining (m/s, stock estimate; -1 and a dv_remaining_note at a trickle throttle), duration (s), body,
//| situation (e.g. "ORBITING"), orbit (lexicon ap, pe in m, inc in degrees) or, when landed,
//| splashed or on the pad, geo (lexicon lat, lng, alt), and stage (current stage number); plus the
//| block's extra results. An abort leaves the craft safe (throttle 0, SAS holding attitude) and
//| sets reason "abort <option>: <detail>". Waits use on-rails warp only, back at 1x at least 30 s before
//| a burn, and have no cap: a block waits for its window or daylight however long, and coast holds
//| until a UT. Warp drops to 1x when the Mission needs the player and resumes after; the player may
//| cancel it. Don't warp yourself (physics warp is blocked). Report the Script Result yourself, once, at the end, e.g.
//| `addons:claudia:report(res).`, or a lexicon of your own with at least success and reason.

// ---- Options ----

// The common options every block takes. max_duration is the block's own default.
function claudia_common_defaults {
  parameter max_duration.
  return lexicon("min_ec_pct", 10, "max_duration", max_duration, "abort_on_part_loss", true, "stage_limit", 0, "drop_empty", list()).
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
  local merged is claudia_merge_options(opts, defaults).
  local error is claudia_unknown_option(opts, defaults).
  // One name or tag may be given bare; the rest of the library sees a list.
  if merged:drop_empty:istype("String") {
    set merged:drop_empty to list(merged:drop_empty).
  }
  if error = "" and not merged:drop_empty:istype("List") {
    set error to "drop_empty must be a list of part names or tags".
  }
  return lexicon("error", error, "opt", merged).
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

// True if a stage fires straight on into the next one. fired_engines: the stage just fired held
// engines; next_engines: the next stage holds engines; had_thrust: some engine gave thrust before
// firing; next_unsafe: as for claudia_stage_action. A decoupler-only stage with no thrust left
// would otherwise leave the craft coasting for a tick or more before its engines light.
function claudia_stage_on {
  parameter stage_number, stage_limit, fired_engines, next_engines, had_thrust, next_unsafe.
  if fired_engines or had_thrust or next_unsafe or not next_engines {
    return false.
  }
  return stage_number > stage_limit and stage_number > 0.
}

// True if a stage fires straight on through a run of decoupler-only stages into engines.
// stage_number: the number of the stage just fired; engines_below / unsafe_below: lists from the
// next stage to fire downwards, each entry true if that stage holds engines / parachutes or a heat
// shield. Fires on only if no stage in the run is unsafe, the run reaches engines before the stage
// limit or stage 0, and neither the stage just fired nor any engine already gives thrust.
function claudia_stage_chain {
  parameter stage_number, stage_limit, fired_engines, had_thrust, engines_below, unsafe_below.
  if fired_engines or had_thrust {
    return false.
  }
  local step is 0.
  until step >= engines_below:length {
    if stage_number - step <= stage_limit or stage_number - step <= 0 {
      return false.
    }
    if unsafe_below[step] {
      return false.
    }
    if engines_below[step] {
      return true.
    }
    set step to step + 1.
  }
  return false.
}

// claudia_stage_chain over the live craft, from the stage after fired down to the stage limit.
function claudia_stage_run_on {
  parameter fired, stage_limit, fired_engines, had_thrust.
  local engines_below is list().
  local unsafe_below is list().
  local n is fired - 1.
  until n < 0 or n < stage_limit {
    engines_below:add(claudia_stage_has_engine(n)).
    unsafe_below:add(claudia_stage_unsafe(n)).
    set n to n - 1.
  }
  return claudia_stage_chain(fired, stage_limit, fired_engines, had_thrust, engines_below, unsafe_below).
}

// True if stage n holds an engine.
function claudia_stage_has_engine {
  parameter n.
  local engs is list().
  list engines in engs.
  for e in engs {
    if e:stage = n {
      return true.
    }
  }
  return false.
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
  claudia_drop_empty(ctx).
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
    local had_thrust is ship:availablethrust > 0.
    local fired is stage:number - 1.
    local fired_engines is claudia_stage_has_engine(fired).
    stage.
    // Decoupler-only stages that leave no thrust: stage on through the whole run into the engines at once.
    until not claudia_stage_run_on(fired, ctx:opt:stage_limit, fired_engines, had_thrust) {
      wait until stage:ready.
      stage.
      set fired to fired - 1.
      set fired_engines to claudia_stage_has_engine(fired).
    }
    // Let decoupled parts leave before counting what the craft should still have.
    wait 0.5.
    set ctx:parts to ship:parts:length.
    // Thrust during the half second above isn't known, so don't count it as delta-v used.
    set ctx:last_ut to time:seconds.
  }
  return "".
}

// ---- Dropping parts (the drop_empty option and the drop_parts block) ----

// True if a part, by its name or its tag, is one of wanted (names or tags, any case).
function claudia_drop_matches {
  parameter part_name, part_tag, wanted.
  for w in wanted {
    local want is (w + ""):tolower.
    if want <> "" and (want = part_name:tolower or want = part_tag:tolower) {
      return true.
    }
  }
  return false.
}

// True if every tank is empty: amounts and capacities are parallel lists, one entry per tank. A set
// with no tank is not empty, so nothing is dropped for holding no fuel.
function claudia_tanks_empty {
  parameter amounts, capacities.
  if amounts:length = 0 {
    return false.
  }
  local i is 0.
  until i >= amounts:length {
    if amounts[i] > max(0.01, 0.001 * capacities[i]) {
      return false.
    }
    set i to i + 1.
  }
  return true.
}

// True if a resource is a tank's contents (not charge, intake air or ablator).
function claudia_is_propellant {
  parameter res_name.
  return res_name <> "ElectricCharge" and res_name <> "IntakeAir" and res_name <> "Ablator".
}

// The part that separates p from the craft, or "" if it has none.
function claudia_decoupler_of {
  parameter p.
  local d is p:decoupler.
  if d:istype("Part") {
    return d.
  }
  return "".
}

// Every part that leaves with decoupler d (the parts whose decoupler it is).
function claudia_dropped_with {
  parameter d.
  local found is list().
  for p in ship:parts {
    local other is claudia_decoupler_of(p).
    if not other:istype("String") and other:uid = d:uid {
      found:add(p).
    }
  }
  return found.
}

// True if a set of parts holds the part running the script or the root: dropping it ends the flight.
function claudia_drop_holds_core {
  parameter gone.
  for p in gone {
    if p:uid = core:part:uid or p:uid = ship:rootpart:uid {
      return true.
    }
  }
  return false.
}

// The event, of a decoupler module's event names, that drops what it holds; "" if it has none. kOS
// names an event by its menu text, which a part may rename (stock HeatShield1's ModuleDecouple lists
// "jettison heat shield", not "decouple"), so the known names come first, then any that says it.
function claudia_decoupler_event {
  parameter names.
  for wanted in list("decouple", "jettison heat shield", "jettison") {
    for name in names {
      if name:tolower = wanted {
        return name.
      }
    }
  }
  for name in names {
    if name:tolower:contains("decouple") or name:tolower:contains("jettison") {
      return name.
    }
  }
  return "".
}

// Fires decoupler d. Returns true if one of its modules had an event to fire.
function claudia_fire_decoupler {
  parameter d.
  for module_name in list("ModuleDecouple", "ModuleAnchoredDecoupler", "ModuleJettison") {
    if d:hasmodule(module_name) {
      local m is d:getmodule(module_name).
      local event is claudia_decoupler_event(m:alleventnames).
      if event <> "" {
        m:doevent(event).
        return true.
      }
    }
  }
  return false.
}

// Adds the parts an inner block's result says it dropped to ctx's.
function claudia_note_dropped {
  parameter ctx, inner.
  if inner:haskey("dropped") {
    for n in inner:dropped {
      ctx:dropped:add(n).
    }
  }
}

// Fires the decoupler of every part in drop_empty whose tanks are all empty, together with every other
// part leaving with it, and notes the names of the parts that left in ctx:dropped. Looks twice a second.
// A decoupler is tried once (ctx:drop_tried), and never one below stage_limit.
function claudia_drop_empty {
  parameter ctx.
  local wanted is ctx:opt:drop_empty.
  if wanted:length = 0 or time:seconds < ctx:drop_ut + 0.5 {
    return.
  }
  set ctx:drop_ut to time:seconds.
  local fired is list().
  local fired_parts is list().
  for p in ship:parts {
    if claudia_drop_matches(p:name, p:tag, wanted) {
      local d is claudia_decoupler_of(p).
      if not d:istype("String") and not ctx:drop_tried:contains(d:uid) and d:stage >= ctx:opt:stage_limit {
        local gone is claudia_dropped_with(d).
        local amounts is list().
        local capacities is list().
        for g in gone {
          for res in g:resources {
            if claudia_is_propellant(res:name) and res:capacity > 0 {
              amounts:add(res:amount).
              capacities:add(res:capacity).
            }
          }
        }
        // Nested, not one "and": firing is a side effect and must only follow the guards.
        if claudia_tanks_empty(amounts, capacities) {
          if not claudia_drop_holds_core(gone) {
            ctx:drop_tried:add(d:uid).
            if claudia_fire_decoupler(d) {
              fired:add(d:uid).
              for g in gone {
                fired_parts:add(g).
              }
            }
          }
        }
      }
    }
  }
  if fired:length > 0 {
    // Let the dropped parts leave before counting what the craft should still have.
    wait 0.5.
    local left is list().
    for p in ship:parts {
      left:add(p:uid).
    }
    for g in fired_parts {
      if not left:contains(g:uid) {
        ctx:dropped:add(g:name).
      }
    }
    set ctx:parts to ship:parts:length.
    set ctx:last_ut to time:seconds.
  }
}

// ---- Waits and warp ----

// A wait has no cap (ADR 0004): it warps on rails inside the sequence, however long, and the
// player's Approval of the sequence approved it.

function claudia_on_ground {
  return status = "LANDED" or status = "SPLASHED" or status = "PRELAUNCH".
}

// The UT warp must be over by for a wait ending at ut: lead seconds before it, never less than 30,
// so whatever comes at ut (a burn) starts at 1x.
function claudia_warp_end {
  parameter ut, lead.
  return ut - max(30, lead).
}

// What a wait does this tick; pure, so it is tested off KSP. now: UT; warp_end: from
// claudia_warp_end; rate: the warp rate now; held: the mod holds warp at 1x because the Mission
// needs the player (ADDONS:CLAUDIA:WARPHOLD); want: warp is still to be asked for. Returns "drop"
// (cancel warp), "warp" (ask for warp to warp_end) or "none". Warp under 5 s from its end isn't
// worth asking for.
function claudia_warp_step {
  parameter now, warp_end, rate, held, want.
  if held or now >= warp_end {
    if rate > 1 {
      return "drop".
    }
    return "none".
  }
  if want and warp_end > now + 5 {
    return "warp".
  }
  return "none".
}

// Waits until ut, warping on rails and back at 1x lead seconds before it (at least 30). Warp is
// asked for once, so a player who cancels it is never overridden; but once the mod has held warp at
// 1x (a Pause, link loss, the Spending Cap, a stock alarm) it is asked for again when the hold ends.
// Physics warp is never used: the mode is set to rails first (and the runtime gates refuse physics
// warp anyway). Keeps ticking, so the common aborts still apply. Returns "" or an abort reason.
function claudia_warp_to {
  parameter ctx, ut, lead.
  local warp_end is claudia_warp_end(ut, lead).
  local want is true.
  until time:seconds >= ut {
    local held is addons:claudia:warphold.
    local action is claudia_warp_step(time:seconds, warp_end, kuniverse:timewarp:rate, held, want).
    if action = "drop" {
      kuniverse:timewarp:cancelwarp().
    } else if action = "warp" {
      set kuniverse:timewarp:mode to "RAILS".
      kuniverse:timewarp:warpto(warp_end).
      set want to false.
    }
    if held {
      set want to true.
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

// claudia_warp_to with the 30 s lead every burn needs.
function claudia_wait_until {
  parameter ctx, ut.
  return claudia_warp_to(ctx, ut, 30).
}

// ---- Orbits ----

// Seconds until an orbit (ecc, sma in m, true anomaly ta in degrees) next falls to radius r_fall from
// above, around a body of gravitational parameter mu: an atmosphere's edge, or the ground. 0 if it
// is at or under r_fall now; -1 if the orbit never gets that low. Pure.
function claudia_fall_eta {
  parameter ecc, sma, mu, r_fall, ta.
  if ecc >= 1 or sma <= 0 {
    return -1.
  }
  local semi_latus is sma * (1 - ecc * ecc).
  local r_now is semi_latus / (1 + ecc * cos(ta)).
  if r_now <= r_fall {
    return 0.
  }
  if ecc <= 0 {
    return -1.
  }
  local cos_fall is (semi_latus / r_fall - 1) / ecc.
  if cos_fall > 1 {
    return -1.
  }
  // On the way down the true anomaly is the negative of the one on the way up.
  local ta_fall is 360 - arccos(max(-1, cos_fall)).
  local m_fall is claudia_mean_anomaly(ecc, ta_fall).
  local m_now is claudia_mean_anomaly(ecc, ta).
  local ahead is mod(mod(m_fall - m_now, 360) + 360, 360).
  return ahead / 360 * 2 * 3.141592653589793 * sqrt(sma ^ 3 / mu).
}

// The mean anomaly in degrees for a true anomaly ta in degrees. Pure.
function claudia_mean_anomaly {
  parameter ecc, ta.
  local ecc_anomaly is 2 * arctan2(sqrt(1 - ecc) * sin(ta / 2), sqrt(1 + ecc) * cos(ta / 2)).
  return ecc_anomaly - ecc * sin(ecc_anomaly) * 57.29577951308232.
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
  // For the Capcom strip's progress: the mod shows the block running, with no Claude call.
  addons:claudia:blockstart(block).
  return lexicon(
    "block", block,
    "error", checked:error,
    "opt", checked:opt,
    "start_ut", time:seconds,
    "last_ut", time:seconds,
    "dv_used", 0,
    "parts", ship:parts:length,
    "dropped", list(),
    "drop_tried", list(),
    "drop_ut", 0).
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
  claudia_note_dropped(ctx, inner).
  set ctx:parts to ship:parts:length.
  set ctx:last_ut to time:seconds.
}

// Flies a maneuver node the calling block added, with exec_node and the caller's common options,
// folding its delta-v into ctx. On failure removes the node (exec_node leaves it in place).
// Returns "" or why it failed.
function claudia_fly_node {
  parameter ctx, nd.
  local opt is ctx:opt.
  local inner is exec_node(lexicon("node", nd, "min_ec_pct", opt:min_ec_pct, "abort_on_part_loss", opt:abort_on_part_loss, "stage_limit", opt:stage_limit, "drop_empty", opt:drop_empty)).
  claudia_add_result(ctx, inner).
  if not inner:success {
    remove nd.
    return inner:reason.
  }
  return "".
}

// Stock delta-v reads 0 while an engine runs at a trickle of throttle (under 5%), so it can be trusted
// only at throttle 0 or from 5% up.
function claudia_dv_trusted {
  parameter throttle_now.
  return throttle_now <= 0 or throttle_now >= 0.05.
}

// The stock delta-v left (m/s), or -1 (unknown) at a trickle throttle.
function claudia_dv_remaining {
  if not claudia_dv_trusted(ship:control:mainthrottle) {
    return -1.
  }
  return round(ship:deltav:current, 1).
}

// The block's result lexicon: the Script Result's common fields plus extra (the block's own).
function claudia_finish {
  parameter ctx, success, reason, extra.
  local result is lexicon(
    "success", success,
    "reason", reason,
    "dv_used", round(ctx:dv_used, 1),
    "dv_remaining", claudia_dv_remaining(),
    "duration", round(time:seconds - ctx:start_ut, 1),
    "body", ship:body:name,
    "situation", status,
    "stage", stage:number).
  if claudia_on_ground() {
    set result["geo"] to lexicon("lat", round(ship:geoposition:lat, 4), "lng", round(ship:geoposition:lng, 4), "alt", round(ship:altitude)).
  } else {
    set result["orbit"] to lexicon("ap", round(ship:apoapsis), "pe", round(ship:periapsis), "inc", round(ship:orbit:inclination, 2)).
  }
  if not claudia_dv_trusted(ship:control:mainthrottle) {
    set result["dv_remaining_note"] to "unknown: stock delta-v reads 0 while the engines run at a trickle throttle".
  }
  if ctx:dropped:length > 0 {
    set result["dropped"] to ctx:dropped.
  }
  for key in extra:keys {
    set result[key] to extra[key].
  }
  // One log row per block result; this is not the Script Result, which the Mission Script reports once.
  addons:claudia:blockresult(ctx:block, result).
  return result.
}

// ---- Flying: shared by launch, ascend, land and exec_node ----

// Degrees wrapped into [0, 360).
function claudia_wrap_angle {
  parameter deg.
  return deg - 360 * floor(deg / 360).
}

// A craft whose engines aren't lit yet stages into them (within stage_limit). Returns "" once some
// engine gives thrust, else why not: the staging reason, or "no thrust".
function claudia_stage_into_engines {
  parameter ctx.
  if ship:availablethrust <= 0 {
    local staged is claudia_autostage(ctx).
    if staged <> "" {
      return staged.
    }
  }
  if ship:availablethrust <= 0 {
    return "no thrust".
  }
  return "".
}

// The tail of a burn that succeeded: throttle to 0 and the throttle and steering locks released.
function claudia_release_controls {
  set ship:control:pilotmainthrottle to 0.
  unlock throttle.
  unlock steering.
}

// ---- Transfers: shared by transfer_to and return ----

// "" or why a target_pe value isn't metres above 0.
function claudia_pe_unreadable {
  parameter target_pe.
  if not target_pe:istype("Scalar") or target_pe <= 0 {
    return "target_pe must be metres above 0, not " + target_pe.
  }
  return "".
}

// "" or why a window value isn't "next" or a time (UT seconds).
function claudia_window_unreadable {
  parameter window.
  if window:istype("String") {
    if window <> "next" {
      return "window must be 'next' or a time (UT seconds), not '" + window + "'".
    }
  } else if not window:istype("Scalar") {
    return "window must be 'next' or a time (UT seconds), not '" + window + "'".
  }
  return "".
}

// "" or why a correction value isn't true or false.
function claudia_correction_unreadable {
  parameter correction.
  if not correction:istype("Boolean") {
    return "correction must be true or false".
  }
  return "".
}

// The launch pad at KSC, on Kerbin.
function claudia_ksc {
  return lexicon("lat", -0.0972, "lng", -74.5577).
}

// "" or why a landing target isn't one a block can use: LATLNG(lat, lng), lexicon("lat", x, "lng", y)
// or "ksc". The string "required" is a block's default for a target the script must give.
function claudia_target_unreadable {
  parameter t.
  local target_rule is "target must be LATLNG(lat, lng), lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y) or " + char(34) + "ksc" + char(34) + ", not ".
  if t:istype("String") {
    if t = "required" {
      return "target is required".
    }
    if t <> "ksc" {
      return target_rule + "'" + t + "'".
    }
  } else if t:istype("Lexicon") {
    if not (t:haskey("lat") and t:haskey("lng")) {
      return target_rule + "a lexicon without lat and lng".
    }
    if not (t:lat:istype("Scalar") and t:lng:istype("Scalar")) {
      return target_rule + "a lexicon whose lat and lng aren't numbers".
    }
    if abs(t:lat) > 90 {
      return "target's lat must be from -90 to 90, not " + t:lat.
    }
  } else if not t:istype("GeoCoordinates") {
    return target_rule + "a " + t:typename.
  }
  return "".
}

// A readable target (claudia_target_unreadable) as lexicon("error", "" or why not, "lat", deg,
// "lng", deg) on the body named body_name.
function claudia_target_site {
  parameter target_opt, body_name.
  local site is lexicon("error", "", "lat", 0, "lng", 0).
  if target_opt:istype("String") {
    if body_name <> "Kerbin" {
      set site:error to "ksc is on Kerbin, and the craft orbits " + body_name + ": give a LATLNG there".
      return site.
    }
    local ksc is claudia_ksc().
    set site:lat to ksc:lat.
    set site:lng to ksc:lng.
  } else {
    set site:lat to target_opt:lat.
    set site:lng to target_opt:lng.
  }
  return site.
}

// A predicted landing (a block call's result with lat and lng) as a block reports it.
function claudia_geo {
  parameter landing.
  return lexicon("lat", round(landing:lat, 4), "lng", round(landing:lng, 4)).
}

// The earliest departure (UT): 5 minutes from now, time to plan, turn and lead the burn, or window
// if that is a later UT.
function claudia_earliest {
  parameter now, window.
  local earliest is now + 300.
  if window:istype("Scalar") {
    set earliest to max(earliest, window).
  }
  return earliest.
}

// Coordinate descent: lowers cost(x) over the list of numbers x, one coordinate at a time, starting
// with steps (one per coordinate) and stepping on while that helps; when no step helps, every step
// halves, up to rounds times. Stops early once the cost is at most good, or after 400 tries.
// Returns lexicon("x", the best list, "cost", its cost).
function claudia_descend {
  parameter cost, x, steps, rounds, good.
  local best is x:copy.
  local best_cost is cost(best).
  local step is steps:copy.
  local tries is 1.
  local halvings is 0.
  until halvings >= rounds or best_cost <= good or tries >= 400 {
    local moved is false.
    for i in range(best:length) {
      for sign in list(1, -1) {
        local going is true.
        until not going or best_cost <= good or tries >= 400 {
          local trial is best:copy.
          set trial[i] to best[i] + sign * step[i].
          local trial_cost is cost(trial).
          set tries to tries + 1.
          if trial_cost < best_cost {
            set best to trial.
            set best_cost to trial_cost.
            set moved to true.
          } else {
            set going to false.
          }
        }
      }
    }
    if not moved {
      for i in range(step:length) {
        set step[i] to step[i] / 2.
      }
      set halvings to halvings + 1.
    }
  }
  return lexicon("x", best, "cost", best_cost).
}

// RSVP's lexicon of functions, loaded on first use: a bundled file's top-level statements would
// only run after the Mission Script ends, so RSVP's main is a function called here.
function claudia_rsvp {
  if not (defined rsvp) {
    rsvp_file_main().
  }
  return rsvp.
}

// The first patch of orb's path (orb itself or a later one) around dest:
// lexicon("orbit", that patch, "soi_ut", UT it begins), or "none" if the path never gets there.
function claudia_patch {
  parameter orb, dest.
  local o is orb.
  local start_ut is time:seconds.
  until o:body = dest {
    if not o:hasnextpatch {
      return "none".
    }
    set start_ut to time:seconds + o:nextpatcheta.
    set o to o:nextpatch.
  }
  return lexicon("orbit", o, "soi_ut", start_ut).
}

// m the periapsis at dest on orb's path is above target_pe (negative: below); 1e12 with no encounter.
function claudia_pe_error {
  parameter orb, dest, target_pe.
  local p is claudia_patch(orb, dest).
  if p:istype("String") {
    return 1e12.
  }
  return p:orbit:periapsis - target_pe.
}

// Options for a burn the block flies with exec_node: its own common options, and what is left of
// max_duration.
function claudia_burn_options {
  parameter ctx, nd.
  local opt is ctx:opt.
  local burn is lexicon("node", nd, "min_ec_pct", opt:min_ec_pct, "abort_on_part_loss", opt:abort_on_part_loss, "stage_limit", opt:stage_limit, "drop_empty", opt:drop_empty).
  if opt:max_duration:istype("Scalar") {
    set burn:max_duration to max(1, opt:max_duration - (time:seconds - ctx:start_ut)).
  }
  return burn.
}

// Flies nd with exec_node, counting its delta-v as the block's. Returns exec_node's result.
function claudia_burn {
  parameter ctx, nd.
  local burned is exec_node(claudia_burn_options(ctx, nd)).
  set ctx:dv_used to ctx:dv_used + burned:dv_used.
  claudia_note_dropped(ctx, burned).
  // exec_node's staging dropped parts on purpose; don't count them as lost.
  set ctx:parts to ship:parts:length.
  set ctx:last_ut to time:seconds.
  return burned.
}

// km the predicted landing may be from the target before the aimed correction stops looking.
function claudia_aim_good_km {
  return 2.
}

// What an aimed correction (claudia_correct with aim) lowers for the path orb: the km from the
// target to where it lands; over any real landing (Kerbin is under 2000 km round) when it misses
// the atmosphere, so the search first moves towards the air by target_pe, or 1e5 with no path to dest.
// p is claudia_patch's result for the path.
function claudia_aim_cost {
  parameter aim, p, target_pe.
  if p:istype("String") {
    return 1e5.
  }
  local km is aim(p:orbit, p:soi_ut).
  if km < 0 {
    return 1e4 + abs(p:orbit:periapsis - target_pe) / 1000.
  }
  return km.
}

// The mid-course correction: a node halfway between now and arrive_ut, aimed at target_pe at dest,
// flown with exec_node. A sphere-of-influence change before halfway (leaving a moon on the way home)
// is waited out first (on rails, however long), so the node is placed on the orbit it will be on. Nothing is burned
// when the prediction is already within 1 km of target_pe or the fix is under 0.1 m/s.
// aim, when given (a delegate of an orbit patch round dest and the UT it begins, that returns the km
// its predicted landing is from the target, or -1 if it doesn't come down), makes the correction aim the landing at the
// target instead: target_pe then only draws a path that misses the atmosphere towards it, and
// nothing is burned when the landing is already within claudia_aim_good_km() of the target.
// Returns lexicon("reason", "" or why the block fails, "dv", m/s burned).
function claudia_correct {
  parameter ctx, dest, target_pe, arrive_ut, aim is 0.
  local aiming is not aim:istype("Scalar").
  local done is lexicon("reason", "", "dv", 0).
  local mid is (time:seconds + arrive_ut) / 2.
  if ship:orbit:hasnextpatch {
    local soi_ut is time:seconds + ship:orbit:nextpatcheta.
    if soi_ut < mid - 120 {
      local after is soi_ut + 30.
      local waited is claudia_wait_until(ctx, after).
      if waited <> "" {
        claudia_make_safe().
        set done:reason to waited.
        return done.
      }
    }
  }
  local good is 100.
  if aiming {
    set good to claudia_aim_good_km().
    if claudia_aim_cost(aim, claudia_patch(ship:orbit, dest), target_pe) <= good {
      return done.
    }
  } else if abs(claudia_pe_error(ship:orbit, dest, target_pe)) <= 1000 {
    return done.
  }

  local nd is node(mid, 0, 0, 0).
  add nd.
  local found is claudia_descend({
    parameter x.
    set nd:radialout to x[0].
    set nd:normal to x[1].
    set nd:prograde to x[2].
    if aiming {
      return claudia_aim_cost(aim, claudia_patch(nd:orbit, dest), target_pe).
    }
    return abs(claudia_pe_error(nd:orbit, dest, target_pe)).
  }, list(0, 0, 0), list(1, 1, 1), 10, good).
  set nd:radialout to found:x[0].
  set nd:normal to found:x[1].
  set nd:prograde to found:x[2].
  if nd:deltav:mag < 0.1 {
    remove nd.
    return done.
  }
  local burned is claudia_burn(ctx, nd).
  set done:dv to burned:dv_used.
  if not burned:success {
    set done:reason to burned:reason.
  }
  return done.
}
