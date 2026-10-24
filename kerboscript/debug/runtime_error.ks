// SPDX-License-Identifier: GPL-3.0-or-later
// debug_tools test: fails at run time, so its error text should come back from the terminal.
print "claudia test: about to fail".
print claudia_no_such_variable.
addons:claudia:report(lexicon("ok", false)).
