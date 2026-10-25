// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block land: lands on an airless body from a low, near-circular orbit. Built on
// claudia_lib.ks and exec_node.ks (which flies its deorbit and correction burns); block-only helpers
// are prefixed land_. The "//|" lines are Claudia's docs for it; the first is its summary.
//
// The descent: a deorbit burn half an orbit before the site lowers periapsis to a few km above it;
// a correction burn on the way down swings the orbit plane over the site; then, from the stopping
// distance (times burn_margin), a powered descent steers along a velocity field that brakes onto
// the site while gliding down above the local terrain, checks the slope near the site (diverting
// to flatter ground through ADDONS:CLAUDIA:FLATSPOT) and touches down at touchdown_speed. No
// Trajectories: an airless body's fall is plain orbit maths.

@lazyglobal off.

//| land(options): lands on an airless body (the Mun, Minmus...) from a low, near-circular orbit:
//| a deorbit burn timed for the site, a correction burn that puts the ground track over it, and a
//| powered descent that brakes onto it, rejects slopes over max_slope (diverting up to max_divert to
//| flatter ground) and touches down at about touchdown_speed. Aims for about 5 km of the site.
//| Options:
//|   target ["any"]: "any", LATLNG(lat, lng) or lexicon("lat", x, "lng", y) on the body orbited.
//|     "any" lands about half an orbit on. A site must pass within 20 km of the ground track in the
//|     next 3 orbits; plan a plane change first otherwise.
//|   daylight [true]: set it from the Mission Setting Daylight landing. true touches down only with
//|     the sun at least 5 degrees up at the site. It looks up to 3 orbits ahead for such a pass but
//|     never waits past the wait cap (one orbit unless the player raised it); with no pass in reach
//|     it fails (plan a Due Time for the site's day).
//|   max_slope [8]: degrees; steeper ground is rejected during final descent.
//|   max_divert [300]: m it may move off the site to find ground within max_slope.
//|   touchdown_speed [2]: m/s at touchdown.
//|   gear_alt [1000]: m above the ground to lower the landing gear.
//|   burn_margin [1.10]: braking starts this much farther out than the stopping distance needs.
//|   max_duration ["auto"]: the wait to the deorbit burn plus 2400 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: slope (degrees where it came to rest), touchdown_speed_actual (m/s),
//| distance_to_target (m from the site asked for, or the spot chosen for "any"), best_effort
//| (true if nothing within max_divert was flat enough and it landed on the least steep spot
//| found); geo (lat, lng, alt) is the common field once landed. Fails with "land flies airless
//| bodies only", an orbit it can't land from, a site off the ground track, no daylight pass, a wait
//| over the cap, not enough delta-v, "tipped over", out of fuel, or an abort; a failure in the
//| descent leaves the craft safe but falling, so give it the delta-v (and TWR over 1.5) it needs.
//| Example: `local res is land(lexicon("target", latlng(-0.5, 22), "daylight", true)). addons:claudia:report(res).`

// The block's own options and defaults.
function land_defaults {
  return lexicon("target", "any", "daylight", true, "max_slope", 8, "max_divert", 300,
    "touchdown_speed", 2, "gear_alt", 1000, "burn_margin", 1.10).
}

// "" or why an option's value isn't one the block can use.
function land_unreadable {
  parameter opt.
  local t is opt:target.
  local target_rule is "target must be " + char(34) + "any" + char(34) + ", LATLNG(lat, lng) or lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y), not ".
  if t:istype("String") {
    if t <> "any" {
      return target_rule + "'" + t + "'".
    }
  } else if t:istype("Lexicon") {
    if not (t:haskey("lat") and t:haskey("lng")) {
      return target_rule + "a lexicon without lat and lng".
    }
    if not (t:lat:istype("Scalar") and t:lng:istype("Scalar")) {
      return target_rule + "a lexicon whose lat and lng aren't numbers".
    }
  } else if not t:istype("GeoCoordinates") {
    return target_rule + "a " + t:typename.
  }
  if not opt:daylight:istype("Boolean") {
    return "daylight must be true or false".
  }
  if not land_number_in(opt:max_slope, 0, 45) {
    return "max_slope must be a number of degrees from 0 to 45".
  }
  if not land_number_in(opt:max_divert, 0, 2000) {
    return "max_divert must be metres from 0 to 2000".
  }
  if not land_number_in(opt:touchdown_speed, 0.01, 50) {
    return "touchdown_speed must be a speed above 0 m/s".
  }
  if not land_number_in(opt:gear_alt, 0, 100000) {
    return "gear_alt must be metres, 0 or more".
  }
  if not land_number_in(opt:burn_margin, 1, 10) {
    return "burn_margin must be a number, 1 or more".
  }
  return "".
}

// True if x is a number from low to high.
function land_number_in {
  parameter x, low, high.
  if not x:istype("Scalar") {
    return false.
  }
  return x >= low and x <= high.
}

// ---- Pure helpers: geometry and orbits ----

// A longitude in (-180, 180].
function land_wrap_lng {
  parameter lng.
  local wrapped is mod(mod(lng + 180, 360) + 360, 360) - 180.
  if wrapped = -180 {
    return 180.
  }
  return wrapped.
}

// Metres along the surface between two spots (degrees), on a body of the given radius.
function land_surface_distance {
  parameter lat1, lng1, lat2, lng2, radius.
  local a is sin((lat2 - lat1) / 2) ^ 2 + cos(lat1) * cos(lat2) * sin((lng2 - lng1) / 2) ^ 2.
  // Literal pi rather than CONSTANT:PI, so this stays pure.
  return 2 * radius * arcsin(min(1, sqrt(a))) * 3.141592653589793 / 180.
}

// The longitude, in today's body-fixed frame, of the point a site at lng will have turned to in dt
// seconds: bodies turn east, so after dt the site is where the point east of it is now.
function land_site_lng {
  parameter lng, dt, rotation_period.
  return land_wrap_lng(lng + 360 * dt / rotation_period).
}

// The prograde delta-v (m/s, negative) at radius r0 (m), moving at speed (m/s) horizontally,
// that lowers periapsis to radius r_pe.
function land_deorbit_dv {
  parameter mu, r0, speed, r_pe.
  return sqrt(mu * (2 / r0 - 2 / (r0 + r_pe))) - speed.
}

// Seconds from the deorbit burn at radius r0 to periapsis at r_pe: half the transfer orbit.
function land_transfer_time {
  parameter mu, r0, r_pe.
  return 3.141592653589793 * sqrt(((r0 + r_pe) / 2) ^ 3 / mu).
}

// Speed (m/s) at periapsis r_pe of the transfer from r0.
function land_periapsis_speed {
  parameter mu, r0, r_pe.
  return sqrt(mu * (2 / r_pe - 2 / (r0 + r_pe))).
}

// ---- Pure helpers: descent guidance ----

// Metres needed to stop from speed (m/s) at accel (m/s^2), times margin.
function land_stop_distance {
  parameter speed, accel, margin.
  return speed ^ 2 / (2 * accel) * margin.
}

// Horizontal speed (m/s) to close on a site dist m away: the speed it can still stop from at
// accel / margin, and no more than half the distance per second near the site.
function land_vh_target {
  parameter dist, accel, margin.
  return min(sqrt(2 * accel / margin * max(0, dist)), 0.5 * max(0, dist)).
}

// The height (m above the terrain under the craft) to hold dist m from the site: a 15% glide slope
// no higher than where braking began (h_start), levelling at 100 m until within 30 m of the site,
// then down.
function land_approach_height {
  parameter dist, h_start.
  if dist < 30 {
    return 0.
  }
  return min(h_start, max(100, 0.15 * dist)).
}

// Vertical speed (m/s, up positive) at h m above the terrain, aiming for h_ref: climbs back (up to
// 20 m/s) when below it, and descends at a tenth of the height to lose per second when above,
// never slower than touchdown speed and never faster than it can stop from at a_stop (m/s^2).
function land_vv_target {
  parameter h, h_ref, a_stop, touchdown.
  if h < h_ref {
    return min(20, 0.1 * (h_ref - h)).
  }
  local fastest is max(touchdown, sqrt(2 * a_stop * max(0, h))).
  return -min(fastest, max(touchdown, 0.1 * (h - h_ref))).
}

// The most the thrust may lean from vertical (degrees) at h m above the ground: upright for the
// last few metres, free from about 40 m up.
function land_max_tilt {
  parameter h.
  return min(90, 5 + 2 * max(0, h)).
}

// Splits max_accel between the vertical acceleration wanted (first, 0 to max_accel) and the
// horizontal (what is left). Returns lexicon("v", m/s^2, "h", m/s^2).
function land_split_accel {
  parameter max_accel, vert, horiz.
  local a_v is min(max_accel, max(0, vert)).
  local a_h is min(max(0, horiz), sqrt(max(0, max_accel ^ 2 - a_v ^ 2))).
  return lexicon("v", a_v, "h", a_h).
}

// ---- Planning (reads the ship and the clock) ----

// The site's lat and lng from the target option, or "" for "any".
function land_site_of {
  parameter target_opt.
  if target_opt:istype("String") {
    return "".
  }
  return lexicon("lat", target_opt:lat, "lng", target_opt:lng).
}

// Degrees the sun stands above the horizon at a point (a vector from the body's centre) at ut.
function land_sun_elevation {
  parameter point, ut.
  local sun_dir is positionat(body("Sun"), ut) - positionat(body, ut).
  return 90 - vang(point, sun_dir).
}

// Where the site (lat, lng) will be at ut, as a vector from the body's centre in today's frame.
function land_site_vector {
  parameter site, ut.
  local spot is body:geopositionlatlng(site:lat, land_site_lng(site:lng, ut - time:seconds, body:rotationperiod)).
  return spot:position - body:position.
}

// The periapsis radius to aim for: approach m above the site's terrain; -1 if the orbit (semi-major
// axis sma) isn't at least 1 km above that.
function land_pe_radius {
  parameter site, approach, sma.
  local r_pe is body:radius + max(0, body:geopositionlatlng(site:lat, site:lng):terrainheight) + approach.
  if r_pe > sma - 1000 {
    return -1.
  }
  return r_pe.
}

// land_plan's failure when the orbit is too low to start the descent from over the site.
function land_too_low {
  parameter site, approach.
  local lowest is max(0, body:geopositionlatlng(site:lat, site:lng):terrainheight) + approach + 1000.
  return lexicon("error", "orbit too low to land from: it must be at least " + round(lowest) + " m up, 1 km over the descent's start").
}

// When to burn to land at the site (or anywhere, for site ""): lexicon("error", "" or why not,
// "burn_ut", "arrive_ut", "site" (lat, lng), "r_pe"). Looks up to 3 orbits ahead.
function land_plan {
  parameter opt, site, approach.
  local mu is body:mu.
  local sma is ship:orbit:semimajoraxis.
  local period is ship:orbit:period.
  local earliest is time:seconds + 120.
  local step is period / 360.
  local steps is ceiling(3 * period / step).

  if site:istype("String") {
    // Anywhere: the first burn whose arrival point (opposite the burn) is in daylight if wanted.
    for i in range(steps) {
      local burn_ut is earliest + i * step.
      local here is positionat(ship, burn_ut) - positionat(body, burn_ut).
      local arrive_ut is burn_ut + land_transfer_time(mu, here:mag, body:radius + approach).
      local arrival is -here:normalized * body:radius.
      if not opt:daylight or land_sun_elevation(arrival, arrive_ut) >= 5 {
        local now_geo is body:geopositionof(body:position + arrival).
        local lng is land_site_lng(now_geo:lng, -(arrive_ut - time:seconds), body:rotationperiod).
        local chosen is lexicon("lat", now_geo:lat, "lng", lng).
        local chosen_pe is land_pe_radius(chosen, approach, sma).
        if chosen_pe < 0 {
          return land_too_low(chosen, approach).
        }
        return lexicon("error", "", "burn_ut", burn_ut, "arrive_ut", burn_ut + land_transfer_time(mu, here:mag, chosen_pe), "site", chosen, "r_pe", chosen_pe).
      }
    }
    return lexicon("error", "no daylight touchdown in the next 3 orbits: plan a Due Time").
  }

  local r_pe is land_pe_radius(site, approach, sma).
  if r_pe < 0 {
    return land_too_low(site, approach).
  }
  local fall_s is land_transfer_time(mu, sma, r_pe).
  local normal is vcrs(ship:position - body:position, ship:velocity:orbit):normalized.
  // The angle at each step between the arrival point (opposite the burn) and the site then.
  local best_cross is 1e9.
  local dark is false.
  local prev2 is 1e9.
  local prev is 1e9.
  for i in range(steps + 1) {
    local burn_ut is earliest + i * step.
    local here is positionat(ship, burn_ut) - positionat(body, burn_ut).
    local there is land_site_vector(site, burn_ut + fall_s).
    local apart is vang(-here, there).
    // A pass: the previous step was closest.
    if i >= 2 and prev < prev2 and prev <= apart {
      local pass_ut is burn_ut - step.
      local site_then is land_site_vector(site, pass_ut + fall_s).
      local cross is abs(90 - vang(normal, site_then)) * constant:pi / 180 * body:radius.
      set best_cross to min(best_cross, cross).
      if cross <= 20000 {
        if not opt:daylight or land_sun_elevation(site_then, pass_ut + fall_s) >= 5 {
          return lexicon("error", "", "burn_ut", pass_ut, "arrive_ut", pass_ut + fall_s, "site", site, "r_pe", r_pe).
        }
        set dark to true.
      }
    }
    set prev2 to prev.
    set prev to apart.
  }
  if dark {
    return lexicon("error", "no daylight pass over the site in the next 3 orbits: plan a Due Time for the site's day").
  }
  if best_cross < 1e9 {
    return lexicon("error", "the site is " + round(best_cross / 1000, 1) + " km off the ground track, over 20 km: plan a plane change first").
  }
  return lexicon("error", "no pass over the site in the next 3 orbits").
}

// Adds a node at ut whose burn is the vector dv (m/s, raw frame), choosing the radial and normal
// signs whose burn matches it best. Returns the node.
function land_node_for {
  parameter ut, dv.
  local vel is velocityat(ship, ut):orbit.
  local pos is positionat(ship, ut) - positionat(body, ut).
  local pro is vel:normalized.
  local rad is vxcl(pro, pos):normalized.
  local nrm is vcrs(pro, rad):normalized.
  local along is vdot(dv, pro).
  local out is vdot(dv, rad).
  local across is vdot(dv, nrm).
  local best is 0.
  local best_angle is 1000.
  for signs in list(list(1, 1), list(1, -1), list(-1, 1), list(-1, -1)) {
    local trial is node(ut, signs[0] * out, signs[1] * across, along).
    add trial.
    local off is vang(trial:deltav, dv).
    remove trial.
    if off < best_angle {
      set best_angle to off.
      set best to signs.
    }
  }
  local chosen is node(ut, best[0] * out, best[1] * across, along).
  add chosen.
  return chosen.
}

// ---- The block ----

function land {
  parameter options is lexicon().
  local extra is lexicon("slope", -1, "touchdown_speed_actual", -1, "distance_to_target", -1, "best_effort", false).
  local ctx is claudia_begin("land", options, land_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is land_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if opt:target:istype("GeoCoordinates") {
    if opt:target:body:name <> body:name {
      return claudia_finish(ctx, false, "target is on " + opt:target:body:name + ", not " + body:name, extra).
    }
  }
  if body:atm:exists {
    return claudia_finish(ctx, false, "land flies airless bodies only; " + body:name + " has an atmosphere", extra).
  }
  if claudia_on_ground() {
    return claudia_finish(ctx, false, "already down (" + status + ")", extra).
  }
  if status <> "ORBITING" or ship:orbit:eccentricity > 0.1 {
    return claudia_finish(ctx, false, "land needs a near-circular orbit (eccentricity under 0.1); situation " + status + ", eccentricity " + round(ship:orbit:eccentricity, 3), extra).
  }

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

  local approach is 3000.
  local plan is land_plan(opt, land_site_of(opt:target), approach).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  local aim is plan:site.
  local mu is body:mu.
  local burn_pos is positionat(ship, plan:burn_ut) - positionat(body, plan:burn_ut).
  local burn_vel is velocityat(ship, plan:burn_ut):orbit.
  local deorbit_dv is land_deorbit_dv(mu, burn_pos:mag, burn_vel:mag, plan:r_pe).
  local needed is abs(deorbit_dv) + 1.2 * land_periapsis_speed(mu, burn_pos:mag, plan:r_pe) + 100.
  if ship:deltav:current > 0 and ship:deltav:current < needed {
    return claudia_finish(ctx, false, "not enough delta-v: about " + round(needed) + " m/s needed, " + round(ship:deltav:current) + " m/s left", extra).
  }
  local wait_s is plan:burn_ut - time:seconds - 60.
  if opt:max_duration:istype("String") {
    set opt:max_duration to max(0, wait_s) + 2400.
  }
  local waiting is claudia_check_wait(wait_s).
  if waiting <> "" {
    return claudia_finish(ctx, false, waiting, extra).
  }
  sas off.

  // Deorbit: retrograde, half an orbit before the site.
  local deorbit is node(plan:burn_ut, 0, 0, deorbit_dv).
  add deorbit.
  local flown is claudia_fly_node(ctx, deorbit).
  if flown <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, flown, extra).
  }

  // Correction, a quarter orbit before the site: turn the plane about the craft's position so it
  // holds the site at periapsis.
  local pe_ut is time:seconds + eta:periapsis.
  local fix_ut is time:seconds + eta:periapsis / 2.
  if fix_ut > time:seconds + 90 {
    local fix_pos is positionat(ship, fix_ut) - positionat(body, fix_ut).
    local fix_vel is velocityat(ship, fix_ut):orbit.
    local up_dir is fix_pos:normalized.
    local plane is vcrs(fix_pos, land_site_vector(aim, pe_ut)):normalized.
    if vdot(plane, vcrs(fix_pos, fix_vel)) < 0 {
      set plane to -plane.
    }
    local radial is vdot(fix_vel, up_dir) * up_dir.
    local wanted is radial + vxcl(up_dir, fix_vel):mag * vcrs(plane, up_dir):normalized.
    local fix_dv is wanted - fix_vel.
    if fix_dv:mag > 0.5 {
      local fix is land_node_for(fix_ut, fix_dv).
      set flown to claudia_fly_node(ctx, fix).
      if flown <> "" {
        claudia_make_safe().
        return claudia_finish(ctx, false, flown, extra).
      }
    }
  }

  // Coast (on rails) to a minute or so before braking starts.
  local a_max is ship:availablethrust / ship:mass.
  local g_pe is mu / plan:r_pe ^ 2.
  local a_brake is sqrt(max(0.01, a_max ^ 2 - g_pe ^ 2)).
  local v_pe is land_periapsis_speed(mu, ship:orbit:apoapsis + body:radius, ship:orbit:periapsis + body:radius).
  local brake_ut is time:seconds + eta:periapsis - v_pe * opt:burn_margin / (2 * a_brake).
  if brake_ut - 90 > time:seconds {
    local coasted is claudia_wait_until(ctx, brake_ut - 90).
    if coasted <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, coasted, extra).
    }
  }

  local landed is land_descend(ctx, aim, extra).
  if landed <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, landed, extra).
  }
  set extra:distance_to_target to round(land_surface_distance(aim:lat, aim:lng, ship:geoposition:lat, ship:geoposition:lng, body:radius)).
  set extra:slope to round(addons:claudia:slope(ship:geoposition), 1).
  if vang(ship:facing:forevector, ship:up:vector) > 30 {
    return claudia_finish(ctx, false, "tipped over on landing", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}

// The powered descent onto aim, to touchdown and a settled craft. Sets best_effort and
// touchdown_speed_actual in extra. Returns "" or why it failed.
function land_descend {
  parameter ctx, aim, extra.
  local opt is ctx:opt.
  local site is lexicon("lat", aim:lat, "lng", aim:lng).
  local checked is false.
  local braking is false.
  local h_start is 0.
  local steer is -ship:velocity:surface.
  local tset is 0.
  lock steering to steer.
  lock throttle to tset.
  local last_speed is ship:velocity:surface:mag.

  until status = "LANDED" or status = "SPLASHED" {
    local up_v is ship:up:vector.
    local to_site is vxcl(up_v, body:geopositionlatlng(site:lat, site:lng):position).
    local dist is to_site:mag.
    local vel is ship:velocity:surface.
    local v_v is vdot(vel, up_v).
    local v_h is vxcl(up_v, vel).
    local h is alt:radar.
    local a_max is ship:availablethrust / max(0.001, ship:mass).
    local radius_now is body:radius + altitude.
    local g is body:mu / radius_now ^ 2.
    local a_brake is sqrt(max(0.01, a_max ^ 2 - g ^ 2)).
    set last_speed to vel:mag.

    if not braking {
      // Brake at the stopping distance, if the site falls behind, or if the ground comes up first.
      local falling_for is h / max(1, -v_v).
      if dist <= land_stop_distance(v_h:mag, a_brake, opt:burn_margin) or vdot(v_h, to_site) < 0 or falling_for < 40 or h < 1500 {
        set braking to true.
        set h_start to max(500, h).
      }
      set steer to -vel.
      set tset to 0.
    }
    if braking {
      if not checked and dist < 2000 {
        // Final descent: is the ground at the site flat enough? If not, divert to flatter ground.
        local spot is addons:claudia:flatspot(body:geopositionlatlng(site:lat, site:lng), opt:max_divert, opt:max_slope).
        set site to lexicon("lat", spot:lat, "lng", spot:lng).
        set extra:best_effort to not spot:flat.
        set checked to true.
      }
      local vh_want is v(0, 0, 0).
      if dist > 0.1 {
        set vh_want to to_site:normalized * land_vh_target(dist, a_brake, opt:burn_margin).
      }
      local a_stop is max(0.2, 0.5 * (a_max - g)).
      local vv_want is land_vv_target(h, land_approach_height(dist, h_start), a_stop, opt:touchdown_speed).
      local g_eff is g - v_h:sqrmagnitude / radius_now.
      local h_err is (vh_want - v_h) * 0.4.
      local split is land_split_accel(0.95 * a_max, g_eff + 0.5 * (vv_want - v_v), h_err:mag).
      local accel is up_v * split:v.
      if h_err:mag > 0 {
        set accel to accel + h_err:normalized * split:h.
      }
      local tilt is land_max_tilt(h).
      if vang(accel, up_v) > tilt {
        local side is vxcl(up_v, accel):normalized.
        set accel to (up_v * cos(tilt) + side * sin(tilt)) * (split:v / max(0.01, cos(tilt))).
      }
      if accel:mag > 0.001 {
        set steer to accel.
      } else {
        set steer to up_v.
      }
      set tset to min(1, accel:mag / max(0.001, a_max)) * max(0, cos(vang(ship:facing:forevector, steer))).
      if h < opt:gear_alt and not gear {
        gear on.
      }
    }
    local ticked is claudia_tick(ctx, tset).
    if ticked <> "" {
      return ticked.
    }
    wait 0.
  }
  set tset to 0.
  set ship:control:pilotmainthrottle to 0.
  set extra:touchdown_speed_actual to round(last_speed, 2).
  unlock throttle.
  // Hold upright while it settles on its legs, then leave it safe.
  set steer to ship:up:vector.
  local settle_ut is time:seconds + 5.
  until time:seconds > settle_ut {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      return ticked.
    }
    wait 0.
  }
  claudia_make_safe().
  return "".
}
