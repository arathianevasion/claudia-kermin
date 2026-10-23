// PROTOTYPE spike test: RSVP v9 smoke test on kOS 1.6.0.1. Needs 0:/ (home connection) and a Kerbin orbit.
print "claudia spike: RSVP v9 -> Mun".
set config:ipu to 2000.
runoncepath("0:/rsvp/main").
local result is rsvp:goto(mun, lexicon("create_maneuver_nodes", "first", "verbose", true)).
addons:claudia:report(result).
