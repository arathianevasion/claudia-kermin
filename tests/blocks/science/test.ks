// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// science block test: the Mission Script the debug block test runner starts (bundled with the Core
// Library, as for a real Mission). The probe arrives in a 100 km circular Kerbin orbit in Sandbox.
// science runs once, keeping the data, then again: the second call must run nothing, because every
// experiment now holds data. The Script Result is the first call's plus counts of the entries.

@lazyglobal off.

wait until ship:unpacked.
local first is science(lexicon("transmit", "keep")).
local second is science(lexicon("transmit", "keep")).
local ran is 0.
local kept is 0.
for entry in first:results {
  if entry:action = "ran" {
    set ran to ran + 1.
    if entry:transmit = "kept" {
      set kept to kept + 1.
    }
  }
}
local ran_again is 0.
for entry in second:results {
  if entry:action = "ran" {
    set ran_again to ran_again + 1.
  }
}
set first["ran"] to ran.
set first["kept"] to kept.
set first["ran_again"] to ran_again.
set first["success_again"] to second:success.
print "science test: success " + first:success + ", ran " + ran + ", kept " + kept + ", ran again " + ran_again + ", " + first:reason.
addons:claudia:report(first).
