// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
// debug_tools test: reports a nested lexicon, to check it reaches C# intact.
print "claudia test: reporting".
local inner is lexicon("body", ship:body:name, "numbers", list(1, 2.5, 3)).
addons:claudia:report(lexicon("ok", true, "altitude", round(ship:altitude), "nested", inner)).
