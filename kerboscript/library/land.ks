// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block land: lands on an airless body from a low, near-circular orbit. Built on
// claudia_lib.ks and exec_node.ks (which flies its deorbit and correction burns); block-only helpers
// are prefixed land_. The "//|" lines are Claudia's docs for it; the first is its summary.
//
// The descent: a deorbit burn half an orbit before the site lowers periapsis to a few km above it;
// a correction burn on the way down swings the orbit plane over the site; then, from the stopping
// distance (times burn_margin), a powered descent checks the slope at the site (diverting to
// flatter ground through ADDONS:CLAUDIA:FLATSPOT), then brakes once along the way it was moving
// (land_guide: never turning back unless it stopped over max_overshoot past the site, never faster
// than when braking began) while gliding down above the highest terrain ahead (land_clearance),
// comes straight down once stopped and touches down at touchdown_speed. It traces its descent to the
// log (ADDONS:CLAUDIA:BLOCKTRACE) as it goes. No Trajectories: an airless body's fall is plain orbit maths.

@lazyglobal off.

//| land(options): lands on an airless body (the Mun, Minmus...) from a low, near-circular orbit:
//| a deorbit burn timed for the site, a correction burn that puts the ground track over it, and a
//| powered descent that brakes onto it, rejects slopes over max_slope (diverting up to max_divert to
//| flatter ground) and touches down at about touchdown_speed. Aims for about 5 km of the site.
//| Options:
//|   target ["any"]: "any", LATLNG(lat, lng), lexicon("lat", x, "lng", y) or
//|     lexicon("biome", name) on the body orbited. "any" lands about half an orbit on. A site must
//|     pass within 20 km of the ground track in the next 3 orbits; plan a plane change first
//|     otherwise. A biome target (e.g. lexicon("biome", "Midlands")), with an optional "near" point
//|     (LATLNG or lat/lng lexicon), finds its site when it flies, with find_landing_site's search: the
//|     soonest pass (or the one nearest near) over the biome under the ground track in the time cap,
//|     5 km inside its edge if it can, in daylight if daylight is set, within max_slope. It warps there
//|     on rails and flies that site as a LATLNG.
//|     Use find_landing_site to see such sites when you plan; pass land the biome, not a site.
//|   daylight [true]: set it from the Mission Setting Daylight landing. true touches down only with
//|     the sun at least 5 degrees up at the site. For a site it looks up to a day of the body ahead
//|     for such a pass (about 47 h at the Mun, 13 h at Minmus) and warps there on rails, with no Due
//|     Time needed; give expected_duration_s that wait. "any" with daylight fails only on an orbit
//|     that runs along the day-night line.
//|   max_slope [8]: degrees; steeper ground is rejected during final descent.
//|   max_divert [300]: m it may move off the site to find ground within max_slope.
//|   max_overshoot [500]: m past the site it may land rather than turn back. The descent brakes
//|     once and never turns back for the site while it stops within this; stopped farther past, it
//|     flies back. Set it from the Mission Goal's accuracy (e.g. 50 for "land within 50 m of the
//|     flag"); a small figure may cost a turn back and its delta-v.
//|   touchdown_speed [2]: m/s at touchdown.
//|   gear_alt [1000]: m above the ground to lower the landing gear.
//|   burn_margin [1.10]: braking starts this much farther out than the stopping distance needs.
//|   max_duration ["auto"]: the wait to the deorbit burn plus 2400 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: slope (degrees where it came to rest), touchdown_speed_actual (m/s),
//| touchdown_h_speed (m/s sideways at touchdown), lean (degrees from upright once settled),
//| reversals (times the descent turned back after braking began; 0 normally), distance_to_target
//| (m from the site asked for, or the spot chosen for "any" or a biome), best_effort (true if nothing within
//| max_divert was flat enough and it landed on the least steep spot found), burn_start_ut (when the braking burn was planned to start; -1 if it never got that far),
//| impact_ut (when the orbit after the deorbit burn meets the ground at the site's height; -1 if it
//| clears it or it never got that far), biome (the biome it came to rest in; "" if it never landed);
//| geo (lat, lng, alt) is the common field once landed. Fails with "land flies airless
//| bodies only", an orbit it can't land from, a site off the ground track, no daylight pass,
//| "no site in the <biome> biome" (saying how far the search got), landing outside the biome asked for,
//| not enough delta-v, "deorbit left periapsis" well under the plan, "tipped over", out of fuel, or an abort; a failure in the
//| descent leaves the craft safe but falling, so give it the delta-v (and TWR over 1.5) it needs.
//| Tool: stage_performance for the delta-v and TWR at the body (and find_landing_site, as above).
//| Example: `local res is land(lexicon("target", latlng(-0.5, 22), "daylight", true)). addons:claudia:report(res).`

// The block's own options and defaults.
function land_defaults {
  return lexicon("target", "any", "daylight", true, "max_slope", 8, "max_divert", 300,
    "max_overshoot", 500, "touchdown_speed", 2, "gear_alt", 1000, "burn_margin", 1.10).
}

// "" or why an option's value isn't one the block can use.
function land_unreadable {
  parameter opt.
  local t is opt:target.
  local target_rule is "target must be " + char(34) + "any" + char(34) + ", LATLNG(lat, lng), lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y) or lexicon(" + char(34) + "biome" + char(34) + ", name), not ".
  if t:istype("String") {
    if t <> "any" {
      return target_rule + "'" + t + "'".
    }
  } else if land_is_biome(t) {
    local biome_problem is land_biome_unreadable(t).
    if biome_problem <> "" {
      return biome_problem.
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
  if not land_number_in(opt:max_overshoot, 0, 10000) {
    return "max_overshoot must be metres from 0 to 10000".
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

// "" or why a biome target (a lexicon with the key biome) isn't one the block can use: biome a name,
// and near, if given, a LATLNG or a lat/lng lexicon; no other keys.
function land_biome_unreadable {
  parameter t.
  local name_rule is "target's biome must be a biome's name, e.g. " + char(34) + "Midlands" + char(34).
  if not t:biome:istype("String") {
    return name_rule.
  }
  if t:biome:trim = "" {
    return name_rule.
  }
  for key in t:keys {
    if key <> "biome" and key <> "near" {
      return "a biome target takes only biome and near, not " + key.
    }
  }
  if t:haskey("near") {
    local near is t:near.
    local near_rule is "target's near must be LATLNG(lat, lng) or lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y) with numbers".
    if near:istype("Lexicon") {
      if not (near:haskey("lat") and near:haskey("lng")) {
        return near_rule.
      }
      if not (near:lat:istype("Scalar") and near:lng:istype("Scalar")) {
        return near_rule.
      }
    } else if not near:istype("GeoCoordinates") {
      return "target's near must be LATLNG(lat, lng) or a lat/lng lexicon, not a " + near:typename.
    }
  }
  return "".
}

// True if the target option is a biome target.
function land_is_biome {
  parameter target_opt.
  if not target_opt:istype("Lexicon") {
    return false.
  }
  return target_opt:haskey("biome").
}

// What land asks ADDONS:CLAUDIA:LANDINGSITE (find_landing_site's search) for a biome target: a
// site in the biome, in daylight if wanted, no steeper than max_slope, under the ground track in the
// window find_landing_site defaults to (the Mission Settings time cap, no within_hours) and at
// least the search's default margin inside the biome, nearest the near point if there is one. The
// search wants a slope over 0, so max_slope 0 asks for 0.1 degrees. Pure.
function land_site_query {
  parameter target_opt, daylight, max_slope.
  local query is lexicon("biome", target_opt:biome, "daylight", daylight, "max_slope_deg", max(0.1, max_slope)).
  if target_opt:haskey("near") {
    query:add("near", lexicon("latitude", target_opt:near:lat, "longitude", target_opt:near:lng)).
  }
  return query.
}

// True if two biome names are the same one, ignoring case and spaces ("northern basin" is stock's
// NorthernBasin), as the site search matches them. Pure.
function land_same_biome {
  parameter a, b.
  return a:replace(" ", "") = b:replace(" ", "").
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

// ---- Pure helpers: the coast's end condition for the ground ----

// Seconds until a craft h m above the terrain, moving down at v_down (m/s, negative if climbing),
// reaches it under gravity g (m/s^2): the root of h = v_down t + g t^2 / 2. 0 at or below the ground.
function land_fall_eta {
  parameter h, v_down, g.
  if h <= 0 {
    return 0.
  }
  return (sqrt(v_down ^ 2 + 2 * g * h) - v_down) / g.
}

// Whether the coast must end and the descent start: the time to impact (t_impact, from land_fall_eta) is
// inside the time to stop the downward speed at accel (m/s^2), times margin, plus 30 s.
function land_brake_now {
  parameter t_impact, v_down, accel, margin.
  return land_brake_in(t_impact, v_down, accel, margin) < 0.
}

// Seconds until land_brake_now turns true: negative once it is.
function land_brake_in {
  parameter t_impact, v_down, accel, margin.
  return t_impact - (max(0, v_down) / accel * margin + 30).
}

// The physics warp rate (1 to 4) the fall may use: 4x, 2x from 60 s before braking is due, 1x from 30 s.
function land_warp_cap {
  parameter t_impact, v_down, accel, margin.
  local until_brake is land_brake_in(t_impact, v_down, accel, margin).
  if until_brake <= 30 {
    return 1.
  }
  if until_brake <= 60 {
    return 2.
  }
  return 4.
}

// Whether periapsis after the deorbit burn (m above sea level) is within 1500 m under the planned one.
function land_periapsis_ok {
  parameter actual, planned.
  return actual >= planned - 1500.
}

// ---- Pure helpers: descent guidance ----

// Metres needed to stop from speed (m/s) at accel (m/s^2), times margin.
function land_stop_distance {
  parameter speed, accel, margin.
  return speed ^ 2 / (2 * accel) * margin.
}

// Horizontal speed (m/s) to close on a site dist m away: the speed it can still stop from at
// accel / margin, eased to half the distance per second near the site. Following it costs
// accel / margin times v / (v + 2 accel / margin) of deceleration: never more than accel / margin,
// tapering to v / 2 at the site, with no step where a sudden demand could overshoot (#233).
function land_vh_target {
  parameter dist, accel, margin.
  local a_ok is accel / margin.
  return sqrt(2 * a_ok * max(0, dist) + (2 * a_ok) ^ 2) - 2 * a_ok.
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

// Distances (m) along the ground track at which the descent reads the terrain ahead: where the craft
// will be in 0.5 s to a minute at v_h m/s, every 3 s past the first few, and never past to_go (m to
// where it stops), since the ground beyond doesn't matter (#365). Pure.
function land_lookahead {
  parameter v_h, to_go.
  local out is list().
  for t in list(0.5, 1, 2) {
    out:add(min(max(0, to_go), v_h * t)).
  }
  for i in range(1, 21) {
    out:add(min(max(0, to_go), v_h * 3 * i)).
  }
  return out.
}

// The height (m) the descent steers by: the least of radar (m above the terrain under the craft)
// and its height above each of heights (m above sea level, from land_lookahead's points) at alt_now
// (m above sea level), so it holds its height above the highest ground ahead, not only below (#365). Pure.
function land_clearance {
  parameter radar, alt_now, heights.
  local h is radar.
  for each in heights {
    set h to min(h, alt_now - each).
  }
  return h.
}

// Vertical speed (m/s, up positive) at h m above the terrain, aiming for h_ref: climbs back (up to
// 20 m/s) when below it, and descends at a tenth of the height to lose per second when above,
// never slower than touchdown speed and never faster than it can stop from at a_stop (m/s^2), nor
// than 100 m/s, about a free fall from the descent's planned 3 km (#365).
function land_vv_target {
  parameter h, h_ref, a_stop, touchdown.
  if h < h_ref {
    return min(20, 0.1 * (h_ref - h)).
  }
  local fastest is max(touchdown, min(100, sqrt(2 * a_stop * max(0, h)))).
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

// Acceleration (m/s^2, positive ahead) along one horizontal axis onto a site s m ahead, moving
// v_ahead m/s (positive ahead) with a_max m/s^2 to spend. It holds land_vh_target's speed: the
// deceleration that speed needs as the distance closes (its slope times the speed), plus a pull onto
// it over 2 s; faster than that, at least the deceleration that stops it on the site. On or past the
// site it only stops, so it lands long rather than turning back. Never over v_top m/s: braking far
// out, it holds its speed rather than speeding up to the curve's (#365). All capped at a_max.
function land_axis_accel {
  parameter s, v_ahead, a_max, margin, v_top is 1e9.
  // Stopping past the site: over 4 s, slow enough that a turn lagging a second or two behind
  // doesn't carry it on through zero.
  local a is -v_ahead / 4.
  if s > 0 {
    local v_want is land_vh_target(s, a_max, margin).
    local a_ok is a_max / margin.
    // d(v_want)/ds, 0 where v_top holds it.
    local slope is a_ok / max(0.001, v_want + 2 * a_ok).
    if v_want > v_top {
      set v_want to v_top.
      set slope to 0.
    }
    set a to (v_want - v_ahead) / 2 - slope * max(0, v_ahead).
    if v_ahead > v_want {
      set a to min(a, -v_ahead ^ 2 / (2 * s)).
    }
  }
  return max(-a_max, min(a_max, a)).
}

// land_axis_accel across the braking axis, where the site may be on either side: c m to it (either
// sign), moving v_across m/s the same way.
function land_cross_accel {
  parameter c, v_across, a_max, margin.
  if c < 0 {
    return -land_axis_accel(-c, -v_across, a_max, margin).
  }
  return land_axis_accel(c, v_across, a_max, margin).
}

// One tick of the powered descent's guidance in the braking frame: the axis along the horizontal
// velocity when braking began, and across it. st holds s (m to the site along the axis, negative once
// past it), c (m to it across), v_al and v_cr (m/s along and across), v_up (m/s), h (m above the
// terrain), h_start (h when braking began), v_top (the most m/s along the axis), a_max (m/s^2 the engines give), g (m/s^2) and radius (m
// from the body's centre). It glides down (land_approach_height) to where it will stop, the site or
// its stopping distance on, so it comes straight down once it has stopped; gives the vertical
// acceleration first, brakes along the axis with what is left (land_axis_accel) and closes across it
// with the rest, upright near the ground. Returns lexicon("al", "cr", "up"): m/s^2 wanted, with, for
// the trace, "h_ref" (m), "vh" and "vv" (m/s) it aims for and "to_go" (m to where it stops).
function land_guide {
  parameter st, touchdown, margin.
  local v_h is sqrt(st:v_al ^ 2 + st:v_cr ^ 2).
  local g_eff is st:g - v_h ^ 2 / st:radius.
  local a_most is 0.95 * st:a_max.
  local a_h_most is sqrt(max(0.01, a_most ^ 2 - max(0, g_eff) ^ 2)).
  local to_go is max(max(0, st:s), max(abs(st:c), land_stop_distance(v_h, a_h_most, margin))).
  // It falls no faster than half the thrust braking leaves (land_vh_target's deceleration at this
  // speed) can stop, so stopping the fall never takes the thrust the braking needs.
  local a_ok is a_h_most / margin.
  local braking_needs is a_ok * v_h / (v_h + 2 * a_ok).
  local a_stop is max(0.2, 0.5 * (sqrt(max(0, a_most ^ 2 - braking_needs ^ 2)) - st:g)).
  local h_ref is land_approach_height(to_go, st:h_start).
  local vv_want is land_vv_target(st:h, h_ref, a_stop, touchdown).
  local split is land_split_accel(a_most, g_eff + 0.5 * (vv_want - st:v_up), a_most).
  local al is land_axis_accel(st:s, st:v_al, split:h, margin, st:v_top).
  local cr is land_cross_accel(st:c, st:v_cr, sqrt(max(0, split:h ^ 2 - al ^ 2)), margin).
  local tilt is land_max_tilt(st:h).
  if tilt < 90 {
    local most is split:v * tan(tilt).
    local sideways is sqrt(al ^ 2 + cr ^ 2).
    if sideways > most {
      set al to al * most / sideways.
      set cr to cr * most / sideways.
    }
  }
  return lexicon("al", al, "cr", cr, "up", split:v, "h_ref", h_ref, "vv", vv_want,
    "vh", min(st:v_top, land_vh_target(st:s, split:h, margin)), "to_go", to_go).
}

// The descent's phase, for its trace: "fall" before braking, "braking" while over 50 m/s across the
// ground, "approach" closing in, "terminal" once coming straight down (h_ref, its height to hold, 0). Pure.
function land_phase {
  parameter braking, v_h, h_ref.
  if not braking {
    return "fall".
  }
  if h_ref <= 0 {
    return "terminal".
  }
  if v_h > 50 {
    return "braking".
  }
  return "approach".
}

// Whether the descent means its engines to thrust in this phase: every phase but the fall before
// braking (so a stage that runs dry while braking stages even though the throttle falls to 0). Pure.
function land_burning {
  parameter phase.
  return phase <> "fall".
}

// Whether the descent traces a line now: at a phase change, or 2 s (ut) after the last line. Pure.
function land_trace_due {
  parameter phase, last_phase, ut, last_ut.
  return phase <> last_phase or ut >= last_ut + 2.
}

// Whether a craft moving v_ahead m/s along the braking axis has turned back: under -1 m/s (jitter
// below that is ignored), and until it is moving ahead again (0 or more); was: the last answer.
function land_is_reversed {
  parameter v_ahead, was.
  if v_ahead < -1 {
    return true.
  }
  if v_ahead >= 0 {
    return false.
  }
  return was.
}

// Whether the descent must turn back for the site: stopped (horizontal speed under 1 m/s) more than
// max_overshoot m past it (s, m to the site along the braking axis, is under -max_overshoot).
function land_turn_back {
  parameter s, speed, max_overshoot.
  return s < -max_overshoot and speed < 1.
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

// True if a touchdown cross_m off the ground track with the sun sun_deg up is one land may plan for
// in daylight: within 20 km of the track and the sun at least 5 degrees up. Pure.
function land_lit_near {
  parameter cross_m, sun_deg.
  return cross_m <= 20000 and sun_deg >= 5.
}

// How far ahead (s) land looks for a daylight pass over a site: a little over one turn of the body
// (rotation_s), which holds a whole day and night. Pure.
function land_daylight_horizon {
  parameter rotation_s.
  return 1.2 * rotation_s.
}

// The first burn UT from from_ut to until_ut at which a pass over the site could touch down in
// daylight (land_lit_near, the touchdown a fall_s fall later), on the orbit as it is now; -1 if none.
// Coarse steps of a 720th of the body's turn, so land_plan_from then pins the pass itself.
function land_next_lit {
  parameter site, from_ut, until_ut, fall_s.
  local normal is vcrs(ship:position - body:position, ship:velocity:orbit):normalized.
  local stride is body:rotationperiod / 720.
  local t is from_ut.
  until t > until_ut {
    local spot is land_site_vector(site, t + fall_s).
    local cross is abs(90 - vang(normal, spot)) * constant:pi / 180 * body:radius.
    if land_lit_near(cross, land_sun_elevation(spot, t + fall_s)) {
      return t.
    }
    set t to t + stride.
  }
  return -1.
}

// The UT land's pass search starts from: 120 s from now, or when the craft passes over a site
// (pass_ut, -1 for none) an orbit before it, so the 3 orbits searched hold that pass however far
// ahead it is (a biome site's pass may be hours on). Pure.
function land_search_start {
  parameter now, pass_ut, period.
  return max(now + 120, pass_ut - period).
}

// When to burn to land at the site (or anywhere, for site ""): lexicon("error", "" or why not,
// "burn_ut", "arrive_ut", "site" (lat, lng), "r_pe"). Looks 3 orbits ahead; when daylight is wanted
// and every pass over a site in that time is dark, it looks on for a lit pass up to a day of the body
// ahead (land_daylight_horizon). There is no wait cap: the block warps there (ADR 0004).
function land_plan {
  parameter opt, site, approach, pass_ut is -1.
  local earliest is land_search_start(time:seconds, pass_ut, ship:orbit:period).
  local plan is land_plan_from(opt, site, approach, earliest).
  if not plan:haskey("dark") {
    return plan.
  }
  local period is ship:orbit:period.
  local fall_s is land_transfer_time(body:mu, ship:orbit:semimajoraxis, land_pe_radius(site, approach, ship:orbit:semimajoraxis)).
  local until_ut is earliest + land_daylight_horizon(body:rotationperiod).
  local from_ut is earliest.
  until from_ut > until_ut {
    local lit is land_next_lit(site, from_ut, until_ut, fall_s).
    if lit < 0 {
      break.
    }
    // Start the fine search an orbit early, so the pass the coarse step found is inside it.
    // Any miss here (dark, or the passes in this window wide of the site on an inclined orbit) steps
    // on: the first search already found the site near the track, so only success ends the search.
    set plan to land_plan_from(opt, site, approach, max(earliest, lit - period)).
    if plan:error = "" {
      return plan.
    }
    set from_ut to lit + period.
  }
  return lexicon("error", "no daylight pass over the site within a day of " + body:name + " (" + round((until_ut - earliest) / 3600, 1) + " h) on this orbit: change the orbit's plane, or land with daylight false").
}

// land_plan's search from UT earliest, up to 3 orbits ahead. A failure with only dark passes over a
// site has the key "dark".
function land_plan_from {
  parameter opt, site, approach, earliest.
  local mu is body:mu.
  local sma is ship:orbit:semimajoraxis.
  local period is ship:orbit:period.
  local step is period / 360.
  local steps is ceiling(3 * period / step).

  if site:istype("String") {
    // Anywhere: the first burn whose arrival point (opposite the burn) is in daylight if wanted.
    for i in range(steps) {
      local burn_ut is earliest + i * step.
      local here is positionat(ship, burn_ut) - body:position.
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
    // Anywhere on the ring is dark for 3 orbits only when the orbit runs along the day-night line,
    // which turns too slowly (with the body's year) to wait out.
    return lexicon("error", "no daylight touchdown in the next 3 orbits: the orbit runs along the day-night line; change its plane, or land with daylight false").
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
    local here is positionat(ship, burn_ut) - body:position.
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
    return lexicon("error", "no daylight pass over the site in the next 3 orbits", "dark", true).
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
  local pos is positionat(ship, ut) - body:position.
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
  local extra is lexicon("slope", -1, "touchdown_speed_actual", -1, "touchdown_h_speed", -1, "lean", -1, "reversals", 0,
    "distance_to_target", -1, "best_effort", false, "burn_start_ut", -1, "impact_ut", -1, "biome", "").
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
  local by_biome is land_is_biome(opt:target).
  if by_biome {
    if opt:target:haskey("near") {
      if opt:target:near:istype("GeoCoordinates") {
        if opt:target:near:body:name <> body:name {
          return claudia_finish(ctx, false, "target's near is on " + opt:target:near:body:name + ", not " + body:name, extra).
        }
      }
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

  // A biome target becomes a site, then flies as a LATLNG would. The site search is the mod's
  // (find_landing_site's), through a block-internal call the Script Check allows only here in the
  // Core Library (ADR 0007), so it stays inline rather than in a helper a Mission Script could call.
  local site is "".
  local pass_ut is -1.
  if by_biome {
    local found is addons:claudia:landingsite(land_site_query(opt:target, opt:daylight, opt:max_slope)).
    if found:error <> "" {
      local advice is "raise max_slope, try another biome or near point, or change the orbit's plane".
      if opt:daylight {
        set advice to advice + ", or land with daylight false".
      }
      return claudia_finish(ctx, false, found:error + "; " + advice, extra).
    }
    set site to lexicon("lat", found:lat, "lng", found:lng).
    set pass_ut to found:pass_ut.
    if found:near_edge {
      addons:claudia:blocktrace("land", lexicon("phase", "site", "note", "at the " + opt:target:biome + " biome edge: nothing deeper reachable")).
    }
  } else {
    set site to land_site_of(opt:target).
  }

  // A craft whose engines aren't lit yet stages into them (within stage_limit).
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }

  local approach is 3000.
  local plan is land_plan(opt, site, approach, pass_ut).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  local aim is plan:site.
  local mu is body:mu.
  local burn_pos is positionat(ship, plan:burn_ut) - body:position.
  local burn_vel is velocityat(ship, plan:burn_ut):orbit.
  local deorbit_dv is land_deorbit_dv(mu, burn_pos:mag, burn_vel:mag, plan:r_pe).
  local needed is abs(deorbit_dv) + 1.2 * land_periapsis_speed(mu, burn_pos:mag, plan:r_pe) + 100.
  if ship:deltav:current > 0 and ship:deltav:current < needed {
    return claudia_finish(ctx, false, "not enough delta-v: about " + round(needed) + " m/s needed, " + round(ship:deltav:current) + " m/s left", extra).
  }
  // The deorbit burn may be up to a day of the body off (a daylight pass): exec_node warps there.
  local wait_s is plan:burn_ut - time:seconds - 60.
  if opt:max_duration:istype("String") {
    set opt:max_duration to max(0, wait_s) + 2400.
  }
  sas off.

  // Deorbit: retrograde, half an orbit before the site.
  local descent_node is node(plan:burn_ut, 0, 0, deorbit_dv).
  add descent_node.
  local flown is claudia_fly_node(ctx, descent_node).
  if flown <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, flown, extra).
  }

  // A burn that left periapsis well under the plan (a bad burn or bad terrain data) would put the
  // craft into the ground before the descent could be flown to plan.
  local planned_pe is plan:r_pe - body:radius.
  if not land_periapsis_ok(ship:orbit:periapsis, planned_pe) {
    claudia_make_safe().
    return claudia_finish(ctx, false, "deorbit left periapsis at " + round(ship:orbit:periapsis) + " m, planned " + round(planned_pe) + " m", extra).
  }

  // Correction, a quarter orbit before the site: turn the plane about the craft's position so it
  // holds the site at periapsis.
  local pe_ut is time:seconds + eta:periapsis.
  local fix_ut is time:seconds + eta:periapsis / 2.
  if fix_ut > time:seconds + 90 {
    local fix_pos is positionat(ship, fix_ut) - body:position.
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
  // When braking is planned to start, and when this orbit meets the ground at the site's height (-1:
  // it clears it), so a braking burn planned after the impact shows in the result.
  set extra:burn_start_ut to round(brake_ut, 1).
  local ground_r is body:radius + max(0, body:geopositionlatlng(aim:lat, aim:lng):terrainheight).
  local fall_s is claudia_fall_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, mu, ground_r, ship:orbit:trueanomaly).
  if fall_s >= 0 {
    set extra:impact_ut to round(time:seconds + fall_s, 1).
  }
  if brake_ut - 90 > time:seconds {
    local coasted is land_coast(ctx, brake_ut - 90, a_brake).
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
  set extra:lean to round(vang(ship:facing:forevector, ship:up:vector), 1).
  set extra:biome to addons:claudia:biome(ship:geoposition).
  if extra:lean > 30 {
    return claudia_finish(ctx, false, "tipped over on landing", extra).
  }
  if by_biome {
    if not land_same_biome(extra:biome, opt:target:biome) {
      return claudia_finish(ctx, false, "landed in the " + extra:biome + ", outside the " + opt:target:biome + " biome (" + extra:distance_to_target + " m from the site chosen in it)", extra).
    }
  }
  return claudia_finish(ctx, true, "", extra).
}

// The shared wait loop (claudia_warp_to) to ut, with a stop condition: ended early (returns "") once
// land_brake_now says the terrain under the craft is coming too soon, since KSP can cut rails warp
// short near the ground. a_brake: m/s^2 the engines brake with. Returns "" (at ut, or early for the
// ground; the descent follows either way) or an abort reason.
function land_coast {
  parameter ctx, ut, a_brake.
  local impact_in is {
    local g is body:mu / (body:radius + altitude) ^ 2.
    return land_fall_eta(alt:radar, -ship:verticalspeed, g).
  }.
  local ground_near is {
    return land_brake_now(impact_in:call(), -ship:verticalspeed, a_brake, ctx:opt:burn_margin).
  }.
  local cap is {
    return land_warp_cap(impact_in:call(), -ship:verticalspeed, a_brake, ctx:opt:burn_margin).
  }.
  return claudia_warp_to(ctx, ut, 30, ground_near, cap).
}

// The powered descent onto aim, to touchdown and a settled craft. Sets best_effort, reversals,
// touchdown_speed_actual and touchdown_h_speed in extra (and lean, if settling fails), and traces each
// phase and every 2 s to the log (#365). Returns "" or why it failed.
function land_descend {
  parameter ctx, aim, extra.
  local opt is ctx:opt.
  local site is lexicon("lat", aim:lat, "lng", aim:lng).
  local braking is false.
  local h_start is 0.
  // The braking axis: the horizontal velocity when braking began (towards the site again after
  // turning back), and whether the craft is moving back along it (land_is_reversed).
  local axis is v(0, 0, 0).
  local reversed is false.
  set extra:reversals to 0.
  local steer is -ship:velocity:surface.
  local tset is 0.
  lock steering to steer.
  lock throttle to tset.
  local last_speed is ship:velocity:surface:mag.
  local last_h_speed is 0.
  // The fastest it may move along the braking axis: its speed when braking began (#365).
  local v_top is 0.
  // The terrain heights at land_lookahead's points along the ground track, a few read each tick.
  local ahead_h is list().
  local next_read is 0.
  local to_go is 1e9.
  // The trace (ADDONS:CLAUDIA:BLOCKTRACE): the last line's phase and UT.
  local last_phase is "".
  local last_trace_ut is -1e9.

  until status = "LANDED" or status = "SPLASHED" {
    local up_v is ship:up:vector.
    local to_site is vxcl(up_v, body:geopositionlatlng(site:lat, site:lng):position).
    local dist is to_site:mag.
    local vel is ship:velocity:surface.
    local v_v is vdot(vel, up_v).
    local v_h is vxcl(up_v, vel).
    local radar is alt:radar.
    local a_max is ship:availablethrust / max(0.001, ship:mass).
    local radius_now is body:radius + altitude.
    local g is body:mu / radius_now ^ 2.
    set last_speed to vel:mag.
    set last_h_speed to v_h:mag.
    if not braking {
      set to_go to dist.
    }
    // The ground ahead: all of it on the first tick, then four points a tick, oldest first.
    local points is land_lookahead(v_h:mag, to_go).
    local track is v(0, 0, 0).
    if v_h:mag > 0.01 {
      set track to v_h:normalized.
    }
    local reads is 4.
    if ahead_h:length = 0 {
      set reads to points:length.
    }
    for k in range(reads) {
      local i is mod(next_read, points:length).
      local height is body:geopositionof(ship:position + track * points[i]):terrainheight.
      if i < ahead_h:length {
        set ahead_h[i] to height.
      } else {
        ahead_h:add(height).
      }
      set next_read to next_read + 1.
    }
    local h is land_clearance(radar, altitude, ahead_h).
    local want is lexicon("h_ref", -1, "vh", -1, "vv", -1).

    if not braking {
      // Brake once land_vh_target's speed is down to this one, if the site falls behind, or if the
      // ground comes up first.
      local a_brake is sqrt(max(0.01, (0.95 * a_max) ^ 2 - g ^ 2)).
      local falling_for is h / max(1, -v_v).
      if land_vh_target(dist, a_brake, opt:burn_margin) <= v_h:mag or vdot(v_h, to_site) < 0 or falling_for < 40 or h < 1500 {
        set braking to true.
        set h_start to max(500, h).
        // At least enough to close on a site moved up to max_divert.
        set v_top to max(50, v_h:mag).
        set axis to v_h.
        if v_h:mag < 1 {
          set axis to to_site.
        }
        // Stopped over the site: any axis will do, as long as it has a direction.
        if vxcl(up_v, axis):mag < 1 {
          set axis to ship:north:vector.
        }
        // Is the ground at the site flat enough? If not, divert to flatter ground now, while the
        // descent has the whole braking distance to take the new site in (a later move could put
        // the site behind it).
        local spot is addons:claudia:flatspot(body:geopositionlatlng(site:lat, site:lng), opt:max_divert, opt:max_slope).
        local moved is land_surface_distance(site:lat, site:lng, spot:lat, spot:lng, body:radius).
        set site to lexicon("lat", spot:lat, "lng", spot:lng).
        set extra:best_effort to not spot:flat.
        set to_site to vxcl(up_v, body:geopositionlatlng(site:lat, site:lng):position).
        if moved > 1 {
          addons:claudia:blocktrace("land", lexicon("phase", "divert", "moved", round(moved), "flat", spot:flat, "dist", round(to_site:mag))).
        }
      }
      set steer to -vel.
      set tset to 0.
    }
    if braking {
      local ahead is vxcl(up_v, axis):normalized.
      local s is vdot(to_site, ahead).
      if land_turn_back(s, v_h:mag, opt:max_overshoot) {
        // Stopped more than max_overshoot past the site: the one case it turns back.
        set axis to to_site.
        set ahead to vxcl(up_v, axis):normalized.
        set s to vdot(to_site, ahead).
        set extra:reversals to extra:reversals + 1.
        set reversed to false.
      }
      local across is vcrs(up_v, ahead):normalized.
      local v_ahead is vdot(v_h, ahead).
      local was is reversed.
      set reversed to land_is_reversed(v_ahead, was).
      if reversed and not was {
        set extra:reversals to extra:reversals + 1.
      }
      set want to land_guide(lexicon("s", s, "c", vdot(to_site, across), "v_al", v_ahead, "v_cr", vdot(v_h, across),
        "v_up", v_v, "h", h, "h_start", h_start, "v_top", v_top, "a_max", a_max, "g", g, "radius", radius_now), opt:touchdown_speed, opt:burn_margin).
      set to_go to want:to_go.
      local accel is ahead * want:al + across * want:cr + up_v * want:up.
      if accel:mag > 0.001 {
        set steer to accel.
      } else {
        set steer to up_v.
      }
      set tset to min(1, accel:mag / max(0.001, a_max)) * max(0, cos(vang(ship:facing:forevector, steer))).
      if radar < opt:gear_alt and not gear {
        gear on.
      }
    }
    // The descent trace, for the log: a craft destroyed mid-descent leaves its last line as the block's state.
    local phase is land_phase(braking, v_h:mag, want:h_ref).
    if land_trace_due(phase, last_phase, time:seconds, last_trace_ut) {
      addons:claudia:blocktrace("land", lexicon("phase", phase, "h", round(h), "radar", round(radar), "v_h", round(v_h:mag, 1),
        "v_down", round(-v_v, 1), "dist", round(dist), "throttle", round(tset, 2), "h_ref", round(want:h_ref),
        "vh_want", round(want:vh, 1), "vv_want", round(want:vv, 1), "pe", round(ship:orbit:periapsis))).
      set last_phase to phase.
      set last_trace_ut to time:seconds.
    }
    local ticked is claudia_tick(ctx, tset, land_burning(phase)).
    if ticked <> "" {
      return ticked.
    }
    wait 0.
  }
  set tset to 0.
  set ship:control:pilotmainthrottle to 0.
  set extra:touchdown_speed_actual to round(last_speed, 2).
  set extra:touchdown_h_speed to round(last_h_speed, 2).
  addons:claudia:blocktrace("land", lexicon("phase", "touchdown", "speed", extra:touchdown_speed_actual, "v_h", extra:touchdown_h_speed)).
  unlock throttle.
  // Hold upright while it settles on its legs, then leave it safe.
  set steer to ship:up:vector.
  local settle_ut is time:seconds + 5.
  until time:seconds > settle_ut {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      // A part lost as it tips stops the settle: report how far over it went.
      set extra:lean to round(vang(ship:facing:forevector, ship:up:vector), 1).
      return ticked.
    }
    wait 0.
  }
  claudia_make_safe().
  return "".
}
