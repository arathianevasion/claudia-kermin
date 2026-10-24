// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
// debug_tools test: runs for a minute with throttle and steering locked, so Abort can be tried on it.
print "claudia test: long run, press Abort".
lock steering to ship:facing.
lock throttle to 0.
local stop is time:seconds + 60.
until time:seconds > stop {
  print "claudia test: " + round(stop - time:seconds) + " s left".
  wait 1.
}
addons:claudia:report(lexicon("ok", true, "ran_s", 60)).
