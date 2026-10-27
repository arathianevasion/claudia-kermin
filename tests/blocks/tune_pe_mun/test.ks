// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// tune_pe block test (patch next): the Mission Script the debug block test runner starts (bundled with
// the Core Library, as for a real Mission). The reference craft arrives in a 100 km circular
// Kerbin orbit with no stage fired. transfer_to puts it on a Mun encounter near 30 km; tune_pe must
// then move the Mun periapsis to 50 km within the default 1 km tolerance and 50 m/s.

@lazyglobal off.

wait until ship:unpacked.
local result is transfer_to(lexicon("target", "Mun", "target_pe", 30000, "stage_limit", 2)).
if result:success {
  local tuned is tune_pe(lexicon("value", 50000, "patch", "next", "stage_limit", 2)).
  print "tune_pe test: success " + tuned:success + ", Mun pe " + tuned:pe_actual + " m, burn at " + tuned:burn_ut + ", dv " + tuned:dv_used + ", " + tuned:reason.
  set result to tuned.
} else {
  print "tune_pe test: transfer_to failed: " + result:reason.
}
addons:claudia:report(result).
