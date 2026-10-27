// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// exec_node_soi block test (#344): the Mission Script the debug block test runner starts (bundled with
// the Core Library, as for a real Mission). The reference craft arrives in a 50 km circular Mun orbit
// with no stage fired. A 300 m/s prograde node escapes the Mun; once that is flown, the orbit has an
// SOI change ahead, and a 20 m/s prograde node 900 s past it is flown by exec_node, which must warp to
// the change, settle, turn, and warp on, starting the burn on time.

@lazyglobal off.

wait until ship:unpacked.
add node(time:seconds + 120, 0, 0, 300).
local result is exec_node(lexicon("stage_limit", 2)).
if result:success {
  if ship:orbit:hasnextpatch {
    add node(time:seconds + ship:orbit:nextpatcheta + 900, 0, 0, 20).
    set result to exec_node(lexicon("stage_limit", 2)).
  } else {
    set result["success"] to false.
    set result["reason"] to "no SOI change after the escape burn".
  }
}
set result["test_past_soi"] to ship:body:name = "Kerbin".
set result["test_turn_within"] to result:turn_s <= result:turn_time.
print "exec_node_soi test: success " + result:success + ", late_start_s " + result:late_start_s + ", body " + ship:body:name + ", " + result:reason.
addons:claudia:report(result).
