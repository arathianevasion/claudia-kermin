// PROTOTYPE spike test: does a program and its triggers survive on-rails warp, including an SOI change?
// With an ENCOUNTER or ESCAPE on the current orbit, warps to 2 min past the SOI change; otherwise warps 6 h.
print "claudia spike: on-rails warp test".
local startBody is ship:body:name.
local transition is ship:orbit:transition.
local soiTriggerUt is -1.
local tickTriggers is 0.
local realStart is kuniverse:realtime.
local gameStart is time:seconds.
when ship:body:name <> startBody then {
  set soiTriggerUt to time:seconds.
  print "trigger: SOI change to " + ship:body:name + " at warp " + kuniverse:timewarp:rate.
}
when true then {
  set tickTriggers to tickTriggers + 1.
  return true.
}
local target is time:seconds + 6 * 3600.
if transition = "ENCOUNTER" or transition = "ESCAPE" {
  set target to time:seconds + eta:transition + 120.
}
print "transition=" + transition + "; warping " + round(target - time:seconds) + " s".
kuniverse:timewarp:warpto(target).
wait until time:seconds >= target.
wait until kuniverse:timewarp:issettled.
addons:claudia:report(lexicon(
  "status", "ok",
  "transition", transition,
  "start_body", startBody,
  "end_body", ship:body:name,
  "soi_trigger_ut", soiTriggerUt,
  "tick_trigger_count", tickTriggers,
  "game_seconds", round(time:seconds - gameStart),
  "real_seconds", round(kuniverse:realtime - realStart))).
