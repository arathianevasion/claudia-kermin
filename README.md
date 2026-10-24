# Claudia Kermin

A Kerbal Space Program mod that adds **Claudia**, an AI mission controller. You give her a goal in plain language, such as "Land on the Mun, run 3 experiments, return to Kerbin". She plans the mission, explains each step, asks your go-ahead, flies it through kOS and reports back.

> **Status: design complete, not yet built.** The v1 design is in [Spec: Claudia Kermin v1](https://github.com/arathianevasion/claudia-kermin/issues/36). Nothing below works yet. It describes what v1 will do.

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
   - **Running:** the mod runs her kOS script, built from a shipped library of tested Building Blocks (`launch`, `circularize`, `exec_node`, `transfer`, `land`, `ascend`, `return`, `reentry`, `science`).
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
- Harmony, the community KSP build.
- ModuleManager (optional), for the compatibility patches.
- Your own **Anthropic API key**.

Sandbox, Science and Career all work.

## API key and cost

Claudia uses the Claude API with your own key. Each Mission has its own Spending Cap, $10 by default, which you can raise later; the mod never sends a request that could exceed it. As a rough guide, a Mun landing and return costs about $2.40 on Sonnet 5.5 (the default) or about $5 on Opus 5.5.

The key is stored as **plain text** in `~/Library/Application Support/ClaudiaKermin/`, readable only by you. If the `ANTHROPIC_API_KEY` environment variable is set, it is used instead. The key is never written to logs, saves or Mission records, and never placed under GameData.

## Known gaps

- Mods that check crew roles by name may not count Claudia as a Pilot, Engineer or Scientist: KER, USI, Near Future Electrical, SystemHeat, SSPX, StationScience, WildBlue and Contract Configurator contract packs. Kerbalism and Community Trait Icons are patched.
- Claudia doesn't go on EVA in v1. When a goal needs EVA science, she asks you to do it by hand.
- **Before uninstalling**, press **Prepare to uninstall** in Claudia's settings in every save that has her. Removing the mod without it is likely to break those saves.

## Building

You need the .NET 10 SDK. The adapter and kOS addon compile against your own KSP 1.12.5 and kOS 1.6 install; no KSP, Unity or kOS assemblies are in this repo. `KSPRoot` points at the install and defaults to `~/KSP_spike` (a macOS install with `KSP.app` inside). You can also set `KSPManagedDir` or `KOSPluginsDir` directly.

| What | Command |
| --- | --- |
| Build everything | `dotnet build ClaudiaKermin.sln -p:KSPRoot=/path/to/KSP` |
| Core tests (no KSP needed) | `dotnet test tests/ClaudiaKermin.Core.Tests` |
| Kerboscript compile check (needs only kOS.Safe) | `dotnet run --project tools/ClaudiaKermin.KsCompile -p:KSPRoot=/path/to/KSP -- kerboscript` |

A failing test or a kerboscript compile error exits non-zero. A build stages the mod in `dist/GameData/ClaudiaKermin/`. To install it, copy that folder into your KSP `GameData` folder, next to kOS and Harmony. kOS and Harmony are dependencies you install yourself; they are never bundled.

The layout follows [ADR 0001](docs/adr/0001-single-in-process-ksp-mod.md):

- `src/ClaudiaKermin.Core`: the Unity-free core. It never references Unity or KSP.
- `src/ClaudiaKermin.Adapter`: the thin KSP adapter (`ClaudiaKermin.dll`).
- `src/ClaudiaKermin.KOS`: the kOS addon (`net48`, like kOS 1.6.0.1).
- `tests/ClaudiaKermin.Core.Tests`: the core tests.
- `tools/ClaudiaKermin.KsCompile`: compiles the `.ks` files under `kerboscript/` with kOS.Safe outside KSP.

CI (`.github/workflows/ci.yml`) runs on every push. It runs the core tests and the kerboscript compile check, taking kOS.Safe from the public kOS release.

## Project docs

- [GLOSSARY.md](GLOSSARY.md): the domain language (Mission, Craft Check, Maneuver Sequence, Debrief and more).
- [docs/adr/](docs/adr/): architecture decisions.
- [Map: Claudia Kermin v1](https://github.com/arathianevasion/claudia-kermin/issues/1): the design decisions, each with its own ticket.

## License

Claudia Kermin is free software under the GNU General Public License, version 3 or (at your option) any later version (`GPL-3.0-or-later`). See [LICENSE](LICENSE).

Bundled third-party scripts keep their own licenses and notices: [RSVP](https://github.com/maneatingape/rsvp) (GPL-3.0) and KSLib's `lib_lazcalc` (MIT). The Mission Scripts and Custom Scripts that Claudia writes for your Missions are yours; Claudia Kermin claims no license over them.
