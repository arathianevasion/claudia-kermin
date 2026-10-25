// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block circularize: makes the orbit circular at apoapsis, periapsis or an altitude, by
// planning one maneuver node and flying it with exec_node. Built on claudia_lib.ks; block-only
// helpers are prefixed circularize_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| circularize(options): makes the orbit circular at apoapsis, periapsis or an altitude in one burn.
//| The burn is planned as a maneuver node and flown by exec_node. After launch, in the same Mission
//| Script, it finishes the orbit; at "pe" inside a new body's SOI it is the capture burn.
//| Options:
//|   at ["ap"]: where to burn: "ap", "pe" or an altitude in m between pe and ap (the next pass).
//|   tolerance [0.01]: the eccentricity it must end within; more fails the block.
//|   max_duration ["auto"]: as for exec_node (the wait to the burn, twice the burn, plus 300 s).
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: ecc (the eccentricity when the block ended). Succeeds at once, with no burn, if the
//| orbit is already within tolerance. Fails with "no apoapsis" on an escape orbit (use "pe"), an
//| altitude the orbit doesn't pass, a burn point inside the atmosphere or below the surface, the
//| reasons exec_node gives (no thrust, a wait over the cap, out of fuel, an abort), or "eccentricity
//| <e> is over tolerance <t>"; on failure the node is left in place.
//| Example: `local r is launch(lexicon("target_ap", 80000)). if r:success { set r to circularize(). }
//| addons:claudia:report(r).`

// The block's own options and defaults (max_duration "auto" is resolved by exec_node).
function circularize_defaults {
  return lexicon("at", "ap", "tolerance", 0.01).
}

// "" or why the at option isn't one the block knows.
function circularize_unreadable {
  parameter at_opt.
  if at_opt:istype("String") {
    if at_opt <> "ap" and at_opt <> "pe" {
      return "at must be 'ap', 'pe' or an altitude in m, not '" + at_opt + "'".
    }
  }
  return "".
}

// The burn that makes the orbit (gravitational parameter mu, semi-major axis sma in m, negative for
// an escape orbit, eccentricity ecc) circular where the craft is rad m from the body's centre, climbing
// or falling there: lexicon("prograde", m/s, "radial", m/s, outward), as for a maneuver node.
function circularize_burn {
  parameter mu, rad, sma, ecc, climbing.
  local v_now is sqrt(mu * (2 / rad - 1 / sma)).
  local v_circ is sqrt(mu / rad).
  local h is sqrt(mu * sma * (1 - ecc ^ 2)).
  local cos_gamma is min(1, h / (rad * v_now)).
  local sin_gamma is sqrt(1 - cos_gamma ^ 2).
  if not climbing {
    set sin_gamma to -sin_gamma.
  }
  return lexicon("prograde", v_circ * cos_gamma - v_now, "radial", -v_circ * sin_gamma).
}

// The true anomaly (degrees, 0 to 180: the climbing pass; 360 minus it is the falling one) at which
// an orbit of sma and ecc is rad m from the body's centre, or -1 if it never is.
function circularize_anomaly {
  parameter rad, sma, ecc.
  if ecc <= 0 {
    return -1.
  }
  local c is (sma * (1 - ecc ^ 2) / rad - 1) / ecc.
  if abs(c) > 1 + 1e-9 {
    return -1.
  }
  return arccos(max(-1, min(1, c))).
}

// Seconds to go from true anomaly now_deg to target_deg on an elliptical orbit of ecc and period.
function circularize_time_to {
  parameter now_deg, target_deg, ecc, period.
  local diff is circularize_mean_anomaly(target_deg, ecc) - circularize_mean_anomaly(now_deg, ecc).
  return (diff - 360 * floor(diff / 360)) / 360 * period.
}

// The mean anomaly (degrees) at true anomaly nu (degrees) on an elliptical orbit of ecc.
function circularize_mean_anomaly {
  parameter nu, ecc.
  local e_anom is arctan2(sqrt(1 - ecc ^ 2) * sin(nu), ecc + cos(nu)).
  // A literal rather than CONSTANT:PI, so this stays pure.
  return e_anom - ecc * sin(e_anom) * 180 / 3.141592653589793.
}

// Where and how to burn: lexicon("error", "" or why not, "ut", s, "prograde", m/s, "radial", m/s).
function circularize_plan {
  parameter at_opt.
  local o is ship:orbit.
  local mu is ship:body:mu.
  local radius is ship:body:radius.
  local plan is lexicon("error", "", "ut", 0, "prograde", 0, "radial", 0).
  local rad is 0.
  local climbing is true.
  if at_opt:istype("String") and at_opt = "ap" {
    if o:eccentricity >= 1 {
      set plan:error to "no apoapsis: the orbit escapes (ecc " + round(o:eccentricity, 3) + "); circularize at pe".
      return plan.
    }
    set rad to radius + o:apoapsis.
    set plan:ut to time:seconds + eta:apoapsis.
  } else if at_opt:istype("String") {
    set rad to radius + o:periapsis.
    set plan:ut to time:seconds + eta:periapsis.
  } else {
    if o:eccentricity >= 1 {
      set plan:error to "an altitude needs a closed orbit (ecc " + round(o:eccentricity, 3) + "); circularize at pe".
      return plan.
    }
    set rad to radius + at_opt.
    local nu is circularize_anomaly(rad, o:semimajoraxis, o:eccentricity).
    if nu < 0 {
      set plan:error to "the orbit doesn't pass " + round(at_opt) + " m (pe " + round(o:periapsis) + " m, ap " + round(o:apoapsis) + " m)".
      return plan.
    }
    local up_in is circularize_time_to(o:trueanomaly, nu, o:eccentricity, o:period).
    local down_in is circularize_time_to(o:trueanomaly, 360 - nu, o:eccentricity, o:period).
    set climbing to up_in <= down_in.
    set plan:ut to time:seconds + min(up_in, down_in).
  }
  local floor_alt is 0.
  if ship:body:atm:exists {
    set floor_alt to ship:body:atm:height.
  }
  if rad <= radius + floor_alt {
    set plan:error to "the burn point at " + round(rad - radius) + " m is inside the atmosphere or below the surface".
    return plan.
  }
  local burn is circularize_burn(mu, rad, o:semimajoraxis, o:eccentricity, climbing).
  set plan:prograde to burn:prograde.
  set plan:radial to burn:radial.
  return plan.
}

function circularize {
  parameter options is lexicon().
  local extra is lexicon("ecc", round(ship:orbit:eccentricity, 5)).
  local ctx is claudia_begin("circularize", options, circularize_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is circularize_unreadable(opt:at).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if ship:orbit:eccentricity <= opt:tolerance {
    return claudia_finish(ctx, true, "", extra).
  }

  local plan is circularize_plan(opt:at).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  local nd is node(plan:ut, plan:radial, 0, plan:prograde).
  add nd.
  // The burn is judged by the eccentricity it leaves, not exec_node's residual.
  local flown is exec_node(lexicon(
    "node", nd,
    "max_residual", 1000000,
    "max_duration", opt:max_duration,
    "min_ec_pct", opt:min_ec_pct,
    "abort_on_part_loss", opt:abort_on_part_loss,
    "stage_limit", opt:stage_limit)).
  set ctx:dv_used to flown:dv_used.
  set extra:ecc to round(ship:orbit:eccentricity, 5).
  if not flown:success {
    return claudia_finish(ctx, false, flown:reason, extra).
  }
  if ship:orbit:eccentricity > opt:tolerance {
    return claudia_finish(ctx, false, "eccentricity " + round(ship:orbit:eccentricity, 4) + " is over tolerance " + opt:tolerance, extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
