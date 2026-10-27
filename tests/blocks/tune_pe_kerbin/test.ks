// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// tune_pe block test (patch current): the Mission Script the debug block test runner starts (bundled
// with the Core Library, as for a real Mission). The reference craft arrives in a 30 km circular Mun
// orbit with no stage fired. return puts it on the way home with a Kerbin periapsis near 35 km; tune_pe
// must then set that periapsis to 45 km. The 35 km periapsis is inside the atmosphere and comes before the
// apoapsis, so it must burn before the periapsis (not at the apoapsis, which the craft would never reach).

@lazyglobal off.

wait until ship:unpacked.
// Called as a value: a bare return(). would be kOS's return statement.
local result is return(lexicon("stage_limit", 2)).
if result:success {
  local tuned is tune_pe(lexicon("value", 45000, "patch", "current", "stage_limit", 2)).
  print "tune_pe test: success " + tuned:success + ", Kerbin pe " + tuned:pe_actual + " m, burn at " + tuned:burn_ut + ", dv " + tuned:dv_used + ", " + tuned:reason.
  set result to tuned.
} else {
  print "tune_pe test: return failed: " + result:reason.
}
addons:claudia:report(result).
