// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, a chosen site: the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). The reference lander arrives in a 30 km circular
// equatorial Mun orbit with no stage fired; land must stage into its engine and land on the
// equator three quarters of an orbit ahead (so the deorbit burn is about a quarter orbit away
// whatever the spawn phase), ignoring daylight so the run doesn't depend on the time of day.

@lazyglobal off.

wait until ship:unpacked.
local site is latlng(0, land_wrap_lng(ship:geoposition:lng + 270)).
print "land test: aiming for " + round(site:lat, 3) + ", " + round(site:lng, 3).
local result is land(lexicon("target", site, "daylight", false)).
// The checker compares a field with a literal only: braking began before the orbit
// meets the ground (impact_ut -1: it clears the ground). #214's landers hit the Mun before braking.
set result["brakes_before_impact"] to result:braking_began_ut > 0 and (result:impact_ut < 0 or result:braking_began_ut < result:impact_ut).
print "land test: success " + result:success + ", " + result:distance_to_target + " m off, " + result:touchdown_speed_actual + " m/s (" + result:touchdown_h_speed + " sideways), " + result:reversals + " reversals, lean " + result:lean + ", dv " + result:dv_used + ", " + result:reason.
addons:claudia:report(result).
