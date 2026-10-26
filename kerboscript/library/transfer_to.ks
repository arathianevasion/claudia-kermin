// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block transfer_to, the spec's `transfer` (kOS already has a built-in TRANSFER, for moving
// resources, which a function may not hide): flies from orbit around a body to an encounter with
// one of its moons, planning the ejection with RSVP v9 (bundled, kerboscript/rsvp/NOTE.md). Built on
// claudia_lib.ks and exec_node.ks (the patch, descent and mid-course correction helpers it shares with
// return are in claudia_lib.ks); block-only helpers are prefixed transfer_to_. The "//|" lines are
// Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| transfer_to(options): flies from orbit to an encounter with a moon of the body the craft orbits (the
//| Mun or Minmus from Kerbin): RSVP v9 plans the ejection burn, exec_node flies it, and a mid-course
//| correction halfway through the coast aims for target_pe. It ends after the correction, before
//| arrival. (It is the spec's transfer block: kOS's own TRANSFER moves resources.)
//| Options:
//|   target [required]: the body to reach, a name ("Mun") or a body (body("Minmus")).
//|   target_pe [required]: periapsis altitude at the target, m.
//|   window ["next"]: "next" departs in the next transfer window from 5 minutes ahead (RSVP
//|     searches one synodic period, the time between windows: about 33 minutes for the Mun from a
//|     100 km orbit); a time (UT seconds) searches the same span from that time instead. The block
//|     warps on rails to the window however far off it is: no Due Time is needed for it.
//|   correction [true]: false leaves out the mid-course correction.
//|   max_duration ["auto"]: the time to the planned arrival plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: encounter_ut (UT the craft enters the target's sphere of influence),
//| target_pe_actual (m, the periapsis predicted at the target), correction_dv (m/s the correction
//| burned; 0 when the prediction was already within 1 km of target_pe, the fix was under 0.1 m/s,
//| or correction is false).
//| ejection_node_dv (m/s the ejection node asked for: RSVP's choice), ejection_dv_used (m/s the
//| ejection burn used) and ejection_burn_time (s the engines burned): a dv_used well above the node
//| means the burn wasted thrust; a node well above the delta-v a Hohmann transfer needs means RSVP
//| chose it. All -1 when the block failed before the ejection burn was flown.
//| ejection_wait (s from the block's start to the ejection node RSVP planned; for window "next" at
//| most a synodic period and 5 minutes, plus RSVP's run): -1 when the block failed before RSVP
//| planned the node.
//| Fails with "target is required", "target_pe is required", an option value it can't read, "no body
//| named <x>", a target that doesn't orbit the craft's body, "not in orbit", "a maneuver node is
//| already planned: remove it first", "rsvp found no transfer: <why>", "no encounter with
//| <target> after the ejection burn" (or "after the correction"), an exec_node failure (that burn's
//| node is left in place), or an abort. The craft must be the active vessel (RSVP checks).
//| Capture is not part of transfer_to: once inside the target's sphere of influence, fly
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

// Seconds between one departure window and the next for orbits of these periods (s); the longer
// period if they are the same.
function transfer_to_synodic {
  parameter period_a, period_b.
  local beat is abs(1 / period_a - 1 / period_b).
  if beat < 1e-12 {
    return max(period_a, period_b).
  }
  return 1 / beat.
}

// RSVP's options for the ejection burn: it creates the first node only, and costs each trial as
// the ejection plus a circular capture at target_pe, the orbit circularize(at pe) flies afterwards,
// so the window is the cheapest overall rather than the fastest. Time of flight is left to RSVP's
// default (it tries the Hohmann time first).
//
// The search spans one synodic period from earliest: the time between windows, which covers every
// phase of the target against the craft, so RSVP finds the next window (#228). A longer span only
// adds windows that, from a circular orbit, cost the same to a few m/s, so RSVP picks one by noise:
// a whole Mun period (RSVP's default) planned a window 60,000 s away and the craft drained its
// charge waiting for it.
//
// The interval is how far apart RSVP seeds its local searches (kerboscript/rsvp/search.ks): a seed
// every search_interval, each a hill climb with a first step of search_interval / 10 that stops once
// the step is under step_threshold, max(120, 0.002 * search_interval) s. RSVP's default for a moon is
// half the craft's period, about 980 s from 100 km: two seeds a window, each step 98 s, under the
// threshold, so no climb ran and RSVP took the better of two departures (1649 m/s against about 860
// for a Hohmann transfer). A hill climb needs an interval of 1200 s or more, and then never steps
// finer than 120 s, 22 degrees round a 100 km orbit, so instead the interval is span / 100: 101
// departures, 20 s apart for the Mun from 100 km, each tried at the Hohmann time of flight. That is
// how the 839 m/s node was found, over 142 departures whose phase against the Mun drifted 14 s each,
// and it costs fewer Lambert solves than that search did.
function transfer_to_rsvp_options {
  parameter earliest, target_pe, craft_period, target_period.
  local span is transfer_to_synodic(craft_period, target_period).
  return lexicon(
    "create_maneuver_nodes", "first",
    "final_orbit_periapsis", target_pe,
    "final_orbit_type", "circular",
    "earliest_departure", earliest,
    "search_duration", span,
    "search_interval", span / 100).
}

// RSVP's failure as a reason: "rsvp found no transfer: <its problems>".
function transfer_to_rsvp_reason {
  parameter planned.
  local texts is list().
  if planned:haskey("problems") {
    for key in planned:problems:keys {
      texts:add(planned:problems[key]).
    }
  }
  if texts:length = 0 {
    return "rsvp found no transfer".
  }
  return "rsvp found no transfer: " + texts:join("; ").
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

  local earliest is claudia_earliest(time:seconds, opt:window).

  // RSVP searches the next window for the cheapest departure, and adds the ejection node; on
  // failure it removes every node.
  local planned is claudia_rsvp():goto(dest, transfer_to_rsvp_options(earliest, opt:target_pe, ship:orbit:period, dest:orbit:period)).
  if not planned:success or not hasnode {
    return claudia_finish(ctx, false, transfer_to_rsvp_reason(planned), extra).
  }
  // The ejection burn may be far off (window "next" up to about an orbit away, a UT window
  // further): exec_node warps there on rails, with no cap (ADR 0004).
  set extra:ejection_wait to round(nextnode:time - ctx:start_ut).
  if opt:max_duration:istype("String") {
    set opt:max_duration to planned:predicted:arrival:time - ctx:start_ut + 900.
  }

  local ejected is claudia_burn(ctx, nextnode).
  set extra:ejection_node_dv to ejected:node_dv.
  set extra:ejection_dv_used to round(ejected:dv_used, 1).
  set extra:ejection_burn_time to ejected:burn_time.
  if not ejected:success {
    return claudia_finish(ctx, false, ejected:reason, extra).
  }
  local enc is claudia_patch(ship:orbit, dest).
  if enc:istype("String") {
    return claudia_finish(ctx, false, "no encounter with " + dest:name + " after the ejection burn", extra).
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    local fixed is claudia_correct(ctx, dest, opt:target_pe, enc:soi_ut).
    set extra:correction_dv to round(fixed:dv, 2).
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
