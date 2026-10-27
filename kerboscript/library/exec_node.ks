// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block exec_node: flies a maneuver node. Built on claudia_lib.ks; block-only helpers are
// prefixed exec_node_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| exec_node(options): flies a maneuver node (the next one by default) with the main engines,
//| ending when the node's remaining delta-v is used up, and removes the node on success.
//| Options:
//|   node ["next"]: the node to fly, "next" or a node from ALLNODES.
//|   max_residual [0.5]: m/s the node may still need when the burn ends; more fails the block.
//|   lead ["half_burn"]: when to start, in seconds before the node; "half_burn" starts early by the
//|     time it takes to burn half the node's delta-v, over every stage the burn uses.
//|   rcs_trim [false]: true trims a residual above max_residual with RCS translation (up to 60 s).
//|   max_duration ["auto"]: the wait to the burn plus twice the burn time plus 300 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: node_dv (m/s the node asked for before the burn), residual (m/s the node still
//| needed when the block ended), burn_time (s the engines burned), late_start_s (s the burn began
//| late; its node moves too), turn_s and turn_time (s the turn took, and its prediction), not_aligned (turns
//| given up after 120 s). It turns to the node before warping, and once Aligned is back at 1x 30 s
//| before the Burn Start (its turn time when the turn was given up); a Burn Start over a day away is
//| warped to a day out first, without turning. Fails with "no maneuver node",
//| "no thrust", "no usable torque" (no reaction wheel running and no RCS on to turn with), a node
//| or lead it can't read, "residual <x> m/s is over max_residual", out of fuel, or an abort; on
//| failure the node is left in place.
//| Plan the node yourself first (ADD NODE(time, radial, normal, prograde)); the node must be far
//| enough ahead to turn and start the burn (lead plus about a minute).
//| Example: `add node(time:seconds + 600, 0, 0, 120). local res is exec_node(). addons:claudia:report(res).`

// The block's own options and defaults (max_duration "auto" is resolved once the burn is known).
function exec_node_defaults {
  return lexicon("node", "next", "max_residual", 0.5, "lead", "half_burn", "rcs_trim", false).
}

// "" or why a string value of node or lead isn't one the block knows.
function exec_node_unreadable {
  parameter opt.
  if opt:node:istype("String") {
    if opt:node <> "next" {
      return "node must be 'next' or a maneuver node, not '" + opt:node + "'".
    }
  }
  if opt:lead:istype("String") {
    if opt:lead <> "half_burn" {
      return "lead must be 'half_burn' or seconds, not '" + opt:lead + "'".
    }
  }
  return "".
}

function exec_node {
  parameter options is lexicon().
  local extra is lexicon("node_dv", -1, "residual", -1, "burn_time", 0, "late_start_s", 0, "turn_s", 0, "turn_time", 0, "not_aligned", 0).
  local ctx is claudia_begin("exec_node", options, exec_node_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.

  local unreadable is exec_node_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local nd is 0.
  if opt:node:istype("String") {
    if not hasnode {
      return claudia_finish(ctx, false, "no maneuver node", extra).
    }
    set nd to nextnode.
  } else {
    set nd to opt:node.
  }
  set extra:node_dv to round(nd:deltav:mag, 3).
  set extra:residual to extra:node_dv.

  // The craft turns to the burn on a Coast, on its wheels and RCS: one that can't fails here, before
  // staging changes it. The call is block-internal (ADR 0006, ADR 0007), so it sits in the block.
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:error <> "" {
    return claudia_finish(ctx, false, turn:error, extra).
  }
  set extra:turn_time to round(turn:turn_time, 1).

  // A craft whose engines aren't lit yet stages into them (within stage_limit).
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }

  if opt:max_duration:istype("String") {
    local timing is claudia_burn_timing(nd:deltav:mag, claudia_burn_stages(opt:stage_limit), nd:eta, opt:lead).
    set opt:max_duration to max(0, timing:start_in) + 2 * max(0, timing:burn_time) + 300.
  }
  // Turn first, then warp to the burn start worked out from every stage the burn uses.
  local burn is claudia_warp_to_burn(ctx, nd, opt:lead, turn:turn_time).
  set extra:turn_s to burn:turn_s.
  set extra:not_aligned to burn:not_aligned.
  if burn:error <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, burn:error, extra).
  }

  local steer is nd:deltav.
  lock steering to steer.
  local tset is 0.
  lock throttle to tset.
  local initial is nd:deltav.
  local done_dv is min(0.1, opt:max_residual / 4).
  local burn_start is -1.
  local lead is nd:time - burn:start_ut.
  until false {
    // Until the engines light, a late burn takes its node with it (#363).
    if burn_start < 0 {
      set extra:late_start_s to claudia_late_start(time:seconds, burn:start_ut).
      local due is exec_node_due_ut(time:seconds, burn:start_ut, lead).
      if abs(due - nd:time) > 0.5 {
        set nd:time to due.
      }
      set initial to nd:deltav.
    }
    local left is nd:deltav.
    // Overshot (the node now points back) or close enough: stop.
    if vdot(initial, left) < 0 or left:mag < done_dv {
      break.
    }
    // Stop chasing the node's direction once it's nearly done; it swings as it shrinks.
    if left:mag > 1 {
      set steer to left.
    }
    set tset to claudia_burn_throttle(left:mag, ship:availablethrust / ship:mass, vang(ship:facing:forevector, steer)).
    if tset > 0 and burn_start < 0 {
      set burn_start to time:seconds.
    }
    // Covered by its own guard (#379): claudia_burn_throttle commands full throttle at no thrust, so
    // the tick's default (tset > 0) stages when the stage runs dry. Throttle 0 here is only a turn
    // more than 10 degrees off the node, which must not stage either.
    local ticked is claudia_tick(ctx, tset).
    if ticked <> "" {
      claudia_make_safe().
      set extra:residual to round(nd:deltav:mag, 3).
      set extra:burn_time to exec_node_burned(burn_start).
      return claudia_finish(ctx, false, ticked, extra).
    }
    wait 0.
  }
  set tset to 0.
  set extra:burn_time to exec_node_burned(burn_start).

  if opt:rcs_trim and nd:deltav:mag > opt:max_residual {
    local trimmed is exec_node_rcs_trim(ctx, nd, done_dv).
    if trimmed <> "" {
      claudia_make_safe().
      set extra:residual to round(nd:deltav:mag, 3).
      return claudia_finish(ctx, false, trimmed, extra).
    }
  }

  local residual is nd:deltav:mag.
  set extra:residual to round(residual, 3).
  claudia_release_controls().
  if residual > opt:max_residual {
    return claudia_finish(ctx, false, "residual " + round(residual, 2) + " m/s is over max_residual " + opt:max_residual + " m/s", extra).
  }
  remove nd.
  return claudia_finish(ctx, true, "", extra).
}

// The UT a burn starting at now should have its node at: lead after the start, which is start_ut
// until the burn runs over a second late, then now. Stock's node vector matches the planned velocity
// at the node's UT, so chasing it from a burn centred tau s past the node tilts the thrust out by
// about 2 n tau (n the orbit's mean motion) and leaves the orbit about n tau^2 dv lower there: for
// #363's 860 m/s node on a 100 km orbit, a minute late was a 22 degree burn that left apoapsis
// 3,400 km short and periapsis 10 km lower. Pure.
function exec_node_due_ut {
  parameter now, start_ut, lead.
  if now - start_ut < 1 {
    return start_ut + lead.
  }
  return now + lead.
}

function exec_node_burned {
  parameter burn_start.
  if burn_start < 0 {
    return 0.
  }
  return round(time:seconds - burn_start, 1).
}

// Translates on RCS along the node's remaining delta-v until under done_dv, for up to 60 s.
// Returns "" or an abort reason.
function exec_node_rcs_trim {
  parameter ctx, nd, done_dv.
  local rcs_was is rcs.
  rcs on.
  // RCS Use leaves RCS alone while translating (claudia_rcs_translate replaces this in #401).
  set ctx:rcs_translating to true.
  local stop_ut is time:seconds + 60.
  local reason is "".
  until nd:deltav:mag < done_dv or time:seconds > stop_ut {
    local left is nd:deltav.
    local scale is max(left:mag, 0.5).
    set ship:control:fore to vdot(left, ship:facing:forevector) / scale.
    set ship:control:starboard to vdot(left, ship:facing:starvector) / scale.
    set ship:control:top to vdot(left, ship:facing:topvector) / scale.
    set reason to claudia_tick(ctx, 0).
    if reason <> "" {
      break.
    }
    wait 0.
  }
  set ship:control:neutralize to true.
  set ctx:rcs_translating to false.
  set rcs to rcs_was.
  return reason.
}
