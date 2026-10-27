// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// exec_node block test: the Mission Script the debug block test runner starts (bundled with the
// Core Library, as for a real Mission). The reference craft arrives in a 100 km circular Kerbin
// orbit with no stage fired; exec_node must stage into its engine (stage_limit 2 keeps the
// decoupler and chute) and fly a 120 m/s prograde node 10 minutes ahead.

@lazyglobal off.

wait until ship:unpacked.
add node(time:seconds + 600, 0, 0, 120).
local result is exec_node(lexicon("stage_limit", 2)).
// The turn flown against TURNTIME's prediction (spec #314: the actual must not exceed it).
set result["test_turn_within"] to result:turn_s <= result:turn_time.
print "exec_node test: success " + result:success + ", residual " + result:residual + " m/s, turn " + result:turn_s + " s of " + result:turn_time + " s predicted, " + result:reason.
addons:claudia:report(result).
