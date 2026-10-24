# What a kOS script can reach beyond its craft

Research for the ticket "What a kOS script can reach beyond its craft". Question: what can a kOS 1.6.0.1 script do that affects the player's save, game, files or other vessels, beyond flying its own craft badly? For each item: can a static check on the source or compiled opcodes detect it, and can the mod block it at runtime?

Facts only. The rules themselves belong to the ticket "Script safety rules".

Sources: kOS source at tag [`1.6.0.1`](https://github.com/KSP-KOS/KOS/tree/1.6.0.1) (commit `d438843`, 2026-05-14), and the kOS docs built from `doc/source` at that tag ([ksp-kos.github.io/KOS](https://ksp-kos.github.io/KOS/)). Below, `SRC` means `https://github.com/KSP-KOS/KOS/blob/1.6.0.1/`. Line anchors are for that tag.

## Answer in brief

- **kOS has no sandbox or permission system.** No kOS setting turns off file ops, KUNIVERSE, part actions or anything else listed here. The only built-in limits are:
  - an own-vessel check on *some* part and vessel mutators;
  - CommNet reachability for the Archive (`0:/`);
  - KSP's own difficulty flags, which gate revert and quickload;
  - an opcode budget per physics tick (`CONFIG:IPU`, clamped to 50 to 2000).
- **Biggest reach outside the craft:**
  - `KUNIVERSE`: revert, quickload (including any named save), quicksave to any name, switch or force-switch the active vessel, launch a new craft from a `.craft` file, pause.
  - Time warp, which is game-wide and includes physics warp.
  - The Archive `0:/`. It is the real folder `<KSP>/Ships/Script/`, shared across **all** saves, never reverted, with no size limit. A script can delete anything under it, write without limit, and write files into `0:/boot/`, which every future craft can pick as a boot file.
  - Global kOS config. `CONFIG:*` setters persist. They include `IPU`, `CLOBBERBUILTINS`, and `TELNET`, which only starts after the player clicks through kOS's opt-in dialogs.
  - `KUNIVERSE:DEFAULTLOADDISTANCE`, which changes load and pack ranges for every vessel in later scenes.
  - Other vessels: renaming or retyping one, changing its load distance, undocking ports, and toggling RCS, gimbal and sensor flags on its parts all lack an own-vessel check.
  - Kerbal Alarm Clock alarms, if KAC is installed: any of them can be deleted or edited.
  - `debugfreezegame(ms)`, which sleeps KSP's main thread.
- **Static checks are useful but not complete.** Built-in calls show up as `OpcodeCall` with a constant name such as `"logfile()"`. Suffixes show up as `OpcodeGetMember`/`OpcodeGetMethod`/`OpcodeSetMember` with a constant identifier, and bound variables as `$name` identifiers. So most surfaces can be found by name in the compiled opcodes. Four holes remain:
  1. `makebuiltindelegate(<string>)` calls **any built-in by a name computed at runtime**.
  2. `run`/`runpath`/`runoncepath` load code that was not present when the check ran. That includes `.ksm` files, which skip the compiler entirely, and files the script wrote itself.
  3. Arguments are runtime values: paths, `DOEVENT`/`DOACTION` names, save names.
  4. Generic suffix names (`ACTIVATE`, `DELETE`, `NAME`, `TAG`) mean different things on different types, and the compiler does not type them.
- **Runtime blocking is possible at a few chokepoints, all through internals or Harmony, none through a supported API:**
  - every built-in function call goes through `FunctionManager.CallFunction(string)`;
  - every suffix get/set goes through `Structure.GetSuffix`/`SetSuffix`, which some subclasses override;
  - every piece of code entering a CPU goes through `ScriptHandler.Compile` (source) or `ProgramContext.AddParts`/`AddObjectParts` (source and KSM);
  - plus the target methods themselves (`KUniverseValue.*`, `TimeWarpValue.*`, `Archive.*`).

## How code reaches the CPU, and what a check can see

- The mod compiles and starts a script with `YieldFinishedCompile.RunScript(...)`. That compiles on a worker thread with `shared.ScriptHandler.Compile(...)`, then calls `programContext.AddParts(codeParts)` on the main thread. Source: [SRC src/kOS.Safe/Execution/YieldFinishedCompile.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Execution/YieldFinishedCompile.cs).
  - So the mod can compile first itself (`ScriptHandler.Compile` returns `List<CodePart>`, each with `FunctionsCode`, `InitializationCode` and `MainCode` opcode lists), inspect the result, then hand it to `AddParts`. Sources: [SRC src/kOS.Safe/Compilation/KS/KSScript.cs#L22](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/KS/KSScript.cs#L22), [SRC src/kOS.Safe/Compilation/CodePart.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/CodePart.cs).
- **What opcodes expose:**
  - `OpcodeCall.Destination` holds the built-in name with `()` appended, e.g. `"logfile()"`. User functions use `$name*` labels instead.
  - `OpcodeIdentifierBase.Identifier` holds suffix names for `GetMember`/`GetMethod`/`SetMember`, and variable names for `Store`/`Push`. Bound globals such as `$warp`, `$kuniverse` and `$config` appear this way.
  - Sources: [SRC src/kOS.Safe/Compilation/Opcode.cs#L500-L520](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/Opcode.cs#L500-L520), [#L1586-L1600](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/Opcode.cs#L1586-L1600).
  - Statements compile to built-in calls:
    - `LOG` → `logfile()`
    - `RUN` → `run()`
    - `RUNPATH`/`RUNONCEPATH`/`COMPILE` → `load()`
    - `REBOOT` → `reboot()`
    - `SHUTDOWN` → `shutdown()`
    - `STAGE` → `stage()`
    - `SWITCH TO` → `switch()`
    - `COPY`/`RENAME`/`DELETE` (deprecated) → `copy_deprecated()` and similar.

    Source: [SRC src/kOS.Safe/Compilation/KS/Compiler.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/KS/Compiler.cs) (the `OpcodeCall("...()")` sites around L3021-L3422).
- **No dynamic suffix lookup.** `Structure` offers `HASSUFFIX` and `SUFFIXNAMES` but no "get suffix by string". Suffix names in opcodes are always compile-time constants. Source: [SRC src/kOS.Safe/Encapsulation/Structure.cs#L65-L70](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Encapsulation/Structure.cs#L65-L70).
- **Dynamic built-in lookup does exist.**
  - The compiler turns `name@` on a built-in into `makebuiltindelegate("name")`.
  - `makebuiltindelegate` is itself a normal built-in function, so a script can call it directly with any string expression.
  - The delegate later runs `OpcodeCall.StaticExecute(cpu, true, Name, true)` → `cpu.CallBuiltinFunction` → `FunctionManager.CallFunction(name)`.
  - Sources: [SRC Compiler.cs#L1810-L1820](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/KS/Compiler.cs#L1810-L1820), [SRC src/kOS.Safe/Function/Misc.cs#L387](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Function/Misc.cs#L387), [SRC src/kOS.Safe/Encapsulation/BuiltinDelegate.cs#L47-L49](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Encapsulation/BuiltinDelegate.cs#L47-L49), [SRC src/kOS.Safe/Execution/CPU.cs#L1643-L1645](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Execution/CPU.cs#L1643-L1645).
  - A static check that names forbidden built-ins must therefore also treat `makebuiltindelegate()` calls with a non-constant argument as unknown. This is an inference from source, not tested.
- **Code admitted after the check.**
  - `run()`/`load()` read a file at runtime. A `.ksm` is turned straight into opcodes with `content.AsParts(...)` and never passes through the compiler. A `.ks` is compiled through `YieldFinishedCompile`. Source: [SRC src/kOS.Safe/Function/Misc.cs#L61-L255](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Function/Misc.cs#L61-L255).
  - The path is a runtime value, and the script can write the file first (`LOG`, `CREATE`, `:WRITE`).
  - Every source compile goes through `ScriptHandler.Compile`: the mod's own, `runpath`, terminal lines and boot. Every program load goes through `ProgramContext.AddParts`/`AddObjectParts`: source and KSM. Either is a single gate. Note that gating `Compile` also gates the player's own terminal commands. Source: [SRC src/kOS.Safe/Execution/ProgramContext.cs#L67-L74](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Execution/ProgramContext.cs#L67-L74).
- **Runtime chokepoints.**
  - All built-ins: `FunctionManager.CallFunction(string functionName)`. It looks names up in a private per-CPU `Dictionary<string, SafeFunctionBase>`, built from `[Function]` attributes. A Harmony prefix here, or replacing entries in that dictionary by reflection, can refuse any built-in by name, including those reached through `makebuiltindelegate`. Source: [SRC src/kOS.Safe/Function/FunctionManager.cs#L49-L61](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Function/FunctionManager.cs#L49-L61).
  - All suffixes: `OpcodeGetMember` calls `specialValue.GetSuffix(Identifier)` and `OpcodeSetMember` calls `SetSuffix`. Both are `virtual` on `Structure`. `Config` and some others override them, so a patch on the base method alone misses those. Patching the concrete target methods (`KUniverseValue.QuickLoad`, `TimeWarpValue.SetWarp`, and so on) is the narrower option. Sources: [SRC Structure.cs#L142](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Encapsulation/Structure.cs#L142), [#L180](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Encapsulation/Structure.cs#L180), [Opcode.cs#L763](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/Opcode.cs#L763), [#L873](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Compilation/Opcode.cs#L873).
  - Bound variables (`WARP`, `WARPMODE`, `TARGET`, `SHIPNAME`, `MAPVIEW`, action groups): setters are lambdas registered with `BindingManager.AddSetter`. Source: [SRC src/kOS/Binding/](https://github.com/KSP-KOS/KOS/tree/1.6.0.1/src/kOS/Binding).
  - Harmony is not part of kOS. It would be a separate mod dependency (not researched here). Everything listed as "runtime block" below means "patch or wrap a public or internal kOS member". kOS offers no hook designed for this.

## Survey

"Static" = can a check on source or opcodes find it. "Runtime" = where the mod could stop it.

### Files and volumes

| Surface | What it can do | Static | Runtime |
|---|---|---|---|
| Archive `0:/` (`LOG ... TO`, `CREATE`, `:WRITE`, `WRITEJSON`, `COPYPATH`, `MOVEPATH`, `DELETEPATH`, `volume:DELETE`, `CREATEDIR`) | Real folder `<KSP>/Ships/Script/`. Per the docs it is shared by **all saves**, does **not** revert, and is "infinitely large". The Archive volume has `Capacity = -1` (infinite), so unbounded writes reach the real disk. Deleting any item under the root is allowed; only deleting the root itself throws. | Function names yes (`logfile()`, `deletepath()`, ...). Suffixes `DELETE`/`CREATE`/`WRITE`/`CLEAR` by name only. Target paths are runtime strings, so the volume (`0:` vs `1:`) is only known statically when it is a literal. | Gate `FunctionManager.CallFunction`, or patch `Archive.Delete`/`SaveFile`/`CreateFile`/`ArchiveFile.Write*`. The Archive is only reachable with a home connection or in PRELAUNCH (`ConnectivityVolumeManager.CheckRange`), but that is gameplay, not a guard. |
| Path escape from the Archive | Blocked by kOS. `GetArchivePath` rejects `..` (`PointsOutside`) and re-checks that the full path starts with the Archive folder. | n/a | Already enforced. |
| Other processors' volumes | `UpdateVolumes` gives each CPU every kOS hard disk **on the same vessel**, docked vessels included because they become one vessel. A script can read, write and delete them (`2:/...`, `PROCESSOR("tag"):VOLUME`). Volumes can be renamed (`volume:NAME`). No other vessel's disk is reachable. | Same as above. | Same chokepoints, plus `Harddisk.*`. |
| `0:/boot/` | Any file written there shows up as a boot-file choice for every craft in the editor. `CORE:BOOTFILENAME` (own vessel only) changes what this CPU runs at every load. | `BOOTFILENAME` suffix by name; the path only if literal. | Patch `kOSProcessorFields.SetBootFilename` or the `bootFile` field; block writes under `boot/`. |
| `RUN`/`RUNPATH`/`RUNONCEPATH` | Runs any reachable `.ks` or `.ksm`, including one the script just wrote. | The call is visible; the content is not. | Gate `ScriptHandler.Compile` and `ProgramContext.AddParts`/`AddObjectParts` (covers KSM). |
| `COMPILE` | Writes a `.ksm` file to a volume. | `load()` call with an output argument. | Same as file writes. |

Sources: [volumes docs](https://ksp-kos.github.io/KOS/general/volumes.html) ([rst L135-L160](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/doc/source/general/volumes.rst)), [SRC src/kOS.Safe/Persistence/Archive.cs#L40-L62](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Persistence/Archive.cs#L40-L62), [#L160-L183](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Persistence/Archive.cs#L160-L183), [SRC Volume.cs#L48-L91 and suffixes #L223-L235](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Persistence/Volume.cs#L223-L235), [SRC VolumeFile.cs#L39-L42](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Persistence/VolumeFile.cs#L39-L42), [SRC src/kOS.Safe/Function/Persistence.cs#L202-L246](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Function/Persistence.cs#L202-L246), [SRC src/kOS/Persistence/ConnectivityVolumeManager.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Persistence/ConnectivityVolumeManager.cs), [SRC src/kOS/Module/kOSProcessor.cs#L915-L947](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Module/kOSProcessor.cs#L915-L947), [SRC kOSProcessorFields.cs#L24-L56](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/PartModuleField/kOSProcessorFields.cs#L24-L56), [boot docs](https://ksp-kos.github.io/KOS/general/boot.html).

### KUNIVERSE, time warp, game state

| Surface | What it can do | Static | Runtime |
|---|---|---|---|
| `KUNIVERSE:REVERTTOLAUNCH`, `REVERTTOEDITOR`, `REVERTTO(name)` | Reverts the flight. Allowed only when KSP allows it (`FlightDriver.CanRevert*` plus difficulty `CanRestart`/`CanLeaveToEditor`); otherwise it throws. | Unique suffix names. | Patch `KUniverseValue.Revert*`, or turn off the KSP difficulty flags. |
| `KUNIVERSE:QUICKSAVE`, `QUICKSAVETO(name)` | Writes `saves/<game>/<name>.sfs` with `SaveMode.OVERWRITE`, so it **overwrites existing saves** of that name. Only the names `persistent`, `quicksave` (for `QUICKSAVETO`) and `kos-backup-quicksave` are refused. kOS does not strip path separators from `name`; what KSP's `GamePersistence.SaveGame` does with them was not checked. Gated by difficulty `CanQuickSave`. | Unique names; the save name only if literal. | Patch `KUniverseValue.QuickSave`/`QuickSaveTo`. |
| `KUNIVERSE:QUICKLOAD`, `QUICKLOADFROM(name)`, `QUICKSAVELIST` | Loads any `.sfs` in the save folder, after first writing `kos-backup-quicksave.sfs`. If the target save's start scene is not FLIGHT, kOS **re-saves that file** (OVERWRITE) and loads the Space Center. Gated by `CanQuickLoad`. | Unique names. | Patch `KUniverseValue.QuickLoad*`. |
| `KUNIVERSE:ACTIVEVESSEL` (set), `FORCESETACTIVEVESSEL`/`FORCEACTIVE` | Switches the player's active vessel. The docs warn that force-switching "may cause … the vessel [to] be treated as if it is re-entering the atmosphere and deleted." | Unique names (`ACTIVEVESSEL` is also read-only harmlessly; a check needs to see `SetMember` vs `GetMember`). | Patch `SetActiveVessel`/`ForceSetActiveVessel`. |
| `KUNIVERSE:LAUNCHCRAFT*`, `GETCRAFT`, `CRAFTLIST` | Launches a new vessel from any saved `.craft`, with chosen crew, after KSP's preflight checks (cost, size, facility). Aborts the running program. | Unique names. | Patch `LaunchShipInternal`. |
| `KUNIVERSE:PAUSE` | Opens the pause menu. | Unique. | Patch `PauseGame`. |
| `KUNIVERSE:DEFAULTLOADDISTANCE:<situation>:LOAD/UNLOAD/PACK` (set) | Changes `PhysicsGlobals.Instance.VesselRangesDefault`. Per the docs this applies to all vessels from the next flight-scene load. Whether it persists past a game restart was not checked (it is in-memory `PhysicsGlobals`). | `DEFAULTLOADDISTANCE` unique; the inner `LOAD`/`UNLOAD`/`PACK` names are generic. | Patch `LoadDistanceValue` setters. |
| `KUNIVERSE:DEBUGLOG` | Writes a line to `KSP.log`. | Unique. | Patch `DebugLog`. |
| `KUNIVERSE:TIMEWARP` (`:RATE`, `:WARP`, `:MODE`, `:WARPTO`), bound `WARP`/`WARPMODE`, `WARPTO()` | Game-wide time warp. `MODE` can be `"PHYSICS"` (physics warp); rates are clamped to KSP's rate tables, and KSP's own altitude limits for rails warp apply in `TimeWarp.SetRate`. | `TIMEWARP` suffix, `$warp`/`$warpmode` stores, `warpto()` call. | Patch `TimeWarpValue.SetWarp`/`SetRate`/`SetModeAsString`/`WarpTo`. |
| `SET TARGET TO ...` | Sets the player's navigation target. Throws unless the CPU's vessel is the active vessel. | `$target` store. | Binding setter. |
| `MAPVIEW` (set) | Toggles map view. | `$mapview`. | Binding setter. |
| `HUDTEXT`, `GUI(...)`, `VECDRAW`, `HIGHLIGHT` | Screen messages, windows and overlays only. | Names. | Function gate. |

Sources: [SRC src/kOS/Suffixed/KUniverseValue.cs#L28-L54](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/KUniverseValue.cs#L28-L54), [#L58-L285](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/KUniverseValue.cs#L58-L285), [#L380-L416](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/KUniverseValue.cs#L380-L416); [kuniverse docs](https://ksp-kos.github.io/KOS/structures/misc/kuniverse.html); [SRC src/kOS/Suffixed/TimeWarpValue.cs#L35-L190](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/TimeWarpValue.cs#L35-L190); [SRC src/kOS/Binding/BindingsUniverse.cs#L24-L29](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Binding/BindingsUniverse.cs#L24-L29); [SRC src/kOS/Binding/MissionSettings.cs#L28-L59](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Binding/MissionSettings.cs#L28-L59); [SRC LoadDistanceValue.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/LoadDistanceValue.cs).

### kOS config (global, persistent)

| Surface | What it can do | Static | Runtime |
|---|---|---|---|
| `CONFIG:IPU`, `UCP`, `STAT`, `ARCH`, `SAFE`, `VERBOSE`, `CLOBBERBUILTINS`, `DEBUGEACHOPCODE`, `SUPPRESSAUTOPILOT`, ... | Every key is a get/set suffix. Per-game keys live in `kOSCustomParameters`, part of the save's game parameters. Others (`TELNET`, `TPORT`, `IPADDRESS`, terminal defaults) are written immediately to kOS's plugin config file by `SaveConfigKey`. Effects include raising IPU to 2000 (it applies on the next tick) and allowing scripts to shadow built-in names (`CLOBBERBUILTINS`). | `$config` then `SetMember <key>`. | Patch `Config.SetSuffix` (it overrides `Structure.SetSuffix`) or the property setters. |
| `CONFIG:TELNET` | Setting it true does **not** start the server by itself. `TelnetMainServer.SetConfigEnable` requires a per-session opt-in dialog, a second dialog for a non-loopback address, and `SIMULATE_IN_BACKGROUND`. Otherwise it resets the flag to false and shows the dialogs. | As above. | As above. |

Sources: [SRC src/kOS/Suffixed/Config.cs#L18-L95](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/Config.cs#L18-L95), [#L170-L220](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/Config.cs#L170-L220), [SRC src/kOS/Module/kOSCustomParameters.cs#L46-L66](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Module/kOSCustomParameters.cs#L46-L66), [SRC src/kOS/UserIO/TelnetMainServer.cs#L174-L200](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/UserIO/TelnetMainServer.cs#L174-L200), [config docs](https://ksp-kos.github.io/KOS/structures/misc/config.html).

### Processor lifecycle

| Surface | What it can do | Static | Runtime |
|---|---|---|---|
| `REBOOT` | Aborts the program, sets this CPU OFF then READY. The boot file then runs again. | `reboot()`. | Function gate. |
| `SHUTDOWN` | Sets this CPU OFF. It stays off until someone powers it on (the part action, or another CPU). | `shutdown()`. | Function gate. |
| `PROCESSOR("tag"):DEACTIVATE/ACTIVATE`, `:CONNECTION:SENDMESSAGE` | Turns other kOS CPUs **on the same vessel** on or off (own-vessel check), or messages them. `VESSEL(...):CONNECTION:SENDMESSAGE` messages other vessels' CPUs; the effect depends on their own scripts. | `processor()` call, then generic `ACTIVATE`/`DEACTIVATE` suffixes. | Patch `kOSProcessorFields.Activate/Deactivate`. |

Sources: [SRC src/kOS.Safe/Function/Misc.cs#L304-L327](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Function/Misc.cs#L304-L327), [SRC src/kOS/Function/Misc.cs#L141-L170](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Function/Misc.cs#L141-L170), [SRC kOSProcessorFields.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/PartModuleField/kOSProcessorFields.cs).

### Parts, modules and other vessels

kOS's guard is `ThrowIfNotCPUVessel()`, which compares the target's vessel id with the CPU's. It is applied per suffix, not globally.

| Surface | Own-vessel check? | Notes |
|---|---|---|
| `PartModule:SETFIELD`, `:DOEVENT`, `:DOACTION` | **Yes** | Only PAW-visible fields and events; actions may be gated by career tech. Event and action names are runtime strings, so a static check sees only `DOEVENT`/`DOACTION`, plus the name when it is a pushed literal. Any action the module exposes is reachable this way: decouple, explode-style actions, fairing deploy, parachute, docking. |
| Engine `ACTIVATE`/`SHUTDOWN`/`TOGGLEMODE`/`PRIMARYMODE`/`AUTOSWITCH`, part `CONTROLFROM`, part `TAG` set, processor `BOOTFILENAME`/`ACTIVATE`/`DEACTIVATE`, flight `CONTROL`, science experiment actions | **Yes** | |
| Vessel `NAME`/`SHIPNAME` set, `TYPE` set | **No** | `VESSEL("x"):NAME` renames any vessel the script can get a handle to. |
| Vessel `LOADDISTANCE:*` set | **No** | |
| Vessel `STARTTRACKING`/`STOPTRACKING` | **No** | Tracking-station state for asteroids and comets. |
| Docking port `UNDOCK`, `TARGET` | **No** | `UNDOCK` fires the port's Undock or Decouple event. |
| RCS `ENABLED`/axis flags, gimbal `LOCK`/axis flags, sensor `ACTIVE`/`TOGGLE` | **No** | Plain field writes on the module. |
| Part `REMOVESYMMETRY` | **No** | |
| `TRANSFER`/`TRANSFERALL` | Requires from and to parts on one common vessel; not checked to be the CPU's vessel. | |
| Action-group bindings (`AG1`..`AG10`, `ABORT`, `GEAR`, `CHUTES`, ...), `STAGE` | Act on `shared.Vessel` only. `STAGE` additionally needs the CPU's vessel to be the active vessel. | |
| Steering, throttle (`LOCK STEERING`, `SHIP:CONTROL`) | Own vessel only (`GetFlightControl` checks). | |

- **Reaching other vessels.** `VESSEL("name")`, `TARGET`, `LIST TARGETS` and `KUNIVERSE:ACTIVEVESSEL` return handles to other vessels. `:PARTS` and `:MODULES` on a loaded vessel return its parts. Inference from source: the unchecked suffixes above therefore work on other *loaded* vessels. This was not tested in game.
- **Static detection** is by suffix name only. Because the names are generic (`NAME`, `TYPE`, `ENABLED`, `LOCK`), a name check cannot tell "own ship" from "other ship". Telling them apart needs runtime checks.
- **Runtime blocking.** Patch the concrete setters (`VesselTarget.RenameVessel`/`RetypeVessel`, `DockingPortValue.DoUndock`, and so on), or add a vessel-id check in a `Structure.SetSuffix`/`GetSuffix` prefix keyed on the structure's type.

Sources: [SRC src/kOS/Suffixed/PartModuleField/PartModuleFields.cs#L42-L46](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/PartModuleField/PartModuleFields.cs#L42-L46), [#L445-L600](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/PartModuleField/PartModuleFields.cs#L445-L600); [SRC VesselTarget.cs#L258-L315](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/VesselTarget.cs#L258-L315), [#L388-L429](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/VesselTarget.cs#L388-L429), [#L481-L500](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/VesselTarget.cs#L481-L500); [SRC Part/PartValue.cs#L45-L76, #L205-L292](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/Part/PartValue.cs#L205-L292); [SRC Part/DockingPortValue.cs#L34-L35, #L108](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/Part/DockingPortValue.cs#L108); [SRC Part/RCSValue.cs#L49-L57](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/Part/RCSValue.cs#L49-L57); [SRC Part/EngineValue.cs#L185-L513](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/Part/EngineValue.cs#L185-L513); [SRC ResourceTransferValue.cs#L368-L388](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/ResourceTransferValue.cs#L368-L388); [SRC src/kOS/Binding/ActionGroups.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Binding/ActionGroups.cs); [SRC src/kOS/Function/Misc.cs#L75-L94](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Function/Misc.cs#L75-L94); [parts and partmodules docs](https://ksp-kos.github.io/KOS/general/parts_and_partmodules.html).

### Addons

- The kOS built-in addons are `ADDONS:KAC`, `ADDONS:RT`, `ADDONS:TR` and `ADDONS:AGX`. Each is active only if the other mod is installed.
  - **KAC:** `deleteAlarm(id)` deletes **any** Kerbal Alarm Clock alarm, and `listAlarms()` gets ids for all of them. Alarm suffixes `NAME`, `NOTES`, `ACTION`, `TIME`, `MARGIN`, `REPEAT` and others are settable. `addAlarm(...)` creates alarms.
  - **TR (Trajectories):** sets that mod's target and descent profile.
  - **AGX:** extended action groups on the CPU's vessel.
  - **RT:** read-only queries.
- Any third-party assembly with `[kOSAddon("ID")]` adds more `ADDONS:ID:*` suffixes. Their reach is whatever that mod implements.
- Static: `$addons` then `GetMember <ID>`, and KAC function names. Runtime: function gate or patch the addon class.
- Sources: [SRC src/kOS/AddOns/](https://github.com/KSP-KOS/KOS/tree/1.6.0.1/src/kOS/AddOns) (`KerbalAlarmClock/KACFunctions.cs#L11-L125`, `KACAlarmWrapper.cs#L29-L47`, `Trajectories/Addon.cs#L19-L40`, `RemoteTech/Addon.cs#L21-L27`), [SRC src/kOS/Suffixed/AddonList.cs](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS/Suffixed/AddonList.cs), [addons docs](https://ksp-kos.github.io/KOS/addons.html).

### CPU time, loops, memory

- **IPU budget.** `CPU.KOSFixedUpdate` runs at most `CONFIG:IPU` opcodes per physics tick (`ContinueExecution`'s `while (InstructionsThisUpdate < instructionsPerUpdate ...)`). The value is clamped to [50, 2000] (`kOSCustomParameters`, default 200). `WAIT` ends the tick's budget early. A kerboscript infinite loop therefore **does not freeze KSP**; it uses the budget every tick and drains electric charge per instruction. Sources: [SRC src/kOS.Safe/Execution/CPU.cs#L1349-L1556](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Execution/CPU.cs#L1349-L1556), [cpu hardware docs](https://ksp-kos.github.io/KOS/general/cpu_hardware.html).
- **Wall-clock time.** There is no wall-clock or per-opcode time limit. The stopwatches in the loop only feed statistics. A single opcode that does a lot of C# work runs to completion inside the tick.
  - `debugfreezegame(ms)` calls `Thread.Sleep(ms)` on KSP's main thread for any requested duration. Source: [SRC src/kOS.Safe/Function/Misc.cs#L339-L362](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Function/Misc.cs#L339-L362).
  - Compiling `.ks` source runs on a worker thread (`YiedFinishedThreadedDetector`), so a huge `runpath` target does not block the main thread during compile.
- **Triggers.** `WHEN`/`ON`/`LOCK` bodies run at raised priority. The docs warn that a trigger with a long or infinite loop starves main-line code and lower triggers, though it is still bounded by IPU per tick. `droppriority()` lets a trigger give up its priority.
- **Stacks.** Argument and scope stacks are capped at 3000 entries each, which bounds unbounded recursion. Source: [SRC src/kOS.Safe/Execution/Stack.cs#L12-L13](https://github.com/KSP-KOS/KOS/blob/1.6.0.1/src/kOS.Safe/Execution/Stack.cs#L12-L13).
- **Heap.** I found no cap on list, lexicon or string size. Inference, not tested: a loop like `SET s TO s + s.` doubles memory per iteration and could exhaust memory within a few ticks.
- **Static detection.** "Has a loop" and "has a `WAIT` in its loop" are visible as backward branch opcodes and `OpcodeWait`. Termination is undecidable in general.
- **Runtime limits available to the mod:**
  - lower IPU, which is a global, per-game setting;
  - count ticks or opcodes per run and abort with `Cpu.BreakExecution` (see the kOS handoff research);
  - gate `debugfreezegame`.

## Uncertainties and things not verified

- Nothing here was run in game. Every "works on other vessels" claim is read from source: the suffix lacks `ThrowIfNotCPUVessel`, and the handle is reachable.
- I did not check what KSP's `GamePersistence.SaveGame` does with path separators in `QUICKSAVETO` names, nor whether `DEFAULTLOADDISTANCE` changes survive a game restart.
- I did not check Harmony's ability to patch the compiler-generated lambdas inside suffix registrations, such as the RCS setters. Patching named methods, or `Structure.GetSuffix`/`SetSuffix` with a type and name filter, avoids that.
- `kOSCustomParameters` is described as "per game" in its migration text. I did not trace exactly which save file stores it.
- The survey covers kOS-provided surfaces. Third-party `kOSAddon`s and other mods' PartModule events reached through `DOEVENT`/`DOACTION` are open-ended.
