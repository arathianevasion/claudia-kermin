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
// The first stage's vacuum delta-v before anything is staged: the scenario drains its tank so this is under half the node's
// (#355), which puts the burn's half-delta-v point in the second stage.
local stage1_dv is ship:stagedeltav(stage:number):vacuum.
add node(time:seconds + 600, 0, 2000, 0).
local result is exec_node(lexicon("stage_limit", 2)).
set result["test_stage1_dv"] to round(stage1_dv, 1).
// The turn flown against TURNTIME's prediction (spec #314: the actual must not exceed it).
set result["test_turn_within"] to result:turn_s <= result:turn_time.
print "exec_node_long test: first stage " + round(stage1_dv) + " m/s of the node's 2000, success " + result:success + ", late_start_s " + result:late_start_s + ", turn " + result:turn_s + " s of " + result:turn_time + " s predicted, " + result:reason.
addons:claudia:report(result).
