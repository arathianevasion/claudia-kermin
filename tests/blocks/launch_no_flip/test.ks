// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch no-flip block test: the #41 failure (a craft with no fins flipped in thick air after
// throttling down). The finless reference craft launches from the pad to a 60 km apoapsis with
// throttle_max_q forcing a throttle-down in thick air. A trigger watches the whole flight: the worst
// angle between the nose and the airflow while the air matters (test_max_aoa), and the lowest
// thrust-to-weight while an engine burns in thick air (test_min_twr: the throttle must not collapse).

@lazyglobal off.

wait until ship:unpacked.
local g0 is ship:body:mu / ship:body:radius ^ 2.
local p0 is ship:body:atm:altitudepressure(0).
local worst_aoa is 0.
local min_twr is 100.
local watching is true.
when true then {
  if ship:q * constant:atmtokpa * 1000 > 500 and ship:velocity:surface:mag > 30 {
    set worst_aoa to max(worst_aoa, vang(ship:facing:forevector, ship:srfprograde:vector)).
  }
  if ship:thrust > 0 and altitude > 100 and ship:body:atm:altitudepressure(altitude) > 0.1 * p0 {
    set min_twr to min(min_twr, ship:thrust / (ship:mass * g0)).
  }
  return watching.
}
local result is launch(lexicon("target_ap", 60000, "throttle_max_q", 12000, "stage_limit", 2)).
set watching to false.
set result["test_max_aoa"] to round(worst_aoa, 2).
set result["test_min_twr"] to round(min_twr, 3).
print "launch no-flip test: success " + result:success + ", worst aoa " + result:test_max_aoa + ", min twr " + result:test_min_twr + ", " + result:reason.
addons:claudia:report(result).
