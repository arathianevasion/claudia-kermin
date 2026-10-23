# Single in-process KSP mod

Claudia Kermin ships as one C# KSP plugin (.NET Framework 4.7.2) that calls the Claude API, reads game state, drives kOS through a small kOS addon, and draws the chat UI, with no companion process and no dependency beyond kOS (plus ModuleManager only if the kOS integration needs it). Prior-art projects that drive kOS robustly all run outside KSP, but only because they relied on kRPC/kIPC; research showed HTTPS streaming to the Claude API works from KSP's Mono runtime and that a `[kOSAddon]` gives in-process access to each kOS processor, so one-mod installation for players outweighs the isolation an external process would give.

## Considered Options

- **Companion process** (Python/Node/Rust over kRPC, kOS via kIPC or telnet): rejected; extra runtime to install, and kIPC is GPL-3 and abandoned upstream since 2018.
- **Hybrid** (thin in-game mod + external planner): rejected; all the planner needs is available in-process.

## Consequences

- The code is split into a Unity-free **core** (mission orchestration, Claude client, prompt building, mission state; unit-testable outside KSP) and a thin **KSP adapter** (game-state reads, kOS bridge, crew roster, UI) that feeds the core plain snapshots.
- All game reads and kOS calls run on Unity's main thread; Claude requests run in the background and hand results back through a queue drained each frame.
- No kRPC, kIPC, MechJeb or Kerbal Engineer dependency.
