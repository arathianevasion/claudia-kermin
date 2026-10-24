# Single in-process KSP mod

Claudia Kermin ships as one in-process C# KSP mod (.NET Framework 4.8, matching kOS) that calls the Claude API, reads game state, drives kOS through a `[kOSAddon]`, and draws the chat UI, with no companion process. It requires kOS 1.6 or later and HarmonyKSP 2.2.1 (Harmony is needed for the runtime gates in [ADR 0003](0003-script-check-and-runtime-gates.md)); ModuleManager is optional and used only for compatibility patches. Prior-art projects that drive kOS robustly all run outside KSP, but only because they relied on kRPC/kIPC; research showed HTTPS streaming to the Claude API works from KSP's Mono runtime and that a `[kOSAddon]` gives in-process access to each kOS processor, so one-mod installation for players outweighs the isolation an external process would give.

## Considered Options

- **Companion process** (Python/Node/Rust over kRPC, kOS via kIPC or telnet): rejected; extra runtime to install, and kIPC is GPL-3 and abandoned upstream since 2018.
- **Hybrid** (thin in-game mod + external planner): rejected; all the planner needs is available in-process.

## Consequences

- The code is split into three assemblies:
  - the Unity-free **core** (mission orchestration, Claude client, prompt building, mission state; unit-testable outside KSP);
  - a thin **KSP adapter** (game-state reads, crew roster, UI) that feeds the core plain snapshots;
  - the **KOS addon** (`ClaudiaKermin.KOS`, the kOS bridge), which depends on the adapter and on kOS 1.6+. It is kept separate to isolate the kOS load dependency: if kOS is missing or too old, KSP declines to load only that assembly and the rest of the mod still loads.
- All game reads and kOS calls run on Unity's main thread; Claude requests run in the background and hand results back through a queue drained each frame.
- No kRPC, kIPC, MechJeb or Kerbal Engineer dependency.
