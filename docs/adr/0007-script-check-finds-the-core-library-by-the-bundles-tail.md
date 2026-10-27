# The Script Check finds the Core Library by the bundle's tail

ADR 0006 lets Building Blocks call the mod's calculations at run time (block-internal calls, e.g. `ADDONS:CLAUDIA:LANDINGSITE` for `land` with a biome target) and has the Script Check refuse those calls in Mission Scripts. But the check never sees a Mission Script alone: the mod bundles it with the Core Library into one compile unit (`CoreLibrary.Bundle`), and checks and runs that. So the check has to tell, opcode by opcode, whose code a call is in.

We decided it by **source line**. The bundle is always the Mission Script, a blank line, then the Core Library, RSVP and the vendored scripts, and that tail is the same text for every Mission Script. The check looks for the whole tail at the end of what it is given (`CoreLibrary.LibraryStartLine`); every opcode on a line before it is the Mission Script's, and a block-internal suffix there is refused. A source that doesn't end with the tail is all Mission Script. Matching the whole tail, not a banner line, means a script that copies the library's text or banner gains nothing: its lines still come before the real tail.

We rejected:
- **A runtime gate on the caller** (refuse the suffix unless a Core Library function is on the kOS call stack). It would also catch computed calls, but there are none to catch: kOS has no computed suffix names, and every way to reach a suffix (`addons:claudia:x`, a variable holding `addons:claudia`, `x@`) compiles to the suffix's name. It would add another patch on kOS internals (ADR 0003's cost) for nothing.
- **A naming rule** (only functions named `land_...` may call it). A Mission Script can name its own functions anything.
- **Checking the Mission Script and the library apart.** The script calls blocks defined in the library, so it doesn't compile alone the way it runs.

The cost:
- **The check depends on the bundle's layout.** If `Bundle` changes, `LibraryStartLine` changes with it (it is computed from `Bundle("")`); a test pins that a Mission Script line in a bundle is still refused.
- **A Core Library helper is reachable.** A Mission Script can call any library function, so a block-internal call sits inline in the block that uses it (`land`), not in a helper a script could call to get the search without the block. Pure helpers (building the query) are fine to share.
- **A Mission Script must not replace library functions.** kOS keeps the first file-level function of a name, which is the Mission Script's, and a `lock` of a function's name replaces it when its line runs. Either would put the script's own code inside a block, round the call (a `land_plan` that keeps the site and fails), so the check refuses, in a bundle, any function or lock the Mission Script defines with a Core Library function's name ("redefine a Core Library function"). Function definitions are all stored on the script's first line, so the check finds where one's code starts from the label its definition pushes.
- **CI checks library files alone** (`KsCompile` over `kerboscript/library`): those are checked as all-library (`KosScriptCheck(forCoreLibrary: true)`). The mod never uses that mode.

The calls only read the game, like the Planning Tools beneath them, so there is no runtime gate on them: the player's terminal may call them too.

Decided in #281, from spec #265.
