// PROTOTYPE spike test: runs 300 s so you can dock, undock, stage or abort mid-run.
print "claudia spike: long run (300 s). Dock / undock / stage / abort now.".
local t0 is time:seconds.
local n is 0.
until time:seconds > t0 + 300 {
  set n to n + 1.
  print "tick " + n + "  vessel=" + ship:name + "  addon#" + addons:claudia:instance + "      " at (0, 12).
  wait 1.
}
addons:claudia:report(lexicon("status", "ok", "ticks", n, "vessel", ship:name, "addon", addons:claudia:instance)).
