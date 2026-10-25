# RSVP v9, bundled

[RSVP](https://github.com/maneatingape/rsvp) by maneatingape, tag `v9` (see `VERSION.txt`), is
licensed GPL-3.0 (`LICENSE.md`, kept with it). The `transfer` Building Block uses it to plan the
ejection burn. These files were copied from an RSVP v9 install's `Ships/Script/rsvp/`.

## What is unmodified

`hill_climb.ks`, `lambert.ks`, `maneuver.ks`, `orbit.ks`, `refine.ks`, `search.ks`, `transfer.ks` and
`validate.ks` are RSVP's files byte for byte. `RsvpBundleTests` in the Core tests pins their git
blob ids, so an edit fails the build's tests. `main.ks.v9` is RSVP's original `main.ks`, kept for
comparison; it is not bundled.

## What was rewritten, and why: the loading only

RSVP loads itself from files: `main.ks` declares the global `rsvp` lexicon and then `import()` runs
each other file with `runoncepath(scriptpath():parent:combine(name), export@)`; each file takes
`export` as its parameter and adds its functions to `rsvp`. `main.ks` also has `compile_to`, which
compiles every file onto a volume.

That can't work, or pass the Script Check, in Claudia Kermin, where a Mission Script and the whole
Core Library are compiled in memory into one unit and nothing is read from or written to a disk:

- `runoncepath` and `scriptpath()` need the files on a volume; the bundle has none. The Script Check
  also forbids running files (`RUN`), and `compile_to`'s `exists`, `open` and `compile` touch files.
- `compile_to` writes to whichever volume it is given, the Archive included; RSVP running inside a
  Mission must never write to the Archive (#132, and ADR 0003's Archive-write gate).
- Each RSVP file is a program of its own: `parameter export.` at its top, file-level locals, and
  `local function`s whose names repeat across files. Pasted together at file level they would clash.

So, and only so:

1. `main.ks` here is RSVP's `main.ks` with `import()` rewritten to call `rsvp_file_<name>(export@)`
   for each file, and with `compile_to` and the source file list (only those two used it) left out.
   `goto` and `export` are unchanged.
2. When the core bundles a Mission Script (`CoreLibrary.Bundle`), each RSVP file goes in whole as
   the body of `function rsvp_file_<name> { ... }`, minus its `@lazyglobal off.` line (kOS allows
   that only at the top of the unit, and the Mission Script's own comes first). A file's
   `parameter export.` becomes that function's parameter, its file-level locals and functions become
   the function's, and kOS closures keep them alive as RSVP's file scopes did. `rsvp_file_main()`
   declares the global `rsvp` and imports the rest.
3. `transfer_rsvp()` (in `transfer.ks`) calls `rsvp_file_main()` the first time a block needs RSVP,
   because a bundled file's top-level statements would only run after the Mission Script ends.

RSVP's planning code itself is untouched. While running it prints (with `verbose`, which `transfer`
leaves off) and adds and removes maneuver nodes; it writes no files.
