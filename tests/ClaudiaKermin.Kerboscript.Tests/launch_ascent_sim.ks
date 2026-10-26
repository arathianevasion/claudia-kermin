// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// An offline ascent of the launch_no_flip block test's craft (LV-T45 under an FL-T800, about 6.4 t,
// thrust-to-weight 2.7 on the pad) from Kerbin's pad, flown by launch's own pitch program, turn hold,
// throttle, thrust floor and angle-off-airflow limit, in the order launch's loop uses them (#235). It
// is a point mass in the plane of the climb: an exponential atmosphere (scale height 5600 m), a
// constant drag area with a transonic rise, thrust and Isp falling with pressure as the LV-T45's do,
// and Kerbin's rotation for the apoapsis. The nose is where the steering asks. Not KSP: good enough
// to show whether a throttled-back craft keeps climbing, not to predict the in-game numbers.

// opt: "target_ap" (m), "max_q" (Pa, -1 for none), "turn_start_speed" (m/s), "turn_shape", "max_aoa"
// (degrees), "drag_area" (m^2), "hold" (false flies the turn by altitude alone, as before #235).
// Prints one "track ..." line per 1000 m of climb, then "stalled <true|false> at <m>" (vertical speed
// lost with the apoapsis still short of target_ap by more than the throttle's easing ramp),
// "coast_ap <m>", "cutoff <where, q and path angle, or none>", "max_q <Pa>", "min_twr <lowest
// burning in thick air>" and "max_lead <degrees>" (most the nose was pitched below the prograde).
function sim_ascent {
  parameter opt.
  local e_const is 2.718281828459.
  local r_body is 600000.
  local mu is 3.5316e12.
  local g is mu / r_body ^ 2.
  local spin is 174.94.
  local scale is 5600.
  local rho0 is 1.225.
  local thrust_vac is 215.
  local isp_vac is 320.
  local flow is thrust_vac / (isp_vac * 9.80665).
  local dry is 2.4.
  local dt is 0.1.
  local turn_end is min(70000, opt:target_ap).
  local ramp is max(500, 0.02 * opt:target_ap).

  local h is 70.
  local vx is 0.
  local vy is 0.
  local m is 6.4.
  local t is 0.
  local turn_start_alt is -1.
  local q_max is 0.
  local min_twr is 100.
  local max_lead is 0.
  local stalled is false.
  local coast_ap is 0.
  local cutoff is "none".
  local next_print is 1000.
  until t > 400 {
    local spd is sqrt(vx ^ 2 + vy ^ 2).
    local ratio is e_const ^ (-h / scale).
    local q is 0.5 * rho0 * ratio * spd ^ 2.
    set q_max to max(q_max, q).
    local r is r_body + h.
    local vt is vx + spin * r / r_body.
    local energy is (vt ^ 2 + vy ^ 2) / 2 - mu / r.
    local ecc is sqrt(max(0, 1 + 2 * energy * (r * vt) ^ 2 / mu ^ 2)).
    local ap is -mu / (2 * energy) * (1 + ecc) - r_body.
    local thrust is 0.
    if m > dry {
      set thrust to thrust_vac * (isp_vac - 70 * ratio) / isp_vac.
    }

    local t_floor is launch_throttle_floor(ratio, m, g, thrust).
    local tset is launch_throttle(ap, opt:target_ap, ramp, q, opt:max_q, t_floor).
    // Cut off within launch's top-up tolerance of target_ap too: launch_throttle only trickles there
    // (#234), which says nothing about the climb.
    if tset = 0 or ap >= opt:target_ap - max(100, 0.005 * opt:target_ap) {
      set coast_ap to ap.
      set cutoff to round(h) + " m, q " + round(q) + " Pa, " + round(90 - arctan2(vy, vx), 1) + " degrees from vertical".
      break.
    }

    local prograde is 90.
    if spd > 1 {
      set prograde to arctan2(vy, vx).
    }
    if turn_start_alt < 0 and spd >= opt:turn_start_speed {
      set turn_start_alt to h.
    }
    if opt:hold and turn_start_alt >= 0 and launch_turn_held(q, opt:max_q, ratio) {
      set turn_start_alt to h.
    }
    local pitch is 90.
    if turn_start_alt >= 0 {
      set pitch to launch_pitch(h, turn_start_alt, turn_end, opt:turn_shape).
    }
    if q > launch_aero_q() and spd > 30 {
      local allowed is launch_allowed_aoa(opt:max_aoa, q, launch_aoa_q_ref()).
      set pitch to max(prograde - allowed, min(prograde + allowed, pitch)).
    }
    if spd > 30 {
      set max_lead to max(max_lead, prograde - pitch).
    }

    local force is thrust * tset.
    if h > 100 and ratio > 0.1 and force > 0 {
      set min_twr to min(min_twr, force / (m * g)).
    }
    local mach is spd / (340 - 0.004 * min(h, 12000)).
    local transonic is (mach - 1.05) / 0.25.
    local drag is q * opt:drag_area * (1 + 1.2 * e_const ^ (-transonic * transonic)) / 1000.
    local ax is force * cos(pitch) / m.
    local ay is force * sin(pitch) / m - mu / r ^ 2 + vt ^ 2 / r.
    if spd > 0 {
      set ax to ax - drag / m * vx / spd.
      set ay to ay - drag / m * vy / spd.
    }
    set vx to vx + ax * dt.
    set vy to vy + ay * dt.
    set h to h + vy * dt.
    set m to m - flow * tset * dt.
    set t to t + dt.

    if h >= next_print {
      print "track " + round(h) + " m " + round(t) + " s vert " + round(90 - pitch, 1) + " prograde " + round(90 - prograde, 1) + " q " + round(q) + " throttle " + round(100 * tset) + " vs " + round(vy) + " ap " + round(ap).
      set next_print to next_print + 1000.
    }
    if vy < 0 {
      set stalled to ap < opt:target_ap - ramp.
      break.
    }
  }
  print "stalled " + stalled + " at " + round(h).
  print "coast_ap " + round(coast_ap).
  print "cutoff " + cutoff.
  print "max_q " + round(q_max).
  print "min_twr " + round(min_twr, 3).
  print "max_lead " + round(max_lead, 1).
}
