// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// return block test with a landing target: the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). The reference craft arrives in a 30 km
// circular Mun orbit with no stage fired. return ejects it and corrects its path to land at KSC;
// reentry then brings it down. The Script Result is return's, plus where it actually came down:
// miss_km against the landing return predicted after its correction, target_km against KSC. KSP.log has
// the predictor's own lines.

@lazyglobal off.

wait until ship:unpacked.

// Great-circle km between two lat/lng lexicons on the body the craft is on.
function test_km {
  parameter from_geo, to_geo.
  local h is sin((to_geo:lat - from_geo:lat) / 2) ^ 2 + cos(from_geo:lat) * cos(to_geo:lat) * sin((to_geo:lng - from_geo:lng) / 2) ^ 2.
  return 2 * arcsin(min(1, sqrt(h))) * constant:pi / 180 * body:radius / 1000.
}

local aim is deorbit_ksc().
// Called as a value: a bare return(). would be kOS's return statement.
local result is return(lexicon("target", "ksc", "stage_limit", 2)).
print "return target test: success " + result:success + ", Kerbin pe " + result:kerbin_pe + " m, predicted " + result:predicted_geo + " (" + result:predicted_error_km + " km off KSC), " + result:reason.
// The checker compares a field with a literal only, so the test reports the comparisons it needs; a
// distance it never measured stays far over its check.
set result["landed_or_splashed"] to false.
set result["miss_km"] to 9999.
set result["target_km"] to 9999.
set result["actual_geo"] to lexicon().
if result:success {
  local down is reentry().
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
  print "return target test: came down at " + result:actual_geo + ", " + result:miss_km + " km from the prediction, " + result:target_km + " km from KSC".
}
addons:claudia:report(result).
