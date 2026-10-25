# Claudia Kermin

A Kerbal Space Program mod that adds **Claudia**, an AI mission controller. You give her a goal in plain language, such as "Land on the Mun, run 3 experiments, return to Kerbin". She plans the mission, explains each step, asks your go-ahead, flies it through kOS and reports back.

> **Status: design complete, not yet built.** The v1 design is in [Spec: Claudia Kermin v1](https://github.com/arathianevasion/claudia-kermin/issues/36). Only the project skeleton exists so far (see [Building](#building)). The rest describes what v1 will do.

## How a Mission works

1. **Mission Goal.** In the VAB/SPH, tell Claudia what the Mission must achieve. Then pick your Mission Settings (daylight landing, time versus delta-v), a Spending Cap and the model she uses.
2. **Craft Check.** Claudia checks your craft against the goal: delta-v per leg, TWR, parachutes, experiments, a kOS processor and a seat for her.
   - **Blockers** stop the Mission from starting.
   - **Warnings** must be acknowledged before you can start.
   - She suggests fixes but never edits your craft.
3. **Mission Plan.** She proposes the whole route as a list of Maneuver Sequences, each with its delta-v budget and timing. You approve it once.
4. **The loop.** Each Maneuver Sequence goes through four steps:
   - **Sequence Plan:** what she'll do, its targets and tolerances, and what could go wrong.
   - **Approval:** your go-ahead in chat.
   - **Running:** the mod runs her kOS script, built from a shipped library of tested Building Blocks (`launch`, `circularize`, `exec_node`, `transfer_to`, `land`, `ascend`, `return`, `reentry`, `science`).
   - **Debrief:** how it went, rated Nominal, Off-Nominal or Failed, followed by the next plan.

Claudia is also a Kerbal: a female crew member with the **Super Intelligence** role, which combines Pilot, Engineer and Scientist. She flies aboard crewed craft, or remotely from Mission Control for probes. If she dies aboard, the Mission is Lost, and she returns to the roster later.

### You stay in charge

- No script runs without your Approval. The one exception is a **Blackout**: with no link to Kerbin, Claudia flies the Mission Plan you already approved, and shows you any changes she made when the link returns.
- The **Abort** button stops a running sequence and makes the craft safe.
- **Manual Control** lets you fly yourself at any time, if you have a link or other crew aboard. **Hand back to Claudia** when you're done, and she replans from where you left the craft.
- While a sequence runs, the mod watches its **Telemetry Checks**, such as delta-v used, duration and electric charge. If one is crossed, Claudia tells you whether to abort.

### Safety

kOS scripts can do far more than fly a craft. Before you see a script, the mod checks it against a forbidden list:

- save, load and revert
- switching vessels
- physics warp
- file operations
- kOS settings
- other vessels and other addons

While Claudia's programs run, runtime guards block the most harmful calls. Your own kOS terminal and scripts are unaffected. This is a safety net against Claude's mistakes, not a security sandbox. See [ADR 0003](docs/adr/0003-script-check-and-runtime-gates.md).

## Requirements (v1)

- Kerbal Space Program **1.12.5** on **macOS**. Windows and Linux are untested and planned for a later release.
- [kOS](https://github.com/KSP-KOS/KOS) 1.6 or later.
- [HarmonyKSP](https://github.com/KSPModdingLibs/HarmonyKSP) 2.2.1 (the community Harmony build for KSP, installed as `GameData/000_Harmony`).
- ModuleManager (optional), for the compatibility patches.
- Your own **Anthropic API key**.

Sandbox, Science and Career all work.

## API key and cost

Claudia uses the Claude API with your own key. Each Mission has its own Spending Cap, $10 by default, which you can raise later; the mod never sends a request that could exceed it. As a rough guide, a Mun landing and return costs about $2.40 on Sonnet 5.5 (the default) or about $5 on Opus 5.5.

The key is stored as **plain text** in `~/Library/Application Support/ClaudiaKermin/`, readable only by you. If the `ANTHROPIC_API_KEY` environment variable is set, it is used instead. The key is never written to logs, saves or Mission records, and never placed under GameData.

## Known gaps

- Stock contracts, EVA welding and the construction combine bonus count Claudia as a Pilot, Engineer and Scientist (Harmony patches on `FinePrint.Utilities.VesselUtilities`). ModuleManager patches in `GameData/ClaudiaKermin/Compatibility` cover Kerbalism and Community Trait Icons.
- These mods check crew roles by name and **do not** count Claudia as a Pilot, Engineer or Scientist: KER, USI, Near Future Electrical, SystemHeat, SSPX, StationScience, WildBlue and Contract Configurator contract packs.
- Claudia doesn't go on EVA in v1. When a goal needs EVA science, she asks you to do it by hand.
- In this development build, Missions are a first pass: scripts are Script-Checked and run behind runtime gates that stop a script trying to save, load, revert, switch vessels, write the Archive, change `CONFIG`, use physics warp, freeze the game, change other vessels or touch another kOS processor (your own terminal is never gated), and a script still running at `runaway_multiplier` (300%) of its planned duration is stopped; still read a Sequence Plan's script with **Show script** before you approve it. The Craft Check does not yet recheck margins in each Debrief. A Mission is kept across scene changes, quicksaves, reverts, quits and crashes: loading a save puts its state back without rewinding Claudia's conversation, a sequence that was Running at a load, quit or crash is marked interrupted and Debriefed (never restarted), and leaving Flight while Running asks first. The Mission's craft is the vessel Claudia is aboard (or, remote, the one with the original kOS processor), through staging, docking and undocking: you may fly other vessels between sequences, and nearby while Running, with a warning 10 seconds before the craft would unload; a far switch while Running asks first; either way the interrupted sequence is Debriefed when you are back in the craft, Resume works only from the craft, and the controls are locked only on the craft. Docking tells Claudia what was added, and the plan awaiting Approval needs Re-plan; undocking, or a vessel with a kOS processor separating, tells her what left. The Mission Plan names any Child Vessels (the Craft Check judges each at its separation stage, and refuses a staging plan that strands Claudia without a processor), and each Sequence Plan names its vessel: a script goes to a Child Vessel only once it has separated and has a CommNet path from Claudia, keeps running if the link drops, and starts only while every other vessel of the Mission is stable. A Child Vessel's run has no Telemetry Checks yet. Separated vessels the plan doesn't name are ignored, and Child Vessels are ordinary vessels once the Mission ends. The mod never switches vessels itself.
- If a script fails the Script Check, Claudia rewrites it once automatically; that rewrite is a billed request under the Spending Cap like any other. If the rewrite fails too, the plan shows **Couldn't produce a safe script** with **Retry**, **Retry with a note** or **Re-plan**. A Sequence Plan restored from a save is Script-Checked again before you can approve it.
- **Before uninstalling**, press **Prepare to uninstall** in Claudia's settings in every save that has her. Removing the mod without it is likely to break those saves.

## Building

You need the .NET 10 SDK. The adapter and kOS addon compile against your own KSP 1.12.5 and kOS 1.6 install; no KSP, Unity or kOS assemblies are in this repo. `KSPRoot` points at the install and defaults to `~/KSP_spike` (a macOS install with `KSP.app` inside). You can also set `KSPManagedDir` or `KOSPluginsDir` directly.

| What | Command |
| --- | --- |
| Build everything | `dotnet build ClaudiaKermin.sln -p:KSPRoot=/path/to/KSP` |
| Core tests (no KSP needed) | `dotnet test tests/ClaudiaKermin.Core.Tests` |
| Script Check tests (need only kOS.Safe) | `dotnet test tests/ClaudiaKermin.ScriptCheck.Tests -p:KSPRoot=/path/to/KSP` |
| Script Check the shipped kerboscript and block tests (needs only kOS.Safe) | `dotnet run --project tools/ClaudiaKermin.KsCompile -p:KSPRoot=/path/to/KSP -- kerboscript tests/blocks` |
| Core Library tests on kOS.Safe (no KSP needed) | `dotnet test tests/ClaudiaKermin.Kerboscript.Tests -p:KSPRoot=/path/to/KSP` |

A failing test, or a script that doesn't compile or uses something forbidden, exits non-zero. A build stages the mod in `dist/GameData/ClaudiaKermin/`. To install it, copy that folder into your KSP `GameData` folder, next to kOS and HarmonyKSP. kOS and HarmonyKSP are dependencies you install yourself; they are never bundled.

The layout follows [ADR 0001](docs/adr/0001-single-in-process-ksp-mod.md):

- `src/ClaudiaKermin.Core`: the Unity-free core. It never references Unity or KSP.
- `src/ClaudiaKermin.Adapter`: the thin KSP adapter (`ClaudiaKermin.dll`). One Winged glare toolbar button serves the Space Center (it toggles Settings), the VAB/SPH and Flight (it toggles the Mission window). The basic chat with Claudia (no Mission needed) is a debug entry point in Settings; her replies stream in and keep coming across scene changes. With `debug_tools = true`, the chat window can dump raw stream events to `stream-dumps/` in the config folder.
- Missions: the toolbar button opens the Mission window. Its start form takes the Mission Goal, Spending Cap and model (from `models` and `default_spending_cap` in the player config); in Flight a Capcom strip under the altimeter follows the Mission (Claudia's status, the Mission Plan's sequences as a segment track that scrolls sideways when they don't fit, what is locked) and the Mission window docks to it, below or, near the bottom of the screen, above. Drag the strip by its status row to move it (it is remembered in `windows.cfg`; double-click the row to put it back). The strip's tab or the toolbar button expands it, and Undock turns it back into a floating window for the session. All Mission logic sits behind one seam in the core (`ClaudiaKermin.Core.Missions.MissionControl`), which the adapter's `MissionDriver` feeds and obeys. Each Mission's transcript is rewritten atomically to `saves/<save>/ClaudiaKermin/missions/<id>/transcript.json` after every response; with `debug_tools = true`, raw stream events go to that folder's `stream-dumps/`, and a button saves Game Snapshots to `saves/<save>/ClaudiaKermin/snapshots/`. The save file holds a small snapshot per Mission (a `MissionScenario` ScenarioModule writes the core's `MissionSaveState`); the Mission folder also holds `mission.json`, an append-only `events.jsonl` and, while a request is out, `unanswered-turn.json`, none of which a load rewinds.
- Craft Check: the mod computes the facts (per-stage delta-v and thrust from the stock simulator, thrust over weight per body, the part inventory, crew, experiments and science left) into the Game Snapshot, and the core computes the Delta-v Map from the snapshot's body data. Claude judges them against the spec's fixed Blocker and Warning lists with the margin from `craft_check_margin` (percent, default 15) in the player config. The Mission board pins the result (Pass, Pass with Warnings or Fail); Start Mission is closed on a Blocker and until every Warning is acknowledged, and runs the check itself if the craft or goal changed. The craft is never edited and the delta-v app's body setting is never touched. If the stock delta-v calculations are off, the check stops and asks you to enable them. You can pick which kOS processor Claudia uses (default: nearest the root); a boot file on it is a Warning.
- `tests/ClaudiaKermin.Core.Tests/Fixtures/streams`: hand-written, byte-exact Claude streams that the client tests replay through the real parser.
- `GameData/ClaudiaKermin`: static mod files (the Super Intelligence trait config and icon, plus the Winged glare toolbar icons and portrait), copied into the staged mod on build. `tools/make-trait-icon.cs` regenerates the trait icon; the Winged glare PNGs are rasterized from `art/icons/*.svg`.
- `kerboscript/library`: the Core Library. `claudia_lib.ks` holds the shared helpers and, in its header, the conventions every Building Block follows; each block is one file named after it (`exec_node.ks`). The core embeds them (`CoreLibrary`), bundles them after each Mission Script into one compile unit, and puts their `//|` doc lines into Claudia's cached prompt layers. The Script Check runs over that whole bundle, the Mission Script with the Core Library, because that is what runs.
- `kerboscript/rsvp`: [RSVP](https://github.com/maneatingape/rsvp) v9 (GPL-3.0), which `transfer_to` plans with, bundled after the Core Library with only its file loading rewritten; `NOTE.md` there says how and why. The spec's `transfer` block is `transfer_to`, because kOS has a built-in `TRANSFER`; `return` keeps its name but must be called as a value (`local res is return().`), since a bare `return().` is kOS's return statement.
- `src/ClaudiaKermin.KOS`: the kOS addon (`net48`, like kOS 1.6.0.1): `ADDONS:CLAUDIA` and the kOS bridge that runs scripts on a processor. With `debug_tools = true` in the player config, a Flight window runs the test scripts in `kerboscript/debug/` through it, and a block test runner window (main menu, Space Center, Flight) runs the tests in `tests/blocks/`.
- `src/ClaudiaKermin.ScriptCheck`: the Script Check ([ADR 0003](docs/adr/0003-script-check-and-runtime-gates.md)). It compiles a script with kOS.Safe and reads its opcodes against the forbidden list. It is Unity-free (`net48`) and references kOS.Safe compile-only; kOS.Safe is never shipped. The core sees it only through `ClaudiaKermin.Core.Kos.IScriptCheck`, and the adapter passes it in.
- `tests/blocks/<test>`: debug block tests. Each has `test.cfg` (block, craft, orbit, timeout and the contract ranges to check), `test.ks` (the Mission Script to run), `scenario.sfs` (the save it starts from) and `reference.craft` (stock parts plus kOS, KSP 1.12.5). They ship inside the kOS addon DLL. **Run** in the runner window writes them to `saves/claudia_block_tests/`, loads that save, spawns the craft into orbit, runs the block unattended and logs PASS or FAIL to KSP.log and `saves/claudia_block_tests/block-tests.log`. Run them whenever a block changes.
- `tests/ClaudiaKermin.Core.Tests`: the core tests. `tests/ClaudiaKermin.ScriptCheck.Tests`: the real Script Check, with a test for each forbidden and allowed item.
- `tests/ClaudiaKermin.Kerboscript.Tests`: runs the Core Library's pure helpers on a kOS CPU from kOS.Safe, with no KSP, through the same bundle the mod starts.
- `tools/ClaudiaKermin.KsCompile`: the Script Check from the command line. It runs over `.ks` files (CI gives it `kerboscript/`, which holds the Core Library, and `tests/blocks`) with the same code the mod uses.

CI (`.github/workflows/ci.yml`) runs on every push. It runs the core tests, then takes kOS.Safe from the public kOS release to run the Script Check tests, Script Check every shipped script and the block test scripts, run the Core Library tests, and require the syntax-error and forbidden-call fixtures to fail.

## Project docs

- [GLOSSARY.md](GLOSSARY.md): the domain language (Mission, Craft Check, Maneuver Sequence, Debrief and more).
- [docs/adr/](docs/adr/): architecture decisions.
- [Map: Claudia Kermin v1](https://github.com/arathianevasion/claudia-kermin/issues/1): the design decisions, each with its own ticket.

## License

Claudia Kermin is free software under the GNU General Public License, version 3 or (at your option) any later version (`GPL-3.0-or-later`). See [LICENSE](LICENSE).

Bundled third-party scripts keep their own licenses and notices: [RSVP](https://github.com/maneatingape/rsvp) (GPL-3.0) and KSLib's `lib_lazcalc` (MIT). The Mission Scripts and Custom Scripts that Claudia writes for your Missions are yours; Claudia Kermin claims no license over them.
