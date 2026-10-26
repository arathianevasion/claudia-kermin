// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// drop_empty block test (#279): launch from Kerbin's pad with drop_empty naming the tag "spent", which
// reference.craft carries on the first stage's tank only (the second stage's tank has none, and drop_empty
// must leave it alone). The shared auto-staging fires the first stage's decoupler once that tank is empty,
// during the launch burn. stage_limit 2 keeps the probe's decoupler and chute.

@lazyglobal off.

wait until ship:unpacked.
// The fuel left in the tagged tank the last time it was on the craft, and the parts it had.
local watching is true.
local fuel_last is -1.
local parts_before is ship:parts:length.
when true then {
  local tagged is ship:partstagged("spent").
  if tagged:length > 0 {
    local total is 0.
    for res in tagged[0]:resources {
      if res:name = "LiquidFuel" or res:name = "Oxidizer" {
        set total to total + res:amount.
      }
    }
    set fuel_last to total.
  }
  return watching.
}
local result is launch(lexicon("target_ap", 80000, "stage_limit", 2, "drop_empty", list("spent"))).
set watching to false.
local dropped_count is 0.
if result:haskey("dropped") {
  set dropped_count to result:dropped:length.
}
set result["test_dropped_count"] to dropped_count.
set result["test_fuel_at_drop"] to fuel_last.
set result["test_tagged_left"] to ship:partstagged("spent"):length.
set result["test_parts_lost"] to parts_before - ship:parts:length.
print "drop_empty test: success " + result:success + ", dropped " + dropped_count + ", fuel at drop " + round(fuel_last, 2) + ", " + result:reason.
addons:claudia:report(result).
