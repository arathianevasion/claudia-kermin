// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// predict_burn scenario (tests/tools, debug_tools): the craft arrives in a 100 km circular Kerbin orbit with no stage
// fired. The script times a Hohmann burn to the Mun from the phase angle, asks predict_burn through the test-only bridge
// what it gives (with the Mun as target), then flies the same burn with exec_node and reports predicted against actual,
// which the runner logs to KSP.log. Spec #265: Ap and Pe within 1%, the same transition (an encounter with the Mun).

@lazyglobal off.

wait until ship:unpacked.

// Degrees the body leads the craft round their parent, 0 to 360, in the craft's direction of motion.
function phase_to {
  parameter b.
  local p1 is ship:position - ship:body:position.
  local p2 is b:position - ship:body:position.
  local angle is vang(p1, p2).
  if vdot(vcrs(p1, p2), vcrs(p1, ship:velocity:orbit)) < 0 { set angle to 360 - angle. }
  return angle.
}

local target_body is body("Mun").
local mu is ship:body:mu.
local r1 is ship:orbit:semimajoraxis.
local r2 is target_body:orbit:semimajoraxis.
local transfer_sma is (r1 + r2) / 2.
local transfer_time is constant:pi * sqrt(transfer_sma ^ 3 / mu).
local hohmann_dv is sqrt(mu * (2 / r1 - 1 / transfer_sma)) - sqrt(mu / r1).
// At the burn the Mun must lead the craft by what it covers during the half-orbit transfer, short of 180°.
local want is 180 - transfer_time * 360 / target_body:orbit:period.
local closing is 360 / ship:orbit:period - 360 / target_body:orbit:period.
local wait_s is mod(phase_to(target_body) - want + 720, 360) / closing.
// Leave exec_node time to turn and stage before the burn.
if wait_s < 300 { set wait_s to wait_s + 360 / closing. }
local ask is lexicon(
  "burns", list(lexicon("at", "in " + round(wait_s), "dv", lexicon("prograde", hohmann_dv, "normal", 0, "radial", 0))),
  "target", "Mun").

// ---- The same in every predict_burn scenario from here ----

// How far apart a predicted and an actual altitude are, as a percentage of the actual (or of 1 km, near zero); -1 when the
// prediction has none (an escape has no Ap).
function error_pct {
  parameter predicted, actual.
  if not predicted:istype("Scalar") { return -1. }
  return abs(predicted - actual) / max(abs(actual), 1000) * 100.
}

// Whether orbit o ends the way the first predicted patch does.
function transition_matches {
  parameter ends, o.
  if ends = "stable" { return o:transition = "FINAL" and o:periapsis > o:body:atm:height. }
  if ends = "escape" { return o:transition = "ESCAPE". }
  if ends = "atmosphere" { return o:periapsis < o:body:atm:height. }
  if ends = "impact" { return o:periapsis < 0. }
  if ends:startswith("encounter ") {
    if o:transition <> "ENCOUNTER" { return false. }
    return o:nextpatch:body:name = ends:substring(10, ends:length - 10).
  }
  return false.
}

local predicted is addons:claudia:planningtool("predict_burn", ask).
if predicted:haskey("error") {
  addons:claudia:report(lexicon("success", false, "reason", "predict_burn refused: " + predicted:error)).
} else {
  local burn is predicted:burns[0].
  local dv is ask:burns[0]:dv.
  add node(burn:at_ut, dv:radial, dv:normal, dv:prograde).
  // Stock's own patched conics for the same node, read before it is flown: the spec's 1% is against these. The flown
  // orbit (finite burn included) is compared too.
  local node_orbit is nextnode:orbit.
  local node_ap is node_orbit:apoapsis.
  local node_pe is node_orbit:periapsis.
  local node_transition is node_orbit:transition.
  local node_match is transition_matches(predicted:patches[0]:ends, node_orbit).
  local flown is exec_node(lexicon("stage_limit", 2)).
  local first is predicted:patches[0].
  local result is lexicon(
    "success", flown:success,
    "reason", flown:reason,
    "body", ship:body:name,
    "predicted_ap", first:ap, "actual_ap", ship:orbit:apoapsis, "ap_error_pct", error_pct(first:ap, ship:orbit:apoapsis),
    "predicted_pe", first:pe, "actual_pe", ship:orbit:periapsis, "pe_error_pct", error_pct(first:pe, ship:orbit:periapsis),
    "predicted_ends", first:ends, "actual_transition", ship:orbit:transition, "transition_match", transition_matches(first:ends, ship:orbit),
    "node_ap", node_ap, "node_ap_error_pct", error_pct(first:ap, node_ap),
    "node_pe", node_pe, "node_pe_error_pct", error_pct(first:pe, node_pe),
    "node_transition", node_transition, "node_transition_match", node_match,
    "predicted_burn_time", burn:burn_time, "actual_burn_time", flown:burn_time,
    "call_ms", predicted:call_ms, "snapshot_ms", predicted:snapshot_ms).
  if predicted:patches:length > 1 and ship:orbit:hasnextpatch {
    result:add("predicted_next_body", predicted:patches[1]:body).
    result:add("actual_next_body", ship:orbit:nextpatch:body:name).
    result:add("predicted_next_pe", predicted:patches[1]:pe).
    result:add("actual_next_pe", ship:orbit:nextpatch:periapsis).
  }
  if predicted:haskey("closest_approach") and predicted:closest_approach:istype("Lexicon") {
    result:add("predicted_closest_altitude", predicted:closest_approach:altitude).
  }
  print "predict_burn scenario: Ap " + first:ap + " predicted, " + round(ship:orbit:apoapsis) + " actual; Pe " + first:pe + " predicted, "
    + round(ship:orbit:periapsis) + " actual; " + first:ends + " against " + ship:orbit:transition.
  addons:claudia:report(result).
}
