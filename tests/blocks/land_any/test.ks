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
print "land_any test: success " + result:success + ", " + result:distance_to_target + " m off, " + result:touchdown_speed_actual + " m/s, " + result:reason.
addons:claudia:report(result).
