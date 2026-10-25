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
print "ascend test: landing " + landed:success + " " + landed:reason.
if not landed:success {
  addons:claudia:report(landed).
} else {
  wait 5.
  print "ascend test: highest terrain " + round(addons:claudia:highestterrain(body)) + " m, biome " + addons:claudia:biome(ship:geoposition).
  local result is ascend(lexicon("target_inc", 0)).
  print "ascend test: success " + result:success + ", ap_error " + result:ap_error + " m, " + result:reason.
  addons:claudia:report(result).
}
