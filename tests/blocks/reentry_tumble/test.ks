// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// reentry block test, tumbling start: the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). The runner has set the probe spinning about its pitch
// axis (tumble_rate in test.cfg) before this starts, high in the atmosphere on a suborbital orbit, so
// reentry has to bring the heat shield forward on the reaction wheel, then drop it and open the chute
// and touch down. The Script Result is the block's plus the part counts before
// and after, to show the heat shield separated.

@lazyglobal off.

wait until ship:unpacked.
local parts_before is ship:parts:length.
local result is reentry().
set result["parts_before"] to parts_before.
set result["parts_after"] to ship:parts:length.
print "reentry tumble test: success " + result:success + ", touchdown " + result:touchdown_speed + " m/s, max g " + result:max_g + ", " + result:reason.
addons:claudia:report(result).
