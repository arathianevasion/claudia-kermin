// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch + circularize block test: one Maneuver Sequence from Kerbin's pad to an 80 km orbit, as
// Claudia would write it. The two-stage reference craft starts on the pad with no stage fired;
// stage_limit 2 keeps the probe's decoupler and chute. The Script Result is circularize's, with
// launch's result under launch_ names.

@lazyglobal off.

wait until ship:unpacked.
local watching is true.
// test_aoa_track: one sample per 500 m of climb, "alt m t s aoa deg off-vertical deg q Pa thrust %"
// joined by "; ", and test_stagings: the altitude of each staging, for #224's control-loss diagnosis.
local aoa_track is "".
local stagings is "".
local next_sample is 0.
local last_stage is stage:number.
when true then {
  if altitude >= next_sample {
    set aoa_track to aoa_track + round(altitude) + " m " + round(missiontime) + " s "
      + round(vang(ship:facing:forevector, ship:srfprograde:vector), 1) + " aoa "
      + round(vang(ship:facing:forevector, ship:up:vector), 1) + " vert "
      + round(ship:q * constant:atmtokpa * 1000) + " Pa "
      + round(ship:thrust / max(ship:availablethrust, 0.001) * 100) + " %; ".
    set next_sample to next_sample + 500.
  }
  if stage:number <> last_stage {
    set stagings to stagings + "stage " + stage:number + " at " + round(altitude) + " m; ".
    set last_stage to stage:number.
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
set result["test_stagings"] to stagings.
print "stagings: " + stagings.
addons:claudia:report(result).
