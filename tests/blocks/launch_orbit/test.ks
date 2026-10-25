// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// launch + circularize block test: one Maneuver Sequence from Kerbin's pad to an 80 km orbit, as
// Claudia would write it. The two-stage reference craft starts on the pad with no stage fired;
// stage_limit 2 keeps the probe's decoupler and chute. The Script Result is circularize's, with
// launch's result under launch_ names.

@lazyglobal off.

wait until ship:unpacked.
local ascent is launch(lexicon("target_ap", 80000, "stage_limit", 2)).
print "launch: success " + ascent:success + ", ap_error " + ascent:ap_error + " m, max_q " + ascent:max_q + " Pa, " + ascent:reason.
local result is ascent.
if ascent:success {
  set result to circularize(lexicon("at", "ap", "stage_limit", 2)).
  print "circularize: success " + result:success + ", ecc " + result:ecc + ", " + result:reason.
}
for key in ascent:keys {
  set result["launch_" + key] to ascent[key].
}
addons:claudia:report(result).
