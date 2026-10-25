// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block launch: flies from the ground of a body with an atmosphere to a coast out of it.
// Built on claudia_lib.ks; block-only helpers are prefixed launch_. The "//|" lines are Claudia's
// docs for it; the first is its summary.

@lazyglobal off.

//| launch(options): flies from the ground of a body with an atmosphere to a coast out of it; then circularize.
//| It ends once the craft is coasting out of the atmosphere with its apoapsis at target_ap; follow it
//| with circularize in the same Mission Script (one Maneuver Sequence, one Approval). A target_ap
//| below the atmosphere top makes it a hop that ends at the top of the climb.
//| Options:
//|   launch_at ["now"]: "now", a time (UT s) to wait for on the pad, or a body or vessel (e.g. MUN)
//|     to wait for the pad to pass under the plane of, which also sets target_inc to its plane.
//|   target_ap ["auto"]: apoapsis to reach, m; "auto" is the atmosphere top + 10 km.
//|   target_inc [0]: inclination, degrees; negative launches southward.
//|   heading ["lib_lazcalc"]: compass degrees to fly, or "lib_lazcalc" to fly into target_inc.
//|   turn_start_speed [60]: m/s of airspeed at which the gravity turn starts. Give more than the craft
//|     will reach (e.g. 100000) to climb straight up.
//|   turn_shape [0.45]: the turn's pitch is 90 * (1 - f^turn_shape), f the fraction of the way from
//|     the turn start to the atmosphere top (or target_ap if lower); smaller turns over sooner.
//|   max_aoa [5]: most degrees the nose may be off the airflow while the air is thick enough to matter
//|     (over 500 Pa dynamic pressure), all the way up and through the coast.
//|   throttle_max_q ["none"]: Pa of dynamic pressure to throttle back at.
//|   g, atm_height, surface_pressure ["body"]: the body's surface gravity (m/s^2), atmosphere top (m)
//|     and surface pressure (atm), to override what the game says.
//|   max_duration ["auto"]: the wait on the pad plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| In thick air (over a tenth of surface pressure) the throttle never drops below the weight of the
//| craft until cutoff, and the craft never steers more than max_aoa off the airflow, so a craft with
//| no fins doesn't flip. Engines are staged into from the pad, and launch clamps released.
//| Extra results: ap_error (m, apoapsis minus target_ap when the block ended; a hop's shows drag
//| loss), max_q (Pa, highest dynamic pressure), coast_ap (m, apoapsis at first engine cutoff).
//| Fails with "launch starts on the ground", "no atmosphere on <body>: use ascend", "no thrust", a
//| launch_at it can't use, a wait over the cap, "lost control: ...", "fell back into the
//| atmosphere: ...", out of fuel, or an abort.
//| Example: `local r is launch(lexicon("target_ap", 80000)). if r:success { set r to circularize(lexicon("at", "ap")). }
//| addons:claudia:report(r).`

// The block's own options and defaults ("auto", "lib_lazcalc", "none" and "body" are resolved once
// the block knows the body and the wait).
function launch_defaults {
  return lexicon(
    "launch_at", "now",
    "target_ap", "auto",
    "target_inc", 0,
    "heading", "lib_lazcalc",
    "turn_start_speed", 60,
    "turn_shape", 0.45,
    "max_aoa", 5,
    "throttle_max_q", "none",
    "g", "body",
    "atm_height", "body",
    "surface_pressure", "body").
}

// "" or why a string value of an option isn't one the block knows.
function launch_unreadable {
  parameter opt.
  if opt:launch_at:istype("String") and opt:launch_at <> "now" {
    return "launch_at must be 'now', a time (UT s) or a body or vessel to launch into the plane of, not '" + opt:launch_at + "'".
  }
  local words is list(
    list("target_ap", "auto", "m"),
    list("heading", "lib_lazcalc", "degrees"),
    list("throttle_max_q", "none", "Pa"),
    list("g", "body", "a number"),
    list("atm_height", "body", "a number"),
    list("surface_pressure", "body", "a number")).
  for w in words {
    local value is opt[w[0]].
    if value:istype("String") and value <> w[1] {
      return w[0] + " must be '" + w[1] + "' or " + w[2] + ", not '" + value + "'".
    }
  }
  return "".
}

// The pitch (degrees above the horizon) at altitude h (m) of a turn that starts at start_alt and
// ends flat at end_alt: 90 * (1 - fraction^shape); a smaller shape turns over sooner.
function launch_pitch {
  parameter h, start_alt, end_alt, shape.
  if h <= start_alt or end_alt <= start_alt {
    return 90.
  }
  local fraction is min(1, (h - start_alt) / (end_alt - start_alt)).
  return 90 * (1 - fraction ^ shape).
}

// The throttle on the way to target_ap: 0 once ap reaches it; easing off linearly over the last
// ramp metres of apoapsis; easing off from 90% to 110% of max_q (Pa, -1 for no limit) at dynamic
// pressure q_now (Pa); but never below t_floor before cutoff, so the throttle can't collapse in thick air.
function launch_throttle {
  parameter ap, target_ap, ramp, q_now, max_q, t_floor.
  if ap >= target_ap {
    return 0.
  }
  local t is min(1, (target_ap - ap) / ramp).
  if max_q > 0 {
    set t to min(t, 1 - (q_now - 0.9 * max_q) / (0.2 * max_q)).
  }
  return min(1, max(t_floor, max(0, t))).
}

// Thick air: pressure above a tenth of the surface pressure.
function launch_thick_air {
  parameter pressure_ratio.
  return pressure_ratio > 0.1.
}

// The lowest throttle allowed before cutoff: in thick air (pressure_ratio of surface pressure),
// enough for thrust to match weight (mass_t at g m/s^2 against thrust_kn); otherwise none.
function launch_throttle_floor {
  parameter pressure_ratio, mass_t, g, thrust_kn.
  if not launch_thick_air(pressure_ratio) or thrust_kn <= 0 {
    return 0.
  }
  return min(1, mass_t * g / thrust_kn).
}

// Degrees wrapped into [0, 360).
function launch_wrap {
  parameter deg.
  return deg - 360 * floor(deg / 360).
}

// The compass heading (degrees) to launch into an orbit of inclination inc (degrees; negative
// launches southward) from latitude lat, for an orbital speed of v_orbit (m/s) over ground turning
// eastward at v_eq (m/s at the equator). An inclination below the latitude can't be reached; it
// gives the nearest, due east (or west for a retrograde one).
// Clean-room stand-in for KSLib lib_lazcalc's launch azimuth, computed once at liftoff.
function launch_azimuth {
  parameter inc, lat, v_orbit, v_eq.
  local inertial is arcsin(max(-1, min(1, cos(abs(inc)) / cos(lat)))).
  if inc < 0 {
    set inertial to 180 - inertial.
  }
  local east is v_orbit * sin(inertial) - v_eq * cos(lat).
  local north is v_orbit * cos(inertial).
  return launch_wrap(arctan2(east, north)).
}

// How long until a launch site at latitude lat (degrees) and celestial longitude site_lng
// (degrees: its longitude plus the body's rotation angle) next passes under the plane of an orbit of
// inclination inc and longitude of ascending node lan, on a body turning once in period s.
// lexicon("wait", s or -1 if the site never passes under it, "north", true if that pass launches
// northward). An equatorial plane gives no wait.
function launch_plane_wait {
  parameter lat, site_lng, inc, lan, period.
  if abs(sin(inc)) < 0.0001 {
    return lexicon("wait", 0, "north", true).
  }
  local x is tan(lat) / tan(inc).
  if abs(x) > 1 {
    return lexicon("wait", -1, "north", true).
  }
  local offset is arcsin(x).
  local north_wait is launch_wrap(lan + offset - site_lng) / 360 * period.
  local south_wait is launch_wrap(lan + 180 - offset - site_lng) / 360 * period.
  if north_wait <= south_wait {
    return lexicon("wait", north_wait, "north", true).
  }
  return lexicon("wait", south_wait, "north", false).
}

// Dynamic pressure (Pa) above which the air pushes hard enough on the craft for max_aoa to apply.
function launch_aero_q {
  return 500.
}

// The body's values, each option's own or, for "body", the game's: lexicon("g", m/s^2,
// "atm_height", m, "surface_pressure", atm).
function launch_body {
  parameter opt.
  local b is ship:body.
  local values is lexicon("g", opt:g, "atm_height", opt:atm_height, "surface_pressure", opt:surface_pressure).
  if values:g:istype("String") {
    set values:g to b:mu / b:radius ^ 2.
  }
  if values:atm_height:istype("String") {
    set values:atm_height to 0.
    if b:atm:exists {
      set values:atm_height to b:atm:height.
    }
  }
  if values:surface_pressure:istype("String") {
    set values:surface_pressure to b:atm:altitudepressure(0).
  }
  return values.
}

// want, turned toward the airflow (flow) until it is no more than max_aoa degrees off it.
function launch_limit_aoa {
  parameter want, flow, max_aoa.
  if vang(want, flow) <= max_aoa {
    return want.
  }
  local side is vxcl(flow, want).
  if side:mag < 0.000001 {
    return flow.
  }
  return flow:normalized * cos(max_aoa) + side:normalized * sin(max_aoa).
}

// When to launch and into what inclination: lexicon("error", "" or why not, "wait", s, "inc", degrees).
// options are the ones given (to tell whether target_inc was).
function launch_when {
  parameter opt, options.
  local plan is lexicon("error", "", "wait", 0, "inc", opt:target_inc).
  local go_at is opt:launch_at.
  if go_at:istype("String") {
    return plan.
  }
  if go_at:istype("Scalar") {
    set plan:wait to max(0, go_at - time:seconds).
    return plan.
  }
  if not go_at:istype("Orbitable") {
    set plan:error to "launch_at must be 'now', a time (UT s) or a body or vessel to launch into the plane of".
    return plan.
  }
  if go_at:body:name <> ship:body:name {
    set plan:error to "launch_at " + go_at:name + " doesn't orbit " + ship:body:name.
    return plan.
  }
  local plane is launch_plane_wait(ship:latitude, ship:longitude + ship:body:rotationangle, go_at:orbit:inclination, go_at:orbit:lan, ship:body:rotationperiod).
  if plane:wait < 0 {
    set plan:error to "the pad never passes under " + go_at:name + "'s plane: inclination " + round(go_at:orbit:inclination, 2) + " is below latitude " + round(abs(ship:latitude), 2).
    return plan.
  }
  set plan:wait to plane:wait.
  if not options:haskey("target_inc") {
    set plan:inc to go_at:orbit:inclination.
    if not plane:north {
      set plan:inc to -plan:inc.
    }
  }
  return plan.
}

// Releases launch clamps still holding the craft, staging once (within stage_limit).
function launch_release_clamps {
  parameter ctx.
  for p in ship:parts {
    if p:hasmodule("LaunchClamp") {
      wait until stage:ready.
      if stage:number > ctx:opt:stage_limit {
        stage.
        wait 0.5.
        set ctx:parts to ship:parts:length.
        set ctx:last_ut to time:seconds.
      }
      return.
    }
  }
}

function launch {
  parameter options is lexicon().
  local extra is lexicon("ap_error", 0, "max_q", 0, "coast_ap", 0).
  local ctx is claudia_begin("launch", options, launch_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is launch_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if not claudia_on_ground() {
    return claudia_finish(ctx, false, "launch starts on the ground, not " + status, extra).
  }
  local bv is launch_body(opt).
  if bv:atm_height <= 0 {
    return claudia_finish(ctx, false, "no atmosphere on " + ship:body:name + ": use ascend", extra).
  }
  local target_ap is opt:target_ap.
  if target_ap:istype("String") {
    set target_ap to bv:atm_height + 10000.
  }
  set extra:ap_error to round(ship:apoapsis - target_ap).

  // Wait on the pad.
  local go is launch_when(opt, options).
  if go:error <> "" {
    return claudia_finish(ctx, false, go:error, extra).
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to go:wait + 900.
  }
  local waiting is claudia_check_wait(go:wait).
  if waiting <> "" {
    return claudia_finish(ctx, false, waiting, extra).
  }
  if go:wait > 0 {
    local waited is claudia_wait_until(ctx, time:seconds + go:wait).
    if waited <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, waited, extra).
    }
  }

  local hdg is opt:heading.
  if hdg:istype("String") {
    local v_orbit is sqrt(ship:body:mu / (ship:body:radius + target_ap)).
    local v_eq is 2 * constant:pi * ship:body:radius / ship:body:rotationperiod.
    set hdg to launch_azimuth(go:inc, ship:latitude, v_orbit, v_eq).
  }
  local max_q_pa is -1.
  if opt:throttle_max_q:istype("Scalar") {
    set max_q_pa to opt:throttle_max_q.
  }

  // Light the engines.
  sas off.
  local tset is 1.
  lock throttle to tset.
  local aim is ship:up:vector.
  lock steering to aim.
  if ship:availablethrust <= 0 {
    local staged is claudia_autostage(ctx).
    if staged <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, staged, extra).
    }
  }
  if ship:availablethrust <= 0 {
    claudia_make_safe().
    return claudia_finish(ctx, false, "no thrust", extra).
  }
  launch_release_clamps(ctx).

  // Climb, then coast with the steering held, until out of the atmosphere (or, for a hop, at the top).
  local hop is target_ap <= bv:atm_height.
  local turn_end is min(bv:atm_height, target_ap).
  local turn_start_alt is -1.
  local ramp is max(500, 0.02 * target_ap).
  local topup_tol is max(100, 0.005 * target_ap).
  local coasting is false.
  local off_since is -1.
  local q_max is 0.
  local reason is "".
  until false {
    local q_pa is ship:q * constant:atmtokpa * 1000.
    set q_max to max(q_max, q_pa).
    local spd is ship:velocity:surface:mag.
    local ap is ship:apoapsis.
    local ratio is 0.
    if bv:surface_pressure > 0 {
      set ratio to ship:body:atm:altitudepressure(altitude) / bv:surface_pressure.
    }

    if coasting {
      if hop and ship:verticalspeed <= 0 {
        break.
      }
      if not hop and altitude > bv:atm_height {
        break.
      }
      if not hop and ship:verticalspeed < 0 {
        set reason to "fell back into the atmosphere: ap " + round(ap) + " m, below " + round(bv:atm_height) + " m".
        break.
      }
      // Top up what drag took, but only out of thick air, so a hop coasts smoothly.
      if ap < target_ap - topup_tol and not launch_thick_air(ratio) {
        set coasting to false.
      }
    }
    if coasting {
      set tset to 0.
    } else {
      local t_floor is launch_throttle_floor(ratio, ship:mass, bv:g, ship:availablethrust).
      set tset to launch_throttle(ap, target_ap, ramp, q_pa, max_q_pa, t_floor).
      if tset = 0 {
        set coasting to true.
        if extra:coast_ap = 0 {
          set extra:coast_ap to round(ap).
        }
      }
    }

    // Steer: the pitch program while burning, the airflow while coasting, never more than max_aoa off
    // the airflow while the air matters.
    if turn_start_alt < 0 and spd >= opt:turn_start_speed {
      set turn_start_alt to altitude.
    }
    local want is ship:up:vector.
    if not coasting {
      local pitch is 90.
      if turn_start_alt >= 0 {
        set pitch to launch_pitch(altitude, turn_start_alt, turn_end, opt:turn_shape).
      }
      set want to heading(hdg, pitch):vector.
    } else if spd > 30 {
      if hop or q_pa > 2 * launch_aero_q() {
        set want to ship:srfprograde:vector.
      } else {
        set want to ship:prograde:vector.
      }
    }
    if q_pa > launch_aero_q() and spd > 30 {
      set want to launch_limit_aoa(want, ship:srfprograde:vector, opt:max_aoa).
    }
    set aim to want.

    // A craft far off its steering for 3 s in the air has lost control: stop rather than tumble on.
    if vang(ship:facing:forevector, aim) > 30 and q_pa > launch_aero_q() {
      if off_since < 0 {
        set off_since to time:seconds.
      } else if time:seconds - off_since > 3 {
        set reason to "lost control: " + round(vang(ship:facing:forevector, aim)) + " degrees off the ascent path at " + round(altitude) + " m".
        break.
      }
    } else {
      set off_since to -1.
    }

    local ticked is claudia_tick(ctx, tset).
    if ticked <> "" {
      set reason to ticked.
      break.
    }
    wait 0.
  }

  set extra:max_q to round(q_max).
  set extra:ap_error to round(ship:apoapsis - target_ap).
  if extra:coast_ap = 0 {
    set extra:coast_ap to round(ship:apoapsis).
  }
  if reason <> "" {
    claudia_make_safe().
    return claudia_finish(ctx, false, reason, extra).
  }
  set tset to 0.
  set ship:control:pilotmainthrottle to 0.
  unlock throttle.
  unlock steering.
  return claudia_finish(ctx, true, "", extra).
}
