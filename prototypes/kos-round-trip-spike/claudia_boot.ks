// PROTOTYPE boot listener (start method B): runs each script path sent via CORE:MESSAGES.
print "claudia boot listener: waiting for messages".
until false {
  wait until not core:messages:empty.
  local m is core:messages:pop.
  print "claudia boot listener: running " + m:content.
  runpath(m:content).
  addons:claudia:done(m:content).
}
