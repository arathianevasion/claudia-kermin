// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block transfer_to, the spec's `transfer` (kOS already has a built-in TRANSFER, for moving
// resources, which a function may not hide): flies from orbit around a body to an encounter with
// one of its moons, finding the ejection with ADDONS:CLAUDIA:EJECTIONBURN. Built on
// claudia_lib.ks and exec_node.ks (the patch, descent and mid-course correction helpers it shares with
// return are in claudia_lib.ks); block-only helpers are prefixed transfer_to_. The "//|" lines are
// Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| transfer_to(options): flies from orbit to an encounter with a moon of the body the craft orbits (the
//| Mun or Minmus from Kerbin): the mod's ejection solver finds the burn, exec_node flies it, and a mid-course
//| correction halfway through the coast aims for target_pe. It ends after the correction, before
//| arrival. (It is the spec's transfer block: kOS's own TRANSFER moves resources.)
//| Options:
//|   target [required]: the body to reach, a name ("Mun") or a body (body("Minmus")).
//|   target_pe [required]: periapsis altitude at the target, m.
//|   window ["next"]: "next" departs in the next transfer window once the craft can have turned to
//|     the burn (one synodic period, the time between windows, is searched: about 33 minutes for the
//|     Mun from a 100 km orbit); a time (UT seconds) searches the same span from that time. It warps
//|     there on rails: no Due Time needed.
//|   correction [true]: false leaves out the mid-course correction.
//|   max_duration ["auto"]: the time to the planned arrival plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: encounter_ut (UT the craft enters the target's sphere of influence),
//| target_pe_actual (m, the periapsis predicted at the target), correction_dv (m/s the corrections
//| burned: one making the encounter if the ejection missed, then the mid-course one, none within
//| 1 km of target_pe, under 0.1 m/s or with correction false).
//| ejection_node_dv (m/s the ejection node asked for), ejection_dv_used (m/s the
//| ejection burn used) and ejection_burn_time (s the engines burned): a dv_used well above the node
//| means the burn wasted thrust. All -1 if it failed before the ejection burn.
//| ejection_wait (s from the block's start to the ejection node; for window "next" at most a
//| synodic period plus the turn time): -1 if it failed before the node was planned.
//| Fails with "target is required", "target_pe is required", an option value it can't read, "no body
//| named <x>", a target that doesn't orbit the craft's body, "not in orbit", "a maneuver node is
//| already planned: remove it first", "found no transfer: <why>", "no encounter with
//| <target> after the ejection burn, and no correction fits: <why>" (or "the correction failed:
//| <why>"; or "after the correction"), an exec_node failure (its node stays), or an abort. The craft must be the active vessel.
//| Capture isn't part of transfer_to: inside the target's sphere of influence, fly
//| `circularize(lexicon("at", "pe"))`: in the same sequence or the next, after
//| `coast(lexicon("until", res:encounter_ut + 60))` (no Due Time needed).
//| Example: `local res is transfer_to(lexicon("target", "Mun", "target_pe", 30000)). addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function transfer_to_defaults {
  return lexicon("target", "required", "target_pe", "required", "window", "next", "correction", true).
}

// "" or why an option value isn't one the block can use.
function transfer_to_unreadable {
  parameter opt.
  if opt:target:istype("String") {
    if opt:target = "required" {
      return "target is required".
    }
  } else if not opt:target:istype("Body") {
    return "target must be a body or its name".
  }
  if opt:target_pe:istype("String") {
    if opt:target_pe = "required" {
      return "target_pe is required".
    }
  }
  local pe is claudia_pe_unreadable(opt:target_pe).
  if pe <> "" {
    return pe.
  }
  local window is claudia_window_unreadable(opt:window).
  if window <> "" {
    return window.
  }
  return claudia_correction_unreadable(opt:correction).
}

// What the correction after a missed ejection asks ADDONS:CLAUDIA:EJECTIONBURN: the soonest burn the craft can turn to
// (turn_time plus a minute for the lead), one the craft's dv_left (m/s, stock's; 0 or less for unknown) pays for, and
// with max_duration given (auto false) one that leaves 120 s of it to burn in. Pure.
function transfer_to_correction_ask {
  parameter dest_name, target_pe, now, turn_time, start_ut, max_duration, auto, dv_left.
  local ask is lexicon("target", dest_name, "target_pe", target_pe, "earliest_ut", now + turn_time + 60).
  if not auto {
    set ask:latest_ut to start_ut + max_duration - 120.
  }
  if dv_left > 0 {
    set ask:max_dv to dv_left.
  }
  return ask.
}

// After an ejection that left no encounter with dest: the cheapest burn from the orbit the craft is on that meets dest
// at target_pe, within the block's limits (transfer_to_correction_ask), flown with exec_node. With max_duration auto, it
// moves to the new arrival plus 900 s. Returns lexicon("reason", "" or ", and no correction fits: <why>" or exec_node's
// failure after ", and the correction failed: ", "dv", m/s burned).
function transfer_to_make_encounter {
  parameter ctx, dest, auto, turn_time.
  local opt is ctx:opt.
  local done is lexicon("reason", "", "dv", 0).
  local found is addons:claudia:ejectionburn(transfer_to_correction_ask(dest:name, opt:target_pe, time:seconds, turn_time,
    ctx:start_ut, opt:max_duration, auto, claudia_dv_remaining())).
  if found:error <> "" {
    set done:reason to ", and no correction fits: " + found:error:replace("found no transfer: ", "").
    return done.
  }
  if auto {
    set opt:max_duration to found:arrival_ut - ctx:start_ut + 900.
  }
  add node(found:ut, found:radial, found:normal, found:prograde).
  local burned is claudia_burn(ctx, nextnode).
  set done:dv to burned:dv_used.
  if not burned:success {
    set done:reason to ", and the correction failed: " + burned:reason.
  }
  return done.
}

function transfer_to {
  parameter options is lexicon().
  local extra is lexicon("encounter_ut", -1, "target_pe_actual", -1, "correction_dv", 0,
    "ejection_wait", -1, "ejection_node_dv", -1, "ejection_dv_used", -1, "ejection_burn_time", -1).
  local ctx is claudia_begin("transfer_to", options, transfer_to_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is transfer_to_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }

  local dest is opt:target.
  if dest:istype("String") {
    if not bodyexists(dest) {
      return claudia_finish(ctx, false, "no body named " + dest, extra).
    }
    set dest to body(dest).
  }
  if status <> "ORBITING" {
    return claudia_finish(ctx, false, "not in orbit: transfer_to starts from orbit around " + ship:body:name, extra).
  }
  if not dest:hasbody or dest:body <> ship:body {
    return claudia_finish(ctx, false, dest:name + " does not orbit " + ship:body:name + ", the body the craft orbits", extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }

  // The solver finds the cheapest ejection in the next window and gives its node's components. The
  // calls are block-internal (ADR 0006, ADR 0007), so they sit in the block. The earliest departure
  // leaves time to turn to the burn, which exec_node does before it warps.
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:error <> "" {
    return claudia_finish(ctx, false, turn:error, extra).
  }
  local found is addons:claudia:ejectionburn(lexicon("target", dest:name, "target_pe", opt:target_pe,
    "earliest_ut", ctx:start_ut + turn:turn_time, "window", opt:window)).
  if found:error <> "" {
    return claudia_finish(ctx, false, found:error, extra).
  }
  add node(found:ut, found:radial, found:normal, found:prograde).
  // The ejection burn may be far off (window "next" up to a synodic period away, a UT window
  // further): exec_node warps there on rails, with no cap (ADR 0004).
  set extra:ejection_wait to round(found:ut - ctx:start_ut).
  local auto is opt:max_duration:istype("String").
  if auto {
    set opt:max_duration to found:arrival_ut - ctx:start_ut + 900.
  }

  local ejected is claudia_burn(ctx, nextnode).
  set extra:ejection_node_dv to ejected:node_dv.
  set extra:ejection_dv_used to round(ejected:dv_used, 1).
  set extra:ejection_burn_time to ejected:burn_time.
  if not ejected:success {
    return claudia_finish(ctx, false, ejected:reason, extra).
  }
  local enc is claudia_patch(ship:orbit, dest).
  local missed is "no encounter with " + dest:name + " after the ejection burn".
  if enc:istype("String") {
    // A missed ejection (#363) gets a correction that makes the encounter, found as the ejection was.
    local made is transfer_to_make_encounter(ctx, dest, auto, turn:turn_time).
    set extra:correction_dv to round(made:dv, 2).
    if made:reason <> "" {
      return claudia_finish(ctx, false, missed + made:reason, extra).
    }
    set enc to claudia_patch(ship:orbit, dest).
    if enc:istype("String") {
      return claudia_finish(ctx, false, "no encounter with " + dest:name + " after the correction", extra).
    }
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    local fixed is claudia_correct(ctx, dest, opt:target_pe, enc:soi_ut).
    set extra:correction_dv to round(extra:correction_dv + fixed:dv, 2).
    if fixed:reason <> "" {
      return claudia_finish(ctx, false, fixed:reason, extra).
    }
    set enc to claudia_patch(ship:orbit, dest).
    if enc:istype("String") {
      return claudia_finish(ctx, false, "no encounter with " + dest:name + " after the correction", extra).
    }
  }
  set extra:encounter_ut to round(enc:soi_ut).
  set extra:target_pe_actual to round(enc:orbit:periapsis).
  return claudia_finish(ctx, true, "", extra).
}
