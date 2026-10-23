// PROTOTYPE spike test: runtime error before the report.
print "claudia spike: runtime error test".
wait 0.5.
print undefined_spike_variable.
addons:claudia:report(lexicon("status", "unreachable")).
