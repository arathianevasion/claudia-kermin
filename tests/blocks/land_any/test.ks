// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// land block test, anywhere in daylight: the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). The reference lander arrives in a 30 km
// circular equatorial Mun orbit with no stage fired; land picks a spot about half an orbit on
// where the sun is up at touchdown, and lands there.

@lazyglobal off.

wait until ship:unpacked.
local result is land(lexicon("target", "any", "daylight", true)).
// The checker compares a field with a literal only: braking was planned to start before the orbit
// meets the ground (impact_ut -1: it clears the ground). #214's landers hit the Mun before braking.
set result["brakes_before_impact"] to result:burn_start_ut > 0 and (result:impact_ut < 0 or result:burn_start_ut < result:impact_ut).
print "land_any test: success " + result:success + ", " + result:distance_to_target + " m off, " + result:touchdown_speed_actual + " m/s, " + result:reason.
addons:claudia:report(result).
