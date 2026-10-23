# Existing kOS script libraries

Research for issue #6. Checked against primary sources on 2026-10-23: GitHub repos, their license files, commit history, issues and releases, and the kOS source repo and docs.

## Answer

- **No single maintained library covers everything we need.** Nobody has published an openly licensed, maintained library that handles launch to orbit, transfer, node execution and powered landing on airless bodies with a stable API. What exists is in pieces:
  - **KSP-KOS/KSLib** (MIT, the official community standard library) is mostly utilities. Of the libraries we would fly with, the useful ones are `lib_lazcalc` (launch azimuth) and `lib_navigation`. It has **no** launch, node-execution or landing routine.
  - **maneatingape/rsvp** (GPL-3.0) is the best fit for **transfer burns**. It is a real library: one `rsvp` entry point, options passed as a lexicon, and a structured `success` result. It builds departure and arrival maneuver nodes. Last release v9 (2023-02) was updated for kOS 1.4. There is no commit since 2023-08, and nobody has reported problems with kOS 1.5 or 1.6.
  - **Noiredd/PEGAS** (MIT, still maintained, last commit 2026-05) is a strong **launch-to-orbit** autopilot (Space Shuttle UPFG guidance). It needs a stage-by-stage vehicle description and is aimed at Realism Overhaul. Its README says it "should run in vanilla as well".
  - **Node execution** has no maintained library. The canonical reference is the kOS docs' own "Execute Node" tutorial (GPL-3, part of the kOS repo).
  - **Powered landing on the Mun.** The only working code is in personal script collections. **Dunbaratu/kerboscripts** (MIT; written by kOS's lead developer; `land_it.ks`, `lib/land.ks`, `launch.ks`, `hohmann.ks`, `just_obey_nodes.ks`; last commit 2024-05) is usable with attribution. **ElWanderer/kOS_scripts** has full moon landing and return, but it has **no license**, so we cannot reuse it. It was written for kOS 1.3.
- **Licenses are not a blocker.** MIT is GPL-3-compatible. RSVP is GPL-3, the same as kOS. Kerboscript files are programs that the kOS interpreter runs. On the FSF's reading, the interpreter's GPL does not extend to the scripts it runs. Only reusing code from unlicensed repos is ruled out. (This is my reading, not legal advice. See "Licensing" below.)
- **Watch for kOS version breaks.** Current kOS is **1.6.0.1** (2026-05-14), which supports KSP 1.8 to 1.12. kOS 1.4.0.0 made a breaking change: a local variable may no longer shadow a built-in name unless the script uses `@CLOBBERBUILTINS`. KSLib (2023) and RSVP (v9) were updated for it. Older scripts were not: gisikw/ksprogramming (last code commit 2016), ElWanderer (kOS 1.3), pand5461 (2018). They may need fixes. kOS 1.5 and 1.6 list no breaking language changes, but 1.6 notes a small behaviour change for GUI `Style` objects.
- **Yes, an LLM planner can compose and parameterize these, but only after we write a thin layer of our own.** RSVP shows the pattern that suits Claudia. Each library call takes one lexicon of options and returns a lexicon with `success`, which Claudia can choose, fill in and check in the Debrief. Recommendation: ship our own small library of Maneuver Sequence primitives, for example `launch(opts)`, `exec_node(opts)`, `transfer(opts)` (wrapping RSVP), `land(opts)` and `circularize(opts)`, all with that RSVP-style contract. Build them from KSLib `lib_lazcalc`, RSVP, the kOS exenode tutorial and Dunbaratu's MIT scripts. Claudia would then write short glue scripts that fill in parameters, not raw guidance loops.

## Comparison table

| Library | Covers | License | Last commit / release | kOS 1.4+ status | Reuse verdict |
|---|---|---|---|---|---|
| [KSP-KOS/KSLib](https://github.com/KSP-KOS/KSLib) | Launch azimuth (`lib_lazcalc`), navigation math, PID (superseded by built-in `PIDLOOP`), terminal/GUI utils | MIT | 2026-03-26 (minor). Bulk "compliance with new compiler directive" commits 2023-02 | Updated for 1.4 | **Use** `lib_lazcalc` and `lib_navigation`. Nothing for node exec or landing |
| [maneatingape/rsvp](https://github.com/maneatingape/rsvp) | Transfer window search, departure and arrival nodes, rendezvous, moons | GPL-3.0 | Commit 2023-08-26; release v9 2023-02-18 | v9: "Support kOS 1.4.0.0" | **Use** for transfer burns. Dormant but complete |
| [Noiredd/PEGAS](https://github.com/Noiredd/PEGAS) | Launch to orbit (UPFG closed-loop guidance, staging, azimuth, launch window) | MIT | 2026-05-03 (v1.3.1) | Actively maintained | **Optional** for launch. Needs a per-vehicle stage table |
| [kOS docs "Execute Node" tutorial](https://github.com/KSP-KOS/KOS/blob/develop/doc/source/tutorials/exenode.rst) | Maneuver node execution | GPL-3.0 (kOS repo) | Ships with kOS | Current | **Reference** for our `exec_node` |
| [Dunbaratu/kerboscripts](https://github.com/Dunbaratu/kerboscripts) | Launch, Hohmann, node exec, powered landing, docking, rover | MIT | 2024-05-06 | Kept to the public kOS release (README) | **Borrow** with attribution. Personal scripts with no stable API |
| [gisikw/ksprogramming](https://github.com/gisikw/ksprogramming) | `library/`: ascent, maneuver, orbit, rendezvous, docking | MIT | Code 2016-11-13 | Unknown, probably needs 1.4 fixes | Reference only |
| [ElWanderer/kOS_scripts](https://github.com/ElWanderer/kOS_scripts) | Launch, moon landing and return, rendezvous, docking, re-entry | **None** (all rights reserved) | 2021-06-04 | Written for kOS 1.3 / KSP 1.10 | **Do not reuse.** Read for ideas only |
| [pand5461/kOS](https://github.com/pand5461/kOS) | Assorted scripts | Unlicense | Code 2018-09 | Unknown | Low value |
| [stevemeacham/Autopilot-for-Realistic-KSP](https://github.com/stevemeacham/Autopilot-for-Realistic-KSP) | RO/RSS autopilot suite | GPL-3.0 | 2020-10 | Unknown | Off target (RO) |
| [nuggreat/kOS-scripts](https://github.com/nuggreat/kOS-scripts) | Staging detection, impact ETA, file utils | None | 2025-10 | Current-ish | Ideas only. Impact ETA is relevant to landing |

## Detail

### kOS itself

- Latest release is 1.6.0.1 (2026-05-14), after 1.6.0 (2026-05-11), 1.5.1.0 (2025-02-06) and 1.5.0.0 (2024-09-13). [releases](https://github.com/KSP-KOS/KOS/releases)
- The `kOS.version` file for 1.6.0.1 declares `KSP_VERSION_MIN 1.8` and `KSP_VERSION_MAX 1.12`, so it is compatible with KSP 1.12.5. [kOS.version](https://github.com/KSP-KOS/KOS/releases/download/1.6.0.1/kOS.version)
- kOS is GPL-3.0 (`LICENSE.md`. GitHub's API reports "NOASSERTION" only because of the file format). [LICENSE.md](https://github.com/KSP-KOS/KOS/blob/develop/LICENSE.md)
- The 1.4.0.0 breaking change: "a local variable [may no longer clobber] a builtin name ... could make existing scripts have to rename a variable or two", with `@CLOBBERBUILTINS` to opt out. [CHANGELOG](https://github.com/KSP-KOS/KOS/blob/develop/CHANGELOG.md)
- The 1.6.0 notes say widget `Style` suffixes are now shared objects ("may be a breaking change"). They also remove the built-in Infernal Robotics addon. Neither affects flight libraries. [1.6.0 release](https://github.com/KSP-KOS/KOS/releases/tag/1.6.0)
- kOS addons that matter for landing: the **Trajectories** addon (impact prediction) and **KAC**. [addon docs](https://github.com/KSP-KOS/KOS/tree/develop/doc/source/addons)

### KSLib

- README: MIT chosen over GPL so each file carries only a short attribution line. You only need to credit "the KSLib team" as a group. [README](https://github.com/KSP-KOS/KSLib/blob/master/README.md), [LICENSE](https://github.com/KSP-KOS/KSLib/blob/master/LICENSE)
- Library list: [library/](https://github.com/KSP-KOS/KSLib/tree/master/library) and [SUMMARY.md](https://github.com/KSP-KOS/KSLib/blob/master/SUMMARY.md). `lib_pid` is marked "superseded by kOS built-in `PIDLoop()`".
- `lib_lazcalc`: `LAZcalc_init(altitude_m, inclination)` then `LAZcalc(data)` during ascent. It handles impossible inclinations and north/south switching. [doc](https://github.com/KSP-KOS/KSLib/blob/master/doc/lib_lazcalc.md)
- Maintenance: 145 stars, 13 open issues, low activity. The last substantive work was the 2023-02 kOS 1.4 compliance pass and a 2024-08 `lib_navball` fix. [commits](https://github.com/KSP-KOS/KSLib/commits/master)

### RSVP

- Features: "Creates departure and arrival maneuver nodes", "Supports rendezvous between vessels, planets, moons, asteroids and comets". Usage is `runoncepath("0:/rsvp/main"). rsvp:goto(target, lexicon(...))`. [README](https://github.com/maneatingape/rsvp/blob/main/README.md)
- API conventions that suit an LLM: a single global `rsvp`, everything else locally scoped. "The return value is a lexicon that will always have a top-level boolean `success` key." Every option has a documented default (`create_maneuver_nodes`, `earliest_departure`, `search_duration`, `max_time_of_flight`, `final_orbit_periapsis`, `final_orbit_type`, `final_orbit_orientation`). [README](https://github.com/maneatingape/rsvp/blob/main/README.md)
- It refines the Lambert solution with a feedback loop at SOI exit, which matters for Kerbin to Mun accuracy. [src/README](https://github.com/maneatingape/rsvp/blob/main/src/README.md)
- Known gap: open issue #8. A vessel in Kerbin orbit cannot target a *vessel* orbiting Minmus. Targeting the moon itself works, according to the issue's workaround. Other open issues (#9 to #12) are feature requests. [issue #8](https://github.com/maneatingape/rsvp/issues/8)
- Recommends `set config:ipu to 2000.` because the search uses a lot of CPU. [README](https://github.com/maneatingape/rsvp/blob/main/README.md)
- v9 notes: "Support kOS 1.4.0.0 by renaming local variables". [v9](https://github.com/maneatingape/rsvp/releases/tag/v9)
- **Uncertainty:** I found no report either way about kOS 1.5 or 1.6. I expect it to work because those releases list no language breaks, but we should smoke-test it.

### PEGAS

- UPFG closed-loop ascent with launch window and azimuth calculation, automatic staging and delegates or addons for custom events. It targets RO but "should run in vanilla as well". [README](https://github.com/Noiredd/PEGAS/blob/master/README.md)
- "UPFG requires complete knowledge of the vehicle it is to guide (stage by stage)." The user supplies `vehicle`, `sequence`, `controls` and `mission` lexicons. [tutorial](https://github.com/Noiredd/PEGAS/blob/master/docs/tutorial.md) Claudia could generate these from the Craft Check, but getting stage masses and Isp right for a player's craft is a real source of failure.
- Maintained: v1.3.1 commits 2026-05-03. [commits](https://github.com/Noiredd/PEGAS/commits/master)
- **Uncertainty:** I found no evidence of how well it handles stock-scale Kerbin ascents with typical player rockets. A simple gravity-turn script may be more forgiving for a first version.

### Node execution

- The kOS docs tutorial computes burn time from `maxthrust/mass`, starts the burn half the burn time before the node, and throttles down as the remaining delta-v shrinks. It notes that the burn-time estimate ignores the rocket equation. [exenode.rst](https://github.com/KSP-KOS/KOS/blob/develop/doc/source/tutorials/exenode.rst), rendered at [ksp-kos.github.io](https://ksp-kos.github.io/KOS_DOC/tutorials/exenode.html)
- Dunbaratu's `just_obey_nodes.ks`, `precise_node.ks` and `lib/burn.ks` are MIT alternatives. [repo](https://github.com/Dunbaratu/kerboscripts)

### Powered landing (airless)

- Dunbaratu/kerboscripts: `land_it.ks` (parameters `safety_margin`, `skycrane`, `off_pitch`, `off_yaw`), `lib/land.ks`, `lib/terrain.ks` and `lib/prediction.ks`. It is MIT. The README says `master` is kept to the public kOS release. [land_it.ks](https://github.com/Dunbaratu/kerboscripts/blob/master/land_it.ks), [README](https://github.com/Dunbaratu/kerboscripts/blob/master/README.md)
- ElWanderer/kOS_scripts: "automated moon landing and return". Precision landing and slope detection were "in progress". "Written for kOS v1.3 and KSP v1.10". The repo has no license file and GitHub reports no license. [README](https://github.com/ElWanderer/kOS_scripts/blob/master/README.md)
- **Uncertainty:** I did not run any landing script. I cannot say how reliable any of them is on current kOS. Landing is the area with the least reusable, licensed and maintained code, so we should expect to write and test our own.

### Prior art: LLM and planner integrations

- **Acea-K/DeepJeb** (MIT, KSP 1.12.5, last commit 2026-07). An in-game LLM chat with a "kOS Programming" knowledge skill. It helps write and debug kOS, but does not fly missions through a library layer. [README](https://github.com/Acea-K/DeepJeb/blob/main/README.md)
- **keenanbuckley/ksp-mission-control** (MIT, 2026-09). A Rust server that runs parameterized `launch.ks` and `maneuver.ks` through kRPC and kIPC from a form. This is the "fill in the parameters of fixed scripts" approach, without an LLM. [README](https://github.com/keenanbuckley/ksp-mission-control/blob/main/README.md)

## Licensing

- **Scripts run by kOS.** The FSF FAQ says that when an interpreter is GPL, programs it interprets are not covered just by being run. They are covered only if they are linked to the interpreter's internals in a deeper way. [GPL FAQ: IfInterpreterIsGPL](https://www.gnu.org/licenses/gpl-faq.html#IfInterpreterIsGPL) So our kerboscript can be under any license.
- **Shipping MIT (KSLib, PEGAS, Dunbaratu) or GPL-3 (RSVP) scripts inside our mod.** MIT is GPL-compatible. Keeping RSVP's files under GPL-3 with their notices is "mere aggregation" if the rest of the mod uses another license, and it is trivially fine if the mod is GPL-3. Modified RSVP files must stay GPL-3. [GPL FAQ: MereAggregation](https://www.gnu.org/licenses/gpl-faq.html#MereAggregation)
- **Our C# plugin depending on kOS.** If the plugin links against kOS's assembly, it is probably a GPL "combined work" and should be released under a GPL-3-compatible license. Choosing GPL-3 for the whole mod removes the question. [GPL FAQ: GPLPlugins](https://www.gnu.org/licenses/gpl-faq.html#GPLPlugins) This is not legal advice.
- **Unlicensed repos** (ElWanderer, nuggreat) default to all rights reserved. We can read them but cannot copy from them.

## What this means for Claudia

1. An LLM writing raw guidance loops (steering locks, throttle PID, suicide-burn timing) is where most flight failures would come from. A fixed, tested primitive library moves that risk into code we can test once.
2. The RSVP contract is the one to copy: one call per Maneuver Sequence, a lexicon of parameters with defaults, and a lexicon result with `success` plus diagnostics. Claudia's Debrief can then compare the result with the plan.
3. Proposed sources for each primitive: `launch` = our own gravity turn using KSLib `lib_lazcalc` (PEGAS as a later option); `transfer` = RSVP; `exec_node` = based on the kOS tutorial and Dunbaratu's MIT scripts; `land` = our own, informed by Dunbaratu `land_it` and the Trajectories addon.
