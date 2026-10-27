// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, a biome target at Minmus (#281): as land_biome_mun, at Minmus, whose equatorial ground track crosses the
// Flats (see tests/tools/find_landing_site_minmus). The reference lander arrives in a 30 km circular equatorial Minmus orbit
// with no stage fired; land must find a daylight site in the Flats under the next 3 orbits, fly it, and come to rest there.

@lazyglobal off.

wait until ship:unpacked.
local result is land(lexicon("target", lexicon("biome", "Flats"), "daylight", true)).
set result["in_biome"] to result:biome = "Flats".
set result["sun_up"] to vang(ship:up:vector, body("Sun"):position) < 90.
set result["brakes_before_impact"] to result:burn_start_ut > 0 and (result:impact_ut < 0 or result:burn_start_ut < result:impact_ut).
print "land_biome test at Minmus: success " + result:success + ", in " + result:biome + ", " + result:distance_to_target + " m off the site, " + result:touchdown_speed_actual + " m/s, lean " + result:lean + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
