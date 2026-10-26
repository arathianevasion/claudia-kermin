// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// ascend block test: the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The runner spawns the reference lander in a 30 km circular
// equatorial Mun orbit, so this lands it anywhere first (daylight ignored), settles, then flies
// ascend to its default target_alt (5 km above the Mun's highest terrain) and an equatorial orbit.

@lazyglobal off.

wait until ship:unpacked.
local landed is land(lexicon("target", "any", "daylight", false)).
print "ascend test: landing " + landed:success + " " + landed:reason + ", lean " + landed:lean + " degrees, " + landed:reversals + " reversals".
// A lander leaning 15 degrees or more has tipped over (land itself fails only past 30): don't try
// to take off from it.
if landed:success and landed:lean >= 15 {
  set landed["success"] to false.
  set landed["reason"] to "ascend test: landed leaning " + landed:lean + " degrees from upright, 15 or more: tipped over".
}
if not landed:success {
  addons:claudia:report(landed).
} else {
  wait 5.
  print "ascend test: highest terrain " + round(addons:claudia:highestterrain(body)) + " m, biome " + addons:claudia:biome(ship:geoposition).
  local result is ascend(lexicon("target_inc", 0)).
  print "ascend test: success " + result:success + ", ap_error " + result:ap_error + " m, " + result:reason.
  // The landing's checks (test.cfg) ride along in ascend's result.
  for key in list("reversals", "touchdown_h_speed", "lean", "dv_used") {
    set result["land_" + key] to landed[key].
  }
  addons:claudia:report(result).
}
