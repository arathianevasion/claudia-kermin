// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block deorbit: from orbit round a body with an atmosphere, plans the one burn that brings
// the craft down on a target and flies it with exec_node. The burn comes from the mod's reentry
// predictor (ADDONS:CLAUDIA:DEORBITBURN: the Kepler orbit to the atmosphere, then the craft's own drag
// cubes held surface retrograde, as reentry flies it), and the landing is predicted again from the orbit
// actually flown (ADDONS:CLAUDIA:REENTRYIMPACT). Both are block-internal calls the Script Check allows
// only in the Core Library (ADR 0007), so they stay inline in deorbit. Built on claudia_lib.ks and
// exec_node.ks; block-only helpers are prefixed deorbit_. The "//|" lines are Claudia's docs for it;
// the first is its summary.

@lazyglobal off.

//| deorbit(options): from orbit round a body with an atmosphere (Kerbin, Eve, Duna, Laythe...), burns
//| once to bring the craft down near a target, e.g. KSC. It chooses the burn itself with the mod's
//| reentry predictor (the craft's own drag, held retrograde as reentry flies it) and flies it with
//| exec_node; it fails rather than burn when no burn in max_wait lands within accuracy_km. It does
//| not fly the descent: call reentry next, in the same sequence.
//| Options:
//|   target [required]: LATLNG(lat, lng), lexicon("lat", x, "lng", y), or "ksc" (Kerbin only). Aim a
//|     splashdown offshore of a coast, so a miss lands in the water.
//|   max_wait ["auto"]: s the burn may wait; "auto" is one turn of the body (about 6 h at Kerbin). The
//|     burn is at least 4 minutes ahead. Give expected_duration_s that wait.
//|   accuracy_km [10]: km the predicted landing may be from the target; a worse best burn fails the
//|     block before any burn.
//|   max_duration ["auto"]: the wait to the burn plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss, stage_limit and drop_empty as for every block.
//| Extra results: burn_ut (UT of the burn; -1 if it never burned), predicted_geo (lexicon lat, lng:
//| where the craft comes down, predicted from the orbit after the burn, or for the best burn found
//| when it fails over accuracy; empty without a prediction), predicted_error_km (km from the target to
//| predicted_geo; -1 without one); dv_used is the burn's. Fails with "target is required", an option
//| value it can't read, "body has no atmosphere", "ksc is on Kerbin", a craft on the ground, "a
//| maneuver node is already planned: remove it first", an orbit it can't deorbit from (escaping, or a
//| periapsis already in the atmosphere: fly reentry), "the best burn ... lands <x> km from the target,
//| over accuracy_km", not enough delta-v, or the reasons exec_node gives (no thrust, out of fuel, an
//| abort; the node is removed). The prediction leaves out parachutes and lift, so expect a few km more.
//| Once the burn has started (burn_ut over 0), call reentry whatever the result: the craft is coming down.
//| Example: `local res is deorbit(lexicon("target", "ksc")). if res:burn_ut > 0 { set res to reentry(). } addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function deorbit_defaults {
  return lexicon("target", "required", "max_wait", "auto", "accuracy_km", 10).
}

// The launch pad at KSC, on Kerbin.
function deorbit_ksc {
  return lexicon("lat", -0.0972, "lng", -74.5577).
}

// How far ahead the burn is at the soonest, s: time to warp out, turn and lead the burn.
function deorbit_lead_s {
  return 240.
}

// "" or why an option value isn't one the block can use.
function deorbit_unreadable {
  parameter opt.
  local t is opt:target.
  local target_rule is "target must be LATLNG(lat, lng), lexicon(" + char(34) + "lat" + char(34) + ", x, " + char(34) + "lng" + char(34) + ", y) or " + char(34) + "ksc" + char(34) + ", not ".
  if t:istype("String") {
    if t = "required" {
      return "target is required".
    }
    if t <> "ksc" {
      return target_rule + "'" + t + "'".
    }
  } else if t:istype("Lexicon") {
    if not (t:haskey("lat") and t:haskey("lng")) {
      return target_rule + "a lexicon without lat and lng".
    }
    if not (t:lat:istype("Scalar") and t:lng:istype("Scalar")) {
      return target_rule + "a lexicon whose lat and lng aren't numbers".
    }
    if abs(t:lat) > 90 {
      return "target's lat must be from -90 to 90, not " + t:lat.
    }
  } else if not t:istype("GeoCoordinates") {
    return target_rule + "a " + t:typename.
  }
  if opt:max_wait:istype("String") {
    if opt:max_wait <> "auto" {
      return "max_wait must be 'auto' or seconds above 0, not '" + opt:max_wait + "'".
    }
  } else if not opt:max_wait:istype("Scalar") or opt:max_wait <= 0 {
    return "max_wait must be 'auto' or seconds above 0, not " + opt:max_wait.
  }
  if not opt:accuracy_km:istype("Scalar") or opt:accuracy_km <= 0 {
    return "accuracy_km must be km above 0, not " + opt:accuracy_km.
  }
  return "".
}

// "" or why the craft can't deorbit where it is: has_atm (its body has an atmosphere) and situation
// (kOS's STATUS).
function deorbit_body_problem {
  parameter has_atm, situation.
  if not has_atm {
    return "body has no atmosphere".
  }
  if situation = "LANDED" or situation = "SPLASHED" or situation = "PRELAUNCH" {
    return "the craft is " + situation:tolower + ", not in orbit: deorbit burns from orbit".
  }
  return "".
}

// The target as lexicon("error", "" or why not, "lat", deg, "lng", deg) on the body named body_name.
function deorbit_site {
  parameter target_opt, body_name.
  local site is lexicon("error", "", "lat", 0, "lng", 0).
  if target_opt:istype("String") {
    if body_name <> "Kerbin" {
      set site:error to "ksc is on Kerbin, and the craft orbits " + body_name + ": give a LATLNG there".
      return site.
    }
    local ksc is deorbit_ksc().
    set site:lat to ksc:lat.
    set site:lng to ksc:lng.
  } else {
    set site:lat to target_opt:lat.
    set site:lng to target_opt:lng.
  }
  return site.
}

// max_wait in seconds: opt_value, or one turn of the body (rotation_period s) for "auto".
function deorbit_max_wait {
  parameter opt_value, rotation_period.
  if opt_value:istype("String") {
    return rotation_period.
  }
  return opt_value.
}

// What deorbit asks ADDONS:CLAUDIA:DEORBITBURN: the site, the window (earliest_ut, then max_wait s),
// the ground's height at the site (m, sea level for water), accuracy_km and the engines' isp (s, for
// the mass after the burn; 0 if unknown). Pure.
function deorbit_query {
  parameter site, earliest_ut, max_wait, ground_alt, accuracy_km, isp.
  return lexicon("lat", site:lat, "lng", site:lng, "earliest_ut", earliest_ut, "max_wait", max_wait,
    "ground_alt", max(0, ground_alt), "accuracy_km", accuracy_km, "isp", isp).
}

// A predicted landing (a block call's result with lat and lng) as the block reports it.
function deorbit_geo {
  parameter landing.
  return lexicon("lat", round(landing:lat, 4), "lng", round(landing:lng, 4)).
}

// "" or why the delta-v left (dv_left m/s; 0 or less when stock can't say) won't fly a burn of dv m/s.
function deorbit_short_of_dv {
  parameter dv, dv_left.
  if dv_left > 0 and dv_left < dv {
    return "not enough delta-v: the burn needs " + round(dv, 1) + " m/s, " + round(dv_left, 1) + " m/s left".
  }
  return "".
}

function deorbit {
  parameter options is lexicon().
  local extra is lexicon("burn_ut", -1, "predicted_geo", lexicon(), "predicted_error_km", -1).
  local ctx is claudia_begin("deorbit", options, deorbit_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is deorbit_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local problem is deorbit_body_problem(body:atm:exists, status).
  if problem <> "" {
    return claudia_finish(ctx, false, problem, extra).
  }
  local site is deorbit_site(opt:target, body:name).
  if site:error <> "" {
    return claudia_finish(ctx, false, site:error, extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }
  // A craft whose engines aren't lit yet stages into them (within stage_limit), so the isp is known.
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }

  local ground_alt is body:geopositionlatlng(site:lat, site:lng):terrainheight.
  local max_wait is deorbit_max_wait(opt:max_wait, body:rotationperiod).
  // The burn search is the mod's reentry predictor, a block-internal call (ADR 0007): inline here.
  local found is addons:claudia:deorbitburn(deorbit_query(site, time:seconds + deorbit_lead_s(), max_wait, ground_alt, opt:accuracy_km, claudia_engine_isp())).
  if found:haskey("lat") {
    set extra:predicted_geo to deorbit_geo(found).
    set extra:predicted_error_km to round(found:error_km, 2).
  }
  if found:error <> "" {
    return claudia_finish(ctx, false, found:error, extra).
  }
  local short is deorbit_short_of_dv(found:dv, ship:deltav:current).
  if short <> "" {
    return claudia_finish(ctx, false, short, extra).
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to max(0, found:burn_ut - time:seconds) + 900.
  }

  local nd is node(found:burn_ut, found:radial, found:normal, found:prograde).
  add nd.
  set extra:burn_ut to round(found:burn_ut, 1).
  local flown is claudia_burn(ctx, nd).
  if not flown:success {
    remove nd.
    return claudia_finish(ctx, false, flown:reason, extra).
  }

  // Where the orbit actually flown comes down: the burn's own error moves it.
  local after is addons:claudia:reentryimpact(lexicon("lat", site:lat, "lng", site:lng, "ground_alt", max(0, ground_alt))).
  if after:error = "" {
    set extra:predicted_geo to deorbit_geo(after).
    set extra:predicted_error_km to round(after:error_km, 2).
  }
  return claudia_finish(ctx, true, "", extra).
}
