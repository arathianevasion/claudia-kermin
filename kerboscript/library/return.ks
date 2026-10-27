// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block return: flies home from orbit around a moon of Kerbin to a Kerbin periapsis in the
// atmosphere. Built on claudia_lib.ks (its patch, descent and mid-course correction helpers) and
// exec_node.ks; block-only helpers are prefixed return_. kOS lets a function be named return
// when it is called as a value. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| return(options): flies home from orbit around a moon of Kerbin (the Mun or Minmus): an ejection
//| burn onto a path whose Kerbin periapsis is target_pe, then a mid-course correction halfway to
//| reentry, both flown with exec_node. It ends after the correction, still in space; the craft comes
//| down anywhere on Kerbin. Call it as a value, `local res is return().`: a bare `return().` is kOS's
//| own return statement.
//| Options:
//|   target_pe [35000]: the Kerbin periapsis altitude to aim for, m; inside the atmosphere (70 km)
//|     the craft comes down.
//|   window ["next"]: "next" ejects at the burn point's next pass, in the craft's next orbit from 5
//|     minutes ahead; a time (UT seconds) ejects at its first pass from that time. The block warps
//|     on rails to the pass however far off it is.
//|   target ["none"]: LATLNG(lat, lng), lexicon("lat", x, "lng", y) or "ksc": where to come down. The
//|     mid-course correction then aims the landing point the mod's reentry predictor gives (the craft's
//|     own drag, held retrograde as reentry flies it) at the target, with target_pe only drawing a path
//|     that misses the air towards it; the ejection burn is unchanged. "none" aims for target_pe only.
//|     The landing is hard to steer from here: read predicted_error_km, and when it is too far for the
//|     Mission Goal, capture at Kerbin and call deorbit instead. Aim a splashdown offshore.
//|   correction [true]: false leaves out the mid-course correction.
//|   max_duration ["auto"]: the time to the predicted reentry plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: kerbin_pe (m, the predicted Kerbin periapsis), reentry_ut (UT of the predicted
//| entry into Kerbin's atmosphere, or of periapsis if that is above it), predicted_geo (lexicon lat,
//| lng: where the craft is predicted to come down, from the path it is on after the correction, with
//| the prediction leaving out parachutes and lift; empty without a target or a prediction) and
//| predicted_error_km (km from the target to predicted_geo; -1 without one).
//| The correction is skipped when the prediction is already within 1 km of target_pe (with a target:
//| within 2 km of it) or the fix is under 0.1 m/s. When leaving the moon's sphere of influence comes before halfway, the block warps
//| through that first, however long it takes.
//| Fails with an option value it can't read, "not in orbit", "already orbiting Kerbin: lower the
//| periapsis with exec_node instead", "return starts from orbit around a moon of Kerbin, not <body>",
//| "a maneuver node is already planned: remove it first", "found no ejection that returns to
//| Kerbin", "no return to Kerbin after the ejection burn" (or "after the correction"), an exec_node
//| failure (that burn's node is left in place), or an abort.
//| Follow it with reentry, in the same sequence or the next (no Due Time needed): once the craft is
//| in Kerbin's sphere of influence (after the correction it usually is; if ship:body is still the
//| moon, coast until ship:orbit:nextpatcheta has passed), reentry warps to the atmosphere itself.
//| Example: `local res is return(). addons:claudia:report(res).`

// The block's own options and defaults.
function return_defaults {
  return lexicon("target_pe", 35000, "window", "next", "correction", true, "target", "none").
}

// Whether the Mission Script gave a landing target.
function return_aims {
  parameter opt.
  return not (opt:target:istype("String") and opt:target = "none").
}

// What return asks ADDONS:CLAUDIA:REENTRYIMPACT: where the path orb round Kerbin comes down, and how
// far that is from site (lat, lng) whose ground is ground_alt m (sea level for water). The path begins
// at from_ut (the craft may not be on it yet), so the fall is taken from there. Pure.
function return_impact_query {
  parameter orb, from_ut, site, ground_alt.
  return lexicon("lat", site:lat, "lng", site:lng, "ground_alt", max(0, ground_alt), "from_ut", from_ut,
    "orbit_sma", orb:semimajoraxis, "orbit_ecc", orb:eccentricity, "orbit_inc", orb:inclination,
    "orbit_lan", orb:lan, "orbit_argpe", orb:argumentofperiapsis, "orbit_mna", orb:meananomalyatepoch,
    "orbit_epoch", orb:epoch, "orbit_body", orb:body:name).
}

// "" or why an option value isn't one the block can use.
function return_unreadable {
  parameter opt.
  local pe is claudia_pe_unreadable(opt:target_pe).
  if pe <> "" {
    return pe.
  }
  if return_aims(opt) {
    local bad_target is claudia_target_unreadable(opt:target).
    if bad_target <> "" {
      return bad_target.
    }
  }
  local window is claudia_window_unreadable(opt:window).
  if window <> "" {
    return window.
  }
  return claudia_correction_unreadable(opt:correction).
}

// The ejection from a circular orbit of radius r0 (m) around a moon (gravitational parameter
// mu_moon) that itself orbits its planet (mu_parent) at moon_sma (m), onto a path whose periapsis
// radius at the planet is pe_radius (m). Leaving the moon backwards along its orbit at v_inf (m/s)
// puts the craft at the apoapsis of that path. Returns lexicon("v_inf", m/s, "dv", m/s prograde
// burn, "eject_angle", degrees the burn point is short of the escape direction, along the orbit).
function return_ejection {
  parameter mu_parent, moon_sma, pe_radius, mu_moon, r0.
  local v_moon is sqrt(mu_parent / moon_sma).
  local v_apoapsis is sqrt(mu_parent * 2 * pe_radius / (moon_sma * (moon_sma + pe_radius))).
  local v_inf is max(0, v_moon - v_apoapsis).
  local v_burn is sqrt(v_inf ^ 2 + 2 * mu_moon / r0).
  local ecc is 1 + r0 * v_inf ^ 2 / mu_moon.
  return lexicon("v_inf", v_inf, "dv", v_burn - sqrt(mu_moon / r0), "eject_angle", arccos(-1 / ecc)).
}

// Seconds an orbit (semi-major axis a in m, negative for a hyperbola; eccentricity ecc; body mu)
// takes from radius (m), coming in, down to periapsis; 0 if it never comes down that far.
function return_time_to_pe {
  parameter a, ecc, mu, radius.
  if ecc < 0.000001 or radius <= a * (1 - ecc) {
    return 0.
  }
  local c is (1 - radius / a) / ecc.
  if a > 0 {
    // Kepler's equation with the eccentric anomaly; radius beyond apoapsis counts from apoapsis.
    local big_e is arccos(max(-1, c)).
    local mean is big_e * 3.141592653589793 / 180 - ecc * sin(big_e).
    return mean / sqrt(mu / a ^ 3).
  }
  local sinh_h is sqrt(c ^ 2 - 1).
  local mean is ecc * sinh_h - ln(c + sinh_h).
  return mean / sqrt(mu / abs(a) ^ 3).
}

// UT of the craft entering the atmosphere on the orbit patch orb (or of periapsis, if it stays
// above the atmosphere).
function return_reentry_ut {
  parameter orb.
  local pe_ut is claudia_rsvp():time_at_periapsis(orb).
  local top is orb:body:radius + orb:body:atm:height.
  return pe_ut - return_time_to_pe(orb:semimajoraxis, orb:eccentricity, orb:body:mu, top).
}

// The time between from_ut and to_ut when the craft is closest to eject_angle degrees (along its
// orbit) short of the moon's backwards direction: the burn point for an escape that leaves along it.
function return_ejection_time {
  parameter moon, eject_angle, from_ut, to_ut.
  local best_ut is from_ut.
  local best_off is 1e9.
  local samples is 90.
  for i in range(samples + 1) {
    local t is from_ut + (to_ut - from_ut) * i / samples.
    local pos is positionat(ship, t) - moon:position.
    local vel is velocityat(ship, t):orbit.
    local back is -velocityat(moon, t):orbit.
    local normal is vcrs(pos, vel):normalized.
    local back_in_plane is back - vdot(back, normal) * normal.
    local angle is vang(pos, back_in_plane).
    if vdot(vel, back_in_plane) < 0 {
      set angle to 360 - angle.
    }
    if abs(angle - eject_angle) < best_off {
      set best_off to abs(angle - eject_angle).
      set best_ut to t.
    }
  }
  return best_ut.
}

function return {
  parameter options is lexicon().
  local extra is lexicon("kerbin_pe", -1, "reentry_ut", -1, "predicted_geo", lexicon(), "predicted_error_km", -1).
  local ctx is claudia_begin("return", options, return_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is return_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }

  local moon is ship:body.
  if status <> "ORBITING" {
    return claudia_finish(ctx, false, "not in orbit: return starts from orbit around a moon of Kerbin", extra).
  }
  if moon:name = "Kerbin" {
    return claudia_finish(ctx, false, "already orbiting Kerbin: lower the periapsis with exec_node instead", extra).
  }
  if not moon:hasbody or moon:body:name <> "Kerbin" {
    return claudia_finish(ctx, false, "return starts from orbit around a moon of Kerbin, not " + moon:name, extra).
  }
  local planet is moon:body.
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }

  // With a target, the correction aims the predicted landing at it. The prediction is the mod's reentry
  // predictor, a block-internal call (ADR 0007): inline here, asked of a path round Kerbin that the craft
  // may not be on yet.
  local aim is 0.
  local predict is 0.
  if return_aims(opt) {
    local site is claudia_target_site(opt:target, planet:name).
    if site:error <> "" {
      return claudia_finish(ctx, false, site:error, extra).
    }
    local ground_alt is planet:geopositionlatlng(site:lat, site:lng):terrainheight.
    set predict to {
      parameter orb, from_ut.
      return addons:claudia:reentryimpact(return_impact_query(orb, from_ut, site, ground_alt)).
    }.
    set aim to {
      parameter orb, from_ut.
      local landing is predict(orb, from_ut).
      if landing:error <> "" {
        return -1.
      }
      return landing:error_km.
    }.
  }

  local earliest is claudia_earliest(time:seconds, opt:window).
  // The burn point comes round once an orbit.
  local latest is earliest + ship:orbit:period.

  // Seed the burn from the patched-conic sums, then let the predicted path settle time and size.
  local seed is return_ejection(planet:mu, moon:orbit:semimajoraxis, planet:radius + opt:target_pe, moon:mu, ship:orbit:semimajoraxis).
  local seed_ut is return_ejection_time(moon, seed:eject_angle, earliest, latest).
  local nd is node(seed_ut, 0, 0, seed:dv).
  add nd.
  local found is claudia_descend({
    parameter x.
    if x[0] < earliest or x[0] > latest {
      return 1e13.
    }
    set nd:eta to x[0] - time:seconds.
    set nd:prograde to x[1].
    return abs(claudia_pe_error(nd:orbit, planet, opt:target_pe)).
  }, list(seed_ut, seed:dv), list(10, 2), 10, 100).
  set nd:eta to found:x[0] - time:seconds.
  set nd:prograde to found:x[1].
  local planned is claudia_patch(nd:orbit, planet).
  if planned:istype("String") {
    remove nd.
    return claudia_finish(ctx, false, "found no ejection that returns to Kerbin", extra).
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to return_reentry_ut(planned:orbit) - ctx:start_ut + 900.
  }

  local ejected is claudia_burn(ctx, nd).
  if not ejected:success {
    return claudia_finish(ctx, false, ejected:reason, extra).
  }
  local home is claudia_patch(ship:orbit, planet).
  if home:istype("String") {
    return claudia_finish(ctx, false, "no return to Kerbin after the ejection burn", extra).
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    local fixed is claudia_correct(ctx, planet, opt:target_pe, return_reentry_ut(home:orbit), aim).
    if fixed:reason <> "" {
      return claudia_finish(ctx, false, fixed:reason, extra).
    }
    set home to claudia_patch(ship:orbit, planet).
    if home:istype("String") {
      return claudia_finish(ctx, false, "no return to Kerbin after the correction", extra).
    }
  }
  set extra:kerbin_pe to round(home:orbit:periapsis).
  set extra:reentry_ut to round(return_reentry_ut(home:orbit)).
  if return_aims(opt) {
    // Where the path flown comes down, after the correction's own error.
    local landing is predict(home:orbit, home:soi_ut).
    if landing:error = "" {
      set extra:predicted_geo to claudia_geo(landing).
      set extra:predicted_error_km to round(landing:error_km, 2).
    }
  }
  return claudia_finish(ctx, true, "", extra).
}
