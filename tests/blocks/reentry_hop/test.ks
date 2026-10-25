// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, suborbital hop: the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). The probe arrives at the 100 km apoapsis of an orbit
// that ends underground, so reentry waits a short while, then flies the fall: heat shield forward,
// shield dropped, parachute opened by speed and pressure, touchdown. The Script Result is the block's
// plus the part counts before and after, to show the heat shield separated.

@lazyglobal off.

wait until ship:unpacked.
local parts_before is ship:parts:length.
local result is reentry().
set result["parts_before"] to parts_before.
set result["parts_after"] to ship:parts:length.
// The checker compares a field with a literal only, so the test reports the comparisons it needs: how
// many parts went (the heat shield), and whether it came down on land or water (geo:alt is height
// above sea level, so it says nothing about a touchdown on high ground).
set result["parts_dropped"] to parts_before - ship:parts:length.
set result["landed_or_splashed"] to status = "LANDED" or status = "SPLASHED".
print "reentry hop test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, max g " + result:max_g + ", " + result:reason.
addons:claudia:report(result).
