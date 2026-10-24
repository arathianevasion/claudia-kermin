# PROTOTYPE results: kOS round-trip spike

Two in-game passes on 2026-10-23 and 2026-10-24 with KSP 1.12.5 (macOS), kOS 1.6.0.1 and RSVP v9. The evidence is in [`logs/pass1.txt`](logs/pass1.txt) and [`logs/pass2.txt`](logs/pass2.txt), which hold the `[ClaudiaSpike]` and kOS error lines from `KSP.log`.

## Verdict

The round trip that **kOS handoff: delivering and running Claude-written scripts** recommended works. C# writes a script to the processor's local disk and starts it. The script returns its result through `ADDONS:CLAUDIA:REPORT(lexicon)`. When a run ends with no report, that reliably means failed, aborted or interrupted.

## Findings

| Question | Result |
|---|---|
| Round trip | Works with all three start methods: A (keystrokes), B (boot listener plus `CORE:MESSAGES`) and C (pushing a program context directly, the way `CPU.Boot()` does). The lexicon arrives intact, including nested lexicons (see RSVP below). |
| Delivery | `HardDisk.SaveFile` to the local disk works anywhere. `0:/` worked on the pad. `0:/` with no connection was not tested, and doesn't need to be if v1 always uses the local disk. |
| Detecting the end of a run | `IPopContextNotifyee.OnPopContext` fires for normal ends, runtime errors, compile errors, `BreakExecution(true)` aborts and scene changes. A report before the pop means OK. No report means failed. |
| Error text | `KSP.log` gets the file, line, caret and exception type (`KOSUndefinedIdentifierException: Undefined Variable Name '…'`, `KOSParseException … at line 3`). The terminal buffer also has the text, with two catches. kOS prints the error *after* the context pops, so read it on a later frame. And each `IScreenBufferLine.ToString()` is padded with NULs, so replace the `'\0'` characters before using it. Unity's log cuts a message off at the first NUL, which is why the spike first showed only one line. |
| Start method B | Fragile. A runtime error in the launched script aborts *every* program on the processor, including the boot listener. Later messages then sit in `CORE:MESSAGES` and nothing runs and nothing errors. While the listener runs, the interpreter is busy, so method C is refused. A boot file set from C# (`bootFile` plus a power cycle) works but doesn't survive a revert to launch, which reloads the craft as built. |
| Start method C | Clean. It needs an idle interpreter. `Interpreter.IsWaitingForCommand()` **throws** `ArgumentOutOfRangeException` until the processor's first `Update()` (about 1 s after a Flight scene loads), so guard it. |
| Abort | `Cpu.BreakExecution(true)` stops a running program at once, and the pop notifyee fires. Only the mod knows the run was an abort, so it has to record that itself. |
| Addon instances | One per processor, created when the processor initialises. Every scene load (including a revert or a trip to the Space Center) creates new instances. The old ones stay behind with destroyed processors, and `KOSCoreId` changes each load. Key Claudia's state by part, not by addon instance or core ID. |
| Dock and undock | The same instances survive both. `shared.Vessel` follows the part: after an undock, one addon reported `KOS Test`, the other `KOS Test Probe`. A program running on the processor that stays (`long_run`) kept going through the split. Ports docked together in the VAB split through `onPartDeCouple`, not `onPartUndock`. Staging wasn't triggered separately; it uses the same decouple path. |
| Scene change | Leaving Flight breaks the program, and that shows up as a pop with no report. That's the interrupted-sequence case. |
| On-rails warp and SOI change | Works. A program ran through `warpto` at up to 1536×. Its `WHEN ship:body…` trigger fired at UT 33990.26, the same UT KSP logged the Kerbin → Mun SOI change. The every-tick trigger fired 261 times over 32,877 game seconds (6 real seconds). A plain 6-hour warp also reported correctly. |
| RSVP v9 on kOS 1.6.0.1 | Works unmodified. `rsvp:goto(mun, …)` returned `success=True` with predicted and actual departure and arrival (849 m/s departure, 239 m/s arrival) and created the node, in about 28 real seconds at `config:ipu` 2000. |

## Build facts

- kOS 1.6.0.1 is compiled against .NET Framework 4.8, so a Claudia addon DLL must target `net48`.
- kOS name tags (and so `CORE:TAG`) come from a ModuleManager patch. Without ModuleManager, every tag is empty.
