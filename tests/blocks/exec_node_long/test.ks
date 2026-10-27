// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// exec_node_long block test (#343): the Mission Script the debug block test runner starts (bundled with
// the Core Library, as for a real Mission). The two-stage reference craft arrives in a 100 km circular
// Kerbin orbit with no stage fired; exec_node must stage into its first engine, turn to a 2000 m/s
// normal node 10 minutes ahead, warp, start the burn on time from both stages' delta-v, and stage into
// the second engine on flameout (stage_limit 2 keeps the probe's decoupler and chute).

@lazyglobal off.

wait until ship:unpacked.
add node(time:seconds + 600, 0, 2000, 0).
local result is exec_node(lexicon("stage_limit", 2)).
// The turn flown against TURNTIME's prediction (spec #314: the actual must not exceed it).
set result["test_turn_within"] to result:turn_s <= result:turn_time.
print "exec_node_long test: success " + result:success + ", late_start_s " + result:late_start_s + ", turn " + result:turn_s + " s of " + result:turn_time + " s predicted, " + result:reason.
addons:claudia:report(result).
