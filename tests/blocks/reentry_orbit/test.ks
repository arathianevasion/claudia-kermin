// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, return from orbit: the Mission Script the debug block test runner starts
// (bundled with the Core Library, as for a real Mission). The probe arrives at the apoapsis of a
// 150 km by 20 km orbit; reentry coasts to the atmosphere, flies the entry and touches down under a
// parachute. jettison_stage 1 names the heat shield's decoupler stage on the reference craft. The
// Script Result is the block's plus the part counts before and after, to show the heat shield went.

@lazyglobal off.

wait until ship:unpacked.
local parts_before is ship:parts:length.
local result is reentry(lexicon("jettison_stage", 1)).
set result["parts_before"] to parts_before.
set result["parts_after"] to ship:parts:length.
print "reentry orbit test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, max g " + result:max_g + ", " + result:reason.
addons:claudia:report(result).
