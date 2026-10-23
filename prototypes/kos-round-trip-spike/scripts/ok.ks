// PROTOTYPE spike test: succeeds and reports.
print "claudia spike: ok test".
wait 1.
addons:claudia:report(lexicon("status", "ok", "answer", 42, "ut", time:seconds, "vessel", ship:name, "addon", addons:claudia:instance)).
