// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block return: flies home from orbit around a moon of Kerbin to a Kerbin periapsis in the
// atmosphere. Built on claudia_lib.ks, exec_node.ks and transfer_to.ks's patch, descent and mid-course
// correction helpers; block-only helpers are prefixed return_. kOS lets a function be named return
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
//|   correction [true]: false leaves out the mid-course correction.
//|   max_duration ["auto"]: the time to the predicted reentry plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: kerbin_pe (m, the predicted Kerbin periapsis), reentry_ut (UT of the predicted
//| entry into Kerbin's atmosphere, or of periapsis if that is above it).
//| The correction is skipped when the prediction is already within 1 km of target_pe or the fix is
//| under 0.1 m/s. When leaving the moon's sphere of influence comes before halfway, the block warps
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
  return lexicon("target_pe", 35000, "window", "next", "correction", true).
}

// "" or why an option value isn't one the block can use.
function return_unreadable {
  parameter opt.
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
  local pe_ut is transfer_to_rsvp():time_at_periapsis(orb).
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
    local pos is positionat(ship, t) - positionat(moon, t).
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
  local extra is lexicon("kerbin_pe", -1, "reentry_ut", -1).
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

  local earliest is transfer_to_earliest(time:seconds, opt:window).
  // The burn point comes round once an orbit.
  local latest is earliest + ship:orbit:period.

  // Seed the burn from the patched-conic sums, then let the predicted path settle time and size.
  local seed is return_ejection(planet:mu, moon:orbit:semimajoraxis, planet:radius + opt:target_pe, moon:mu, ship:orbit:semimajoraxis).
  local seed_ut is return_ejection_time(moon, seed:eject_angle, earliest, latest).
  local nd is node(seed_ut, 0, 0, seed:dv).
  add nd.
  local found is transfer_to_descend({
    parameter x.
    if x[0] < earliest or x[0] > latest {
      return 1e13.
    }
    set nd:eta to x[0] - time:seconds.
    set nd:prograde to x[1].
    return abs(transfer_to_pe_error(nd:orbit, planet, opt:target_pe)).
  }, list(seed_ut, seed:dv), list(10, 2), 10, 100).
  set nd:eta to found:x[0] - time:seconds.
  set nd:prograde to found:x[1].
  local planned is transfer_to_patch(nd:orbit, planet).
  if planned:istype("String") {
    remove nd.
    return claudia_finish(ctx, false, "found no ejection that returns to Kerbin", extra).
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to return_reentry_ut(planned:orbit) - ctx:start_ut + 900.
  }

  local ejected is transfer_to_burn(ctx, nd).
  if not ejected:success {
    return claudia_finish(ctx, false, ejected:reason, extra).
  }
  local home is transfer_to_patch(ship:orbit, planet).
  if home:istype("String") {
    return claudia_finish(ctx, false, "no return to Kerbin after the ejection burn", extra).
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    local fixed is transfer_to_correct(ctx, planet, opt:target_pe, return_reentry_ut(home:orbit)).
    if fixed:reason <> "" {
      return claudia_finish(ctx, false, fixed:reason, extra).
    }
    set home to transfer_to_patch(ship:orbit, planet).
    if home:istype("String") {
      return claudia_finish(ctx, false, "no return to Kerbin after the correction", extra).
    }
  }
  set extra:kerbin_pe to round(home:orbit:periapsis).
  set extra:reentry_ut to round(return_reentry_ut(home:orbit)).
  return claudia_finish(ctx, true, "", extra).
}
