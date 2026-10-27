// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// solve_burn scenario (tests/tools, debug_tools): the craft arrives in a 100 km circular Kerbin orbit with no stage fired.
// The script asks solve_burn, through the test-only bridge, for the burn that sets the Ap to 250 km, flies it with exec_node
// (stage_limit 2 keeps the decoupler and chute), then asks for the burn that circularizes at that Ap and flies that too. It
// reports each goal's target against the orbit actually flown, which the runner logs to KSP.log. Spec #265: closed-form
// goals match vis-viva, so the flown orbit lands on the target within the finite burn's error.

@lazyglobal off.

wait until ship:unpacked.

// How far apart a wanted and an actual altitude are, as a percentage of the actual (or of 1 km, near zero).
function error_pct {
  parameter wanted, actual.
  return abs(wanted - actual) / max(abs(actual), 1000) * 100.
}

// Asks solve_burn for a goal, puts its burn on the flight plan and flies it. Gives the solve_burn answer and exec_node's.
function solve_and_fly {
  parameter ask.
  local solved is addons:claudia:planningtool("solve_burn", ask).
  if solved:haskey("error") {
    return lexicon("solved", solved, "flown", lexicon("success", false, "reason", "solve_burn refused: " + solved:error, "burn_time", -1)).
  }
  add node(solved:at_ut, solved:dv:radial, solved:dv:normal, solved:dv:prograde).
  return lexicon("solved", solved, "flown", exec_node(lexicon("stage_limit", 2))).
}

local goal_ap is 250000.
local first is solve_and_fly(lexicon("goal", "set_ap", "value", goal_ap)).
local first_ap is ship:orbit:apoapsis.
local first_pe is ship:orbit:periapsis.

local second is lexicon("solved", lexicon("error", "skipped: the Ap burn did not fly"),
  "flown", lexicon("success", false, "reason", "skipped", "burn_time", -1)).
if first:flown:success { set second to solve_and_fly(lexicon("goal", "circularize", "at", "ap")). }

local result is lexicon(
  "success", first:flown:success and second:flown:success,
  "reason", first:flown:reason + " / " + second:flown:reason,
  "body", ship:body:name,
  "goal_ap", goal_ap, "actual_ap", first_ap, "ap_error_pct", error_pct(goal_ap, first_ap),
  "goal_pe", 100000, "actual_pe", first_pe, "pe_error_pct", error_pct(100000, first_pe),
  "circle_ap", ship:orbit:apoapsis, "circle_pe", ship:orbit:periapsis,
  "circle_ap_error_pct", error_pct(goal_ap, ship:orbit:apoapsis), "circle_pe_error_pct", error_pct(goal_ap, ship:orbit:periapsis),
  "first_burn_time", first:flown:burn_time, "second_burn_time", second:flown:burn_time,
  "solved_first_dv", -1, "solved_second_dv", -1, "call_ms", -1).
if not first:solved:haskey("error") {
  set result["solved_first_dv"] to first:solved:magnitude.
  set result["call_ms"] to first:solved:call_ms.
  result:add("solved_first_burn_time", first:solved:burn_time).
}
if not second:solved:haskey("error") {
  set result["solved_second_dv"] to second:solved:magnitude.
  result:add("solved_second_burn_time", second:solved:burn_time).
}
print "solve_burn scenario: Ap " + goal_ap + " wanted, " + round(first_ap) + " actual; then circle " + goal_ap + " wanted, Ap "
  + round(ship:orbit:apoapsis) + " Pe " + round(ship:orbit:periapsis) + " actual.".
addons:claudia:report(result).
