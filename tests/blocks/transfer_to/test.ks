// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// transfer_to block test: the Mission Script the debug block test runner starts (bundled with the
// Core Library and RSVP, as for a real Mission). The reference craft arrives in a 100 km circular
// Kerbin orbit with no stage fired; the ejection burn stages into its engine (stage_limit 2 keeps
// the decoupler and chute). transfer_to must reach a Mun encounter near a 30 km periapsis.

@lazyglobal off.

wait until ship:unpacked.
local result is transfer_to(lexicon("target", "Mun", "target_pe", 30000, "stage_limit", 2)).
print "transfer_to test: success " + result:success + ", Mun pe " + result:target_pe_actual + " m, correction " + result:correction_dv + " m/s, " + result:reason.
print "transfer_to ejection: node " + result:ejection_node_dv + " m/s, burned " + result:ejection_dv_used + " m/s in " + result:ejection_burn_time + " s, " + result:ejection_wait + " s after the start".
addons:claudia:report(result).
