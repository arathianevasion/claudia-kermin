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
//|     time it takes to burn half the node's delta-v.
//|   rcs_trim [false]: true trims a residual above max_residual with RCS translation (up to 60 s).
//|   max_duration ["auto"]: the wait to the burn plus twice the burn time plus 300 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: residual (m/s the node still needed when the block ended), burn_time (s the
//| engines burned). Fails with "no maneuver node", "no thrust", a node or lead it can't read, "residual
//| <x> m/s is over max_residual", out of fuel, or an abort; on failure the node is left in place.
//| Plan the node yourself first (ADD NODE(time, radial, normal, prograde)); the node must be far
//| enough ahead to turn and start the burn (lead plus about 30 s).
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

// Burn timing for a node: lexicon("burn_time", s, "lead", s, "start_in", s from now).
// lead_opt is "half_burn" or seconds before the node.
function exec_node_timing {
  parameter dv, mass_t, thrust_kn, isp, node_eta, lead_opt.
  local burn is claudia_burn_time(dv, mass_t, thrust_kn, isp).
  local lead is 0.
  if lead_opt:istype("String") {
    set lead to claudia_burn_time(dv / 2, mass_t, thrust_kn, isp).
  } else {
    set lead to lead_opt.
  }
  return lexicon("burn_time", burn, "lead", lead, "start_in", node_eta - lead).
}

function exec_node {
  parameter options is lexicon().
  local extra is lexicon("residual", -1, "burn_time", 0).
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
  set extra:residual to round(nd:deltav:mag, 3).

  // A craft whose engines aren't lit yet stages into them (within stage_limit).
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }

  local timing is exec_node_timing(nd:deltav:mag, ship:mass, ship:availablethrust, claudia_engine_isp(), nd:eta, opt:lead).
  if timing:burn_time < 0 {
    return claudia_finish(ctx, false, "no thrust", extra).
  }
  local start_ut is time:seconds + timing:start_in.
  if opt:max_duration:istype("String") {
    set opt:max_duration to max(0, timing:start_in) + 2 * timing:burn_time + 300.
  }
  // Point at the burn as it is now (raw-frame vectors drift during warp); the craft turns once warp
  // drops to 1x, 30 s before the start.
  lock steering to nd:deltav.
  local waited is claudia_wait_until(ctx, start_ut).
  if waited <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, waited, extra).
  }

  local steer is nd:deltav.
  lock steering to steer.
  local tset is 0.
  lock throttle to tset.
  local initial is nd:deltav.
  local done_dv is min(0.1, opt:max_residual / 4).
  local burn_start is -1.
  until false {
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
  set rcs to rcs_was.
  return reason.
}
