// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block transfer_to, the spec's `transfer` (kOS already has a built-in TRANSFER, for moving
// resources, which a function may not hide): flies from orbit around a body to an encounter with
// one of its moons, planning the ejection with RSVP v9 (bundled, kerboscript/rsvp/NOTE.md). Built on
// claudia_lib.ks and exec_node.ks; block-only helpers are prefixed transfer_to_, and the return
// block shares the patch, descent and mid-course correction helpers here. The "//|" lines are
// Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| transfer_to(options): flies from orbit to an encounter with a moon of the body the craft orbits (the
//| Mun or Minmus from Kerbin): RSVP v9 plans the ejection burn, exec_node flies it, and a mid-course
//| correction halfway through the coast aims for target_pe. It ends after the correction, before
//| arrival. (It is the spec's transfer block: kOS's own TRANSFER moves resources.)
//| Options:
//|   target [required]: the body to reach, a name ("Mun") or a body (body("Minmus")).
//|   target_pe [required]: periapsis altitude at the target, m.
//|   window ["next"]: "next" departs in the cheapest transfer window from 5 minutes ahead (RSVP
//|     searches one period of the target, a Mun's 6 days or so); a time (UT seconds) searches
//|     from that time instead. The block warps on rails to the window however far off it is: no Due
//|     Time is needed for it.
//|   correction [true]: false leaves out the mid-course correction.
//|   max_duration ["auto"]: the time to the planned arrival plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: encounter_ut (UT the craft enters the target's sphere of influence),
//| target_pe_actual (m, the periapsis predicted at the target), correction_dv (m/s the correction
//| burned; 0 when the prediction was already within 1 km of target_pe, the fix was under 0.1 m/s,
//| or correction is false).
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
  local pe is transfer_to_pe_unreadable(opt:target_pe).
  if pe <> "" {
    return pe.
  }
  local window is transfer_to_window_unreadable(opt:window).
  if window <> "" {
    return window.
  }
  return transfer_to_correction_unreadable(opt:correction).
}

function transfer_to_pe_unreadable {
  parameter target_pe.
  if not target_pe:istype("Scalar") or target_pe <= 0 {
    return "target_pe must be metres above 0, not " + target_pe.
  }
  return "".
}

function transfer_to_window_unreadable {
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

function transfer_to_correction_unreadable {
  parameter correction.
  if not correction:istype("Boolean") {
    return "correction must be true or false".
  }
  return "".
}

// The earliest departure (UT): 5 minutes from now, time to plan, turn and lead the burn, or window
// if that is a later UT.
function transfer_to_earliest {
  parameter now, window.
  local earliest is now + 300.
  if window:istype("Scalar") {
    set earliest to max(earliest, window).
  }
  return earliest.
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

// Seconds RSVP searches for a departure (s): the longer of the two periods, or the synodic period
// if that is longer (RSVP's own default for a body). The craft leaves from its own orbit, so the
// limit on the window is where the target is round its orbit, not the craft's lap: a span of
// transfer_to_synodic(craft period, target period) is about one craft orbit for a moon, which left
// the target's phase to chance (#228).
function transfer_to_search_duration {
  parameter period_a, period_b.
  return max(max(period_a, period_b), transfer_to_synodic(period_a, period_b)).
}

// RSVP's options for the ejection burn: it creates the first node only, and costs each trial as
// the ejection plus a circular capture at target_pe, the orbit circularize(at pe) flies afterwards,
// so the window is the cheapest overall rather than the fastest. Time of flight and search interval
// are left to RSVP's defaults.
function transfer_to_rsvp_options {
  parameter earliest, target_pe, craft_period, target_period.
  return lexicon(
    "create_maneuver_nodes", "first",
    "final_orbit_periapsis", target_pe,
    "final_orbit_type", "circular",
    "earliest_departure", earliest,
    "search_duration", transfer_to_search_duration(craft_period, target_period)).
}

// Coordinate descent: lowers cost(x) over the list of numbers x, one coordinate at a time, starting
// with steps (one per coordinate) and stepping on while that helps; when no step helps, every step
// halves, up to rounds times. Stops early once the cost is at most good, or after 400 tries.
// Returns lexicon("x", the best list, "cost", its cost).
function transfer_to_descend {
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

// RSVP's lexicon of functions, loaded on first use: a bundled file's top-level statements would
// only run after the Mission Script ends, so RSVP's main is a function called here.
function transfer_to_rsvp {
  if not (defined rsvp) {
    rsvp_file_main().
  }
  return rsvp.
}

// The first patch of orb's path (orb itself or a later one) around dest:
// lexicon("orbit", that patch, "soi_ut", UT it begins), or "none" if the path never gets there.
function transfer_to_patch {
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
function transfer_to_pe_error {
  parameter orb, dest, target_pe.
  local p is transfer_to_patch(orb, dest).
  if p:istype("String") {
    return 1e12.
  }
  return p:orbit:periapsis - target_pe.
}

// Options for a burn the block flies with exec_node: its own common options, and what is left of
// max_duration.
function transfer_to_burn_options {
  parameter ctx, nd.
  local opt is ctx:opt.
  local burn is lexicon("node", nd, "min_ec_pct", opt:min_ec_pct, "abort_on_part_loss", opt:abort_on_part_loss, "stage_limit", opt:stage_limit).
  if opt:max_duration:istype("Scalar") {
    set burn:max_duration to max(1, opt:max_duration - (time:seconds - ctx:start_ut)).
  }
  return burn.
}

// Flies nd with exec_node, counting its delta-v as the block's. Returns exec_node's result.
function transfer_to_burn {
  parameter ctx, nd.
  local burned is exec_node(transfer_to_burn_options(ctx, nd)).
  set ctx:dv_used to ctx:dv_used + burned:dv_used.
  // exec_node's staging dropped parts on purpose; don't count them as lost.
  set ctx:parts to ship:parts:length.
  set ctx:last_ut to time:seconds.
  return burned.
}

// The mid-course correction: a node halfway between now and arrive_ut, aimed at target_pe at dest,
// flown with exec_node. A sphere-of-influence change before halfway (leaving a moon on the way home)
// is waited out first (on rails, however long), so the node is placed on the orbit it will be on. Nothing is burned
// when the prediction is already within 1 km of target_pe or the fix is under 0.1 m/s.
// Returns lexicon("reason", "" or why the block fails, "dv", m/s burned).
function transfer_to_correct {
  parameter ctx, dest, target_pe, arrive_ut.
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
  if abs(transfer_to_pe_error(ship:orbit, dest, target_pe)) <= 1000 {
    return done.
  }

  local nd is node(mid, 0, 0, 0).
  add nd.
  local found is transfer_to_descend({
    parameter x.
    set nd:radialout to x[0].
    set nd:normal to x[1].
    set nd:prograde to x[2].
    return abs(transfer_to_pe_error(nd:orbit, dest, target_pe)).
  }, list(0, 0, 0), list(1, 1, 1), 10, 100).
  set nd:radialout to found:x[0].
  set nd:normal to found:x[1].
  set nd:prograde to found:x[2].
  if nd:deltav:mag < 0.1 {
    remove nd.
    return done.
  }
  local burned is transfer_to_burn(ctx, nd).
  set done:dv to burned:dv_used.
  if not burned:success {
    set done:reason to burned:reason.
  }
  return done.
}

function transfer_to {
  parameter options is lexicon().
  local extra is lexicon("encounter_ut", -1, "target_pe_actual", -1, "correction_dv", 0).
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

  local earliest is transfer_to_earliest(time:seconds, opt:window).

  // RSVP searches a whole period of the target for the cheapest window, and adds the ejection node;
  // on failure it removes every node.
  local planned is transfer_to_rsvp():goto(dest, transfer_to_rsvp_options(earliest, opt:target_pe, ship:orbit:period, dest:orbit:period)).
  if not planned:success or not hasnode {
    return claudia_finish(ctx, false, transfer_to_rsvp_reason(planned), extra).
  }
  // The ejection burn may be far off (window "next" can be more than an orbit away, a UT window
  // further): exec_node warps there on rails, with no cap (ADR 0004).
  if opt:max_duration:istype("String") {
    set opt:max_duration to planned:predicted:arrival:time - ctx:start_ut + 900.
  }

  local ejected is transfer_to_burn(ctx, nextnode).
  if not ejected:success {
    return claudia_finish(ctx, false, ejected:reason, extra).
  }
  local enc is transfer_to_patch(ship:orbit, dest).
  if enc:istype("String") {
    return claudia_finish(ctx, false, "no encounter with " + dest:name + " after the ejection burn", extra).
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    local fixed is transfer_to_correct(ctx, dest, opt:target_pe, enc:soi_ut).
    set extra:correction_dv to round(fixed:dv, 2).
    if fixed:reason <> "" {
      return claudia_finish(ctx, false, fixed:reason, extra).
    }
    set enc to transfer_to_patch(ship:orbit, dest).
    if enc:istype("String") {
      return claudia_finish(ctx, false, "no encounter with " + dest:name + " after the correction", extra).
    }
  }
  set extra:encounter_ut to round(enc:soi_ut).
  set extra:target_pe_actual to round(enc:orbit:periapsis).
  return claudia_finish(ctx, true, "", extra).
}
