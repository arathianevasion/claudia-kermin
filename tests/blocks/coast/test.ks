// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// coast block test: the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The reference craft arrives in a 100 km circular Kerbin orbit
// with no stage fired; coast holds three orbits on rails warp, then exec_node flies a 60 m/s
// retrograde deorbit burn (stage_limit 2 keeps the decoupler and chute). One sequence, no Due Time.

@lazyglobal off.

wait until ship:unpacked.
local held is coast(lexicon("until", time:seconds + 3 * ship:orbit:period, "stage_limit", 2)).
local result is held.
if held:success {
  add node(time:seconds + 300, 0, 0, -60).
  set result to exec_node(lexicon("stage_limit", 2)).
  set result["coasted"] to held:coasted.
}
print "coast test: success " + result:success + ", coasted " + held:coasted + " s, " + result:reason.
addons:claudia:report(result).
