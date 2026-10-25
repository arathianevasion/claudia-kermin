// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch + circularize block test: one Maneuver Sequence from Kerbin's pad to an 80 km orbit, as
// Claudia would write it. The two-stage reference craft starts on the pad with no stage fired;
// stage_limit 2 keeps the probe's decoupler and chute. The Script Result is circularize's, with
// launch's result under launch_ names.

@lazyglobal off.

wait until ship:unpacked.
// The angle off the airflow over the climb, one sample per 500 m, so a loss of control shows where it began.
local start_ut is time:seconds.
local aoa_track is list().
local next_sample is 500.
local watching is true.
when true then {
  if altitude >= next_sample and altitude < 40000 {
    aoa_track:add(round(altitude) + " m, " + round(time:seconds - start_ut) + " s: " + round(vang(ship:facing:forevector, ship:srfprograde:vector), 1) + " deg off the airflow, " + round(vang(ship:facing:forevector, ship:up:vector), 1) + " off vertical, q " + round(ship:q * constant:atmtokpa, 1) + " kPa, thrust " + round(100 * ship:thrust / max(0.001, ship:availablethrust)) + "%").
    set next_sample to next_sample + 500.
  }
  return watching.
}
local ascent is launch(lexicon("target_ap", 80000, "stage_limit", 2)).
set watching to false.
print "launch: success " + ascent:success + ", ap_error " + ascent:ap_error + " m, max_q " + ascent:max_q + " Pa, " + ascent:reason.
local result is ascent.
if ascent:success {
  set result to circularize(lexicon("at", "ap", "stage_limit", 2)).
  print "circularize: success " + result:success + ", ecc " + result:ecc + ", " + result:reason.
}
for key in ascent:keys {
  set result["launch_" + key] to ascent[key].
}
set result["test_aoa_track"] to aoa_track.
addons:claudia:report(result).
