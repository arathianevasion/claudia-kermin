// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch vertical hop block test: the reference craft (finned, for its unpowered coast in thick air)
// climbs straight up from the pad to an 8 km apoapsis (turn_start_speed above anything it
// reaches), cuts off in thick air and coasts to the top of the climb, where launch ends. A trigger watches the worst tilt from vertical (test_max_tilt:
// the coast must be smooth, no flip) and the lowest thrust-to-weight while an engine burns
// (test_min_twr: the throttle must not collapse before cutoff).

@lazyglobal off.

wait until ship:unpacked.
local g0 is ship:body:mu / ship:body:radius ^ 2.
local worst_tilt is 0.
local min_twr is 100.
local watching is true.
// The angle off the airflow over the climb, one sample per 500 m, so a loss of control shows where it began.
local start_ut is time:seconds.
local aoa_track is list().
local next_sample is 500.
when true then {
  if altitude >= next_sample and altitude < 40000 {
    aoa_track:add(round(altitude) + " m, " + round(time:seconds - start_ut) + " s: " + round(vang(ship:facing:forevector, ship:srfprograde:vector), 1) + " deg off the airflow, " + round(vang(ship:facing:forevector, ship:up:vector), 1) + " off vertical, q " + round(ship:q * constant:atmtokpa, 1) + " kPa, thrust " + round(100 * ship:thrust / max(0.001, ship:availablethrust)) + "%").
    set next_sample to next_sample + 500.
  }
  if altitude > 100 {
    set worst_tilt to max(worst_tilt, vang(ship:facing:forevector, ship:up:vector)).
  }
  if ship:thrust > 0 and altitude > 100 {
    set min_twr to min(min_twr, ship:thrust / (ship:mass * g0)).
  }
  return watching.
}
local result is launch(lexicon("target_ap", 8000, "turn_start_speed", 100000, "stage_limit", 2)).
set watching to false.
set result["test_max_tilt"] to round(worst_tilt, 2).
set result["test_min_twr"] to round(min_twr, 3).
set result["test_top_alt"] to round(altitude).
set result["test_aoa_track"] to aoa_track.
print "launch hop test: success " + result:success + ", top " + result:test_top_alt + " m, worst tilt " + result:test_max_tilt + ", min twr " + result:test_min_twr + ", " + result:reason.
addons:claudia:report(result).
