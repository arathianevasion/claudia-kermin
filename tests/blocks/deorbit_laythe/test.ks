// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// deorbit block test, Laythe: the Mission Script the debug block test runner starts (bundled with the
// Core Library, as for a real Mission). The reference craft arrives in an 80 km circular equatorial
// orbit with no stage fired. deorbit chooses and flies the burn for 0 N, 0 E within 6 h (Laythe turns
// once in about 14.7 h, so "auto" would allow that long) for the probe capsule alone, flies it, then
// drops the spent tank and engine (separate 1); reentry then brings the capsule down under its
// parachute (#326). The Script Result is deorbit's, plus where it actually came down: miss_km against
// the landing deorbit predicted after its burn and separation, target_km against the target. KSP.log
// has the predictor's own lines.

@lazyglobal off.

wait until ship:unpacked.

// Great-circle km between two lat/lng lexicons on the body the craft is on.
function test_km {
  parameter from_geo, to_geo.
  local h is sin((to_geo:lat - from_geo:lat) / 2) ^ 2 + cos(from_geo:lat) * cos(to_geo:lat) * sin((to_geo:lng - from_geo:lng) / 2) ^ 2.
  return 2 * arcsin(min(1, sqrt(h))) * constant:pi / 180 * body:radius / 1000.
}

local aim is lexicon("lat", 0, "lng", 0).
local result is deorbit(lexicon("target", aim, "max_wait", 21600, "stage_limit", 2, "separate", 1)).
print "deorbit test: success " + result:success + ", burn at " + result:burn_ut + ", dv " + result:dv_used + ", predicted " + result:predicted_geo + " (" + result:predicted_error_km + " km off), " + result:reason.
// The checker compares a field with a literal only, so the test reports the comparisons it needs; a
// distance it never measured stays far over its check.
set result["landed_or_splashed"] to false.
set result["miss_km"] to 9999.
set result["target_km"] to 9999.
set result["actual_geo"] to lexicon().
if result:burn_ut > 0 {
  local down is reentry(lexicon("jettison_stage", "none")).
  set result["reentry_success"] to down:success.
  set result["reentry_reason"] to down:reason.
  set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
  if down:haskey("geo") {
    set result["actual_geo"] to down:geo.
    set result["target_km"] to round(test_km(aim, down:geo), 2).
    if result:predicted_geo:haskey("lat") {
      set result["miss_km"] to round(test_km(result:predicted_geo, down:geo), 2).
    }
  }
  print "deorbit test: came down at " + result:actual_geo + ", " + result:miss_km + " km from the prediction, " + result:target_km + " km from the target".
}
addons:claudia:report(result).
