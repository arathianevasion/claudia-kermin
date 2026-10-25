// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block ascend: flies from the surface of an airless body to a circular orbit above its
// highest terrain. Built on claudia_lib.ks and exec_node.ks (which flies the circularization);
// block-only helpers are prefixed ascend_. The "//|" lines are Claudia's docs for it; the first is
// its summary.
//
// The climb: straight up off the ground, then the pitch that holds half the vertical speed that
// would coast it up to target_alt (so most of the thrust goes into orbital speed), until apoapsis
// reaches it; then a node at apoapsis circularizes.

@lazyglobal off.

//| ascend(options): flies from the surface of an airless body (the Mun, Minmus...) to a circular
//| orbit at target_alt: up off the ground, then a low climb that builds orbital speed until
//| apoapsis reaches target_alt, then a circularization burn at apoapsis (flown with exec_node).
//| Options:
//|   target_alt ["auto"]: orbit altitude in m; "auto" is 5 km above the body's highest terrain.
//|     It must be above the highest terrain (ADDONS:CLAUDIA:HIGHESTTERRAIN(body)) and inside the SOI.
//|   target_inc [0]: orbit inclination in degrees (0 to 180; under 90 is prograde); an inclination
//|     below the site's latitude can't be reached, and gives the nearest one.
//|   max_duration [900]: seconds.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: ap_error (m, apoapsis minus target_alt once in orbit). Fails with "ascend flies
//| from airless bodies only", not landed, a target_alt it can't use, "no thrust", thrust-to-weight
//| under 1.05, a periapsis still under the highest terrain, out of fuel, or an abort.
//| Example: `local res is ascend(lexicon("target_inc", 0)). addons:claudia:report(res).`

// The block's own options and defaults (target_alt "auto" is resolved from the body's terrain).
function ascend_defaults {
  return lexicon("target_alt", "auto", "target_inc", 0).
}

// ---- Pure helpers ----

// "" or why target_inc isn't one the block can use (target_alt is checked once it is resolved).
function ascend_unreadable {
  parameter opt.
  if not opt:target_inc:istype("Scalar") {
    return "target_inc must be degrees from 0 to 180".
  }
  if opt:target_inc < 0 or opt:target_inc > 180 {
    return "target_inc must be degrees from 0 to 180".
  }
  return "".
}

// target_alt resolved: "auto" is 5 km above the highest terrain; anything else is left as it is.
function ascend_target_alt {
  parameter target_alt, highest.
  if target_alt:istype("String") and target_alt = "auto" {
    return highest + 5000.
  }
  return target_alt.
}

// "" or why a (resolved) target_alt can't be flown to: it must be metres, above the highest
// terrain and below the edge of the sphere of influence (soi_alt, m above sea level).
function ascend_check_alt {
  parameter target_alt, highest, soi_alt, body_name.
  if not target_alt:istype("Scalar") {
    return "target_alt must be " + char(34) + "auto" + char(34) + " or metres, not '" + target_alt + "'".
  }
  if target_alt <= highest {
    return "target_alt " + round(target_alt) + " m is not above the highest terrain, " + round(highest) + " m".
  }
  if target_alt >= soi_alt {
    return "target_alt " + round(target_alt) + " m is outside the " + body_name + "'s sphere of influence".
  }
  return "".
}

// The compass heading (degrees) to fly for inclination inc from latitude lat, given the orbital
// speed to reach (m/s) and the ground's own eastward speed there (m/s). Heads north of east for a
// prograde orbit (the ascending node).
function ascend_azimuth {
  parameter inc, lat, v_orbit, v_ground.
  local inertial is arcsin(max(-1, min(1, cos(inc) / cos(lat)))).
  local east is v_orbit * sin(inertial) - v_ground.
  local v_north is v_orbit * cos(inertial).
  local az is arctan2(east, v_north).
  if az < 0 {
    set az to az + 360.
  }
  return az.
}

// The vertical speed (m/s) to climb at: half of what would coast the craft up to target_alt from
// alt_now under gravity less centrifugal lift, g_eff (m/s^2), so most of the thrust goes into
// horizontal speed and apoapsis reaches target_alt mostly through that (as g_eff falls).
function ascend_vv_target {
  parameter alt_now, target_alt, g_eff.
  return 0.5 * sqrt(2 * max(0.01, g_eff) * max(0, target_alt - alt_now)).
}

// Pitch above the horizon (degrees) that holds vertical speed vv at vv_want with full acceleration
// accel (m/s^2) against g_eff.
function ascend_pitch {
  parameter accel, g_eff, vv, vv_want.
  if accel <= 0 {
    return 90.
  }
  return arcsin(max(0, min(1, (g_eff + 0.2 * (vv_want - vv)) / accel))).
}

// Prograde delta-v (m/s) at apoapsis radius r_ap to circularize an orbit of semi-major axis sma.
function ascend_circularize_dv {
  parameter mu, r_ap, sma.
  return sqrt(mu / r_ap) - sqrt(mu * (2 / r_ap - 1 / sma)).
}

// ---- The block ----

function ascend {
  parameter options is lexicon().
  local extra is lexicon("ap_error", 0).
  local ctx is claudia_begin("ascend", options, ascend_defaults(), 900).
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is ascend_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if body:atm:exists {
    return claudia_finish(ctx, false, "ascend flies from airless bodies only; " + body:name + " has an atmosphere (use launch)", extra).
  }
  if not claudia_on_ground() {
    return claudia_finish(ctx, false, "ascend starts on the ground, not " + status, extra).
  }
  local highest is addons:claudia:highestterrain(body).
  local goal is ascend_target_alt(opt:target_alt, highest).
  local bad_alt is ascend_check_alt(goal, highest, body:soiradius - body:radius, body:name).
  if bad_alt <> "" {
    return claudia_finish(ctx, false, bad_alt, extra).
  }
  set opt:target_alt to goal.

  // A craft whose engines aren't lit yet stages into them (within stage_limit).
  if ship:availablethrust <= 0 {
    local staged is claudia_autostage(ctx).
    if staged <> "" {
      return claudia_finish(ctx, false, staged, extra).
    }
  }
  if ship:availablethrust <= 0 {
    return claudia_finish(ctx, false, "no thrust", extra).
  }
  local mu is body:mu.
  local g0 is mu / (body:radius + altitude) ^ 2.
  local twr is ship:availablethrust / ship:mass / g0.
  if twr < 1.05 {
    return claudia_finish(ctx, false, "thrust-to-weight " + round(twr, 2) + " is too low to lift off", extra).
  }

  local v_orbit is sqrt(mu / (body:radius + goal)).
  local v_ground is 2 * constant:pi * body:radius * cos(ship:geoposition:lat) / body:rotationperiod.
  local az is ascend_azimuth(opt:target_inc, ship:geoposition:lat, v_orbit, v_ground).
  local pitch is 90.
  local tset is 1.
  sas off.
  lock steering to heading(az, pitch).
  lock throttle to tset.

  until ship:apoapsis >= goal {
    local radius_now is body:radius + altitude.
    local v_h is vxcl(ship:up:vector, ship:velocity:orbit):mag.
    local g_eff is mu / radius_now ^ 2 - v_h ^ 2 / radius_now.
    local accel is ship:availablethrust / max(0.001, ship:mass).
    if alt:radar < 100 {
      // Clear the ground first.
      set pitch to 90.
    } else {
      set pitch to ascend_pitch(accel, g_eff, ship:verticalspeed, ascend_vv_target(altitude, goal, g_eff)).
    }
    // Ease off as apoapsis nears the target.
    set tset to min(1, max(0.05, (goal - ship:apoapsis) / 2000)).
    local ticked is claudia_tick(ctx, tset).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    wait 0.
  }
  set tset to 0.
  set ship:control:pilotmainthrottle to 0.
  unlock throttle.
  unlock steering.

  // Circularize at apoapsis.
  local nd is node(time:seconds + eta:apoapsis, 0, 0, ascend_circularize_dv(mu, body:radius + ship:apoapsis, ship:orbit:semimajoraxis)).
  add nd.
  local flown is claudia_fly_node(ctx, nd).
  set extra:ap_error to round(ship:apoapsis - goal).
  if flown <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, flown, extra).
  }
  if ship:periapsis <= highest {
    return claudia_finish(ctx, false, "periapsis " + round(ship:periapsis) + " m is still under the highest terrain, " + round(highest) + " m", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
