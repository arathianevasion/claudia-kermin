// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, a biome target at the Mun (#281): the Mission Script the debug block test runner starts (bundled with the
// Core Library, as for a real Mission). The reference lander arrives in a 30 km circular equatorial Mun orbit with no stage
// fired; land must find a daylight site in the Midlands under the next 3 orbits through its block-internal site search, fly
// it as a LATLNG, and come to rest in the Midlands. The script calls only land: the search is the block's, never the script's.

@lazyglobal off.

wait until ship:unpacked.
local result is land(lexicon("target", lexicon("biome", "Midlands"), "daylight", true)).
// The checker compares a field with a literal only, so the script reports the comparisons.
set result["in_biome"] to result:biome = "Midlands".
set result["sun_up"] to vang(ship:up:vector, body("Sun"):position) < 90.
set result["brakes_before_impact"] to result:burn_start_ut > 0 and (result:impact_ut < 0 or result:burn_start_ut < result:impact_ut).
print "land_biome test at the Mun: success " + result:success + ", in " + result:biome + ", " + result:distance_to_target + " m off the site, " + result:touchdown_speed_actual + " m/s, lean " + result:lean + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
