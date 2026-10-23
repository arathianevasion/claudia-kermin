# Prior art: LLM and mission-control mods for KSP

Research for issue #17. Sources read on 2026-10-23. Every claim cites the source it came from. Where I am inferring rather than reading, the text says so.

## Answer

No existing project does what Claudia Kermin plans to do. None pairs an in-game chat with a mission controller that turns a Mission Goal into Maneuver Sequences, writes kOS for each one, waits for the player's Approval, runs it, and then gives a Debrief. The two named projects each cover half of that, and the halves fit together:

- **DeepJeb (Acea-K, MIT)** is the in-process half. It is a KSP 1.12.5 plugin that calls LLM HTTPS APIs (Anthropic among them) directly from KSP's Mono runtime, with an IMGUI chat window, an agent/tool loop, and SKILL.md knowledge injection. It shows that HTTPS from in-game works, provided you force TLS 1.2. It does **not** run kOS. It only chats about kOS, and its file sandbox cannot even reach the kOS archive (`Ships/Script`).
- **ksp-mission-control (keenanbuckley, MIT)** is the execution half. Despite the name, it has **no LLM at all**. It is an external Rust process that talks to KSP over kRPC and pushes commands to kOS over kIPC. Its best ideas are the ones Claudia needs: a kOS boot-file dispatcher, an ack/heartbeat/`script_done` protocol, a watchdog that notices a dead kOS script (power loss, crash, save reload), and "compute constants outside, ship them to a small parameterised kOS script".
- **Differentiate against:** a fast-growing group of external **MCP servers** (caseys/ksp-mcp, Nodenester/KerbalMCP, MaiaJP krpc-mcp, GeePT MCP, codecraft3r/ksp-mcp, ksp-wingman, ksp-hmi). With these, an outside agent such as Claude Code flies the rocket through kRPC, kOS telnet or MechJeb. They have no in-game persona, and most have no Approval gate. The nearest in-game rival is **ClaudePilot** (NodeNestor, no license), an in-game "chat with Claude" copilot with more than 80 direct-control tools. It flies through KSP/MechJeb APIs, not kOS, and it appears to be abandoned. The nearest *UX* rival is **engbman's KSP Mission Control** on SpaceDock (CC-BY-NC-ND, voice CAPCOM, OpenAI, Windows only). It verifies telemetry, but it is a narrator: it does not fly.
- **Claudia's differentiators:** an in-game Kerbal persona; plan-in-chat, then Approval, then a kOS script, then a Debrief, one Maneuver Sequence at a time; kOS scripts the player can read and keep (not opaque API calls); and Claude only.

**Reuse candidates (both MIT, attribution required):** DeepJeb's `HttpHelper`/`AnthropicClient` SSE pattern and its main-thread token queue; ksp-mission-control's `dispatch_listener.ks` protocol and heartbeat watchdog design. **Avoid:** kIPC as a hard dependency. Upstream has been abandoned since 2018, the live forks have different owners, and it is GPL-3.0. Also avoid DeepJeb's process-wide `ServicePointManager` override, its XOR "encryption" of keys, and any copy of its keyword safety filters.

## What Claudia Kermin can take from each

| Concern | DeepJeb | ksp-mission-control | Implication for Claudia |
|---|---|---|---|
| Process model | In-process KSP plugin (4 DLLs) | External Rust server + kRPC + kIPC + kOS boot script | In-process is proven viable for chat + HTTPS; external is proven for robust kOS control. A decision ticket must choose. |
| LLM transport | `HttpWebRequest` from Mono, TLS 1.2 forced, SSE streaming on a background `Task`, tokens queued to a main-thread coroutine | None | Copy the threading pattern; scope TLS setting carefully |
| kOS integration | None (knowledge only) | kIPC messages to a kOS boot dispatcher; scripts pre-deployed to `Ships/Script` | Claudia must write scripts to the archive and start them; dispatcher + heartbeat is the template |
| UI | IMGUI draggable window, toolbar button, markdown renderer | Browser dashboard (vanilla JS over WebSocket) | IMGUI chat is the KSP-native path |
| Safety | Path sandbox, keyword filters, `.bak` backups | Only whitelisted scripts run; buttons disabled while a script runs | Approval gate + whitelisted/inspected script paths |
| Maintenance | 8 releases 2026-06-21 to 2026-07-12; no commits since | 0.2.0 on 2026-09-28; steady PR cadence | Neither is a dependency; both are reference code |

## DeepJeb (Acea-K/DeepJeb)

- **What it is:** an in-game AI chat window for KSP 1.12.5 (Unity 2019.2, C# 7.3, "Zero Dependencies"). Users bring their own key for OpenAI-compatible, Anthropic or Gemini APIs. Source: [README](https://github.com/Acea-K/DeepJeb/blob/main/README.md).
- **License / status:** MIT ([LICENSE](https://github.com/Acea-K/DeepJeb/blob/main/LICENSE)). The README credits it as "vibe coded using Claude Code / DeepSeek V4 Pro". Created 2026-06-21, last push 2026-07-12, 5 stars, releases 0.5.0 to 0.5.6 ([releases](https://github.com/Acea-K/DeepJeb/releases)). There have been no commits for about 3 months, so treat it as dormant (an inference, not a stated status). The binaries (`GameData/DeepJeb/Plugins/*.dll`) are committed to the repo. A KSP forum thread was requested in [issue #2](https://github.com/Acea-K/DeepJeb/issues/2) by linuxgurugamer and is still open, so there is no forum presence yet. I found no SpaceDock listing (web search only, low confidence).

### Architecture (in-process)

- Four assemblies in layers: `DeepJeb.Core` (agent loop, skills, sessions, security; Unity-free and unit-testable), `DeepJeb.Protocol` (provider clients), `DeepJeb.Unity` (IMGUI UI, tools, lifecycle) and `DeepJeb.Mod` (KSPAddon entry and DI wiring). Source: [docs/architecture.md](https://github.com/Acea-K/DeepJeb/blob/main/docs/architecture.md). The "pure C# outside Unity" split is worth copying for testability.
- Constraints of KSP's Mono that it documents: old-style .csproj targeting .NET Framework 4.6; no NuGet; a bundled MiniJSON; **no C# value tuples, because `System.ValueTuple.dll` is not in KSP's Mono**. Source: architecture.md, "Design Decisions".
- Agent loop: at most 10 tool rounds, a forced summary at round 5, repeat detection. Source: architecture.md. Tools are file read/write/delete/list in GameData, `get_game_state`, `web_search` and `fetch_url` ([README](https://github.com/Acea-K/DeepJeb/blob/main/README.md#ai-agent-tools)).
- Skills: SKILL.md files with YAML frontmatter and `applies_when` conditions (for example `directory_exists:GameData/kOS`). Selection is a keyword match; the top 2 skills plus 3 reference files are injected as system messages. Source: architecture.md, "Skill System". It ships a kOS skill compiled from kOS docs v1.4.0.0 ([SKILL.md](https://github.com/Acea-K/DeepJeb/blob/main/GameData/DeepJeb/Skills/mods/kos-programming/SKILL.md)). The current kOS release is 1.6.0.1 (2026-05-14, [KSP-KOS/KOS releases](https://github.com/KSP-KOS/KOS/releases)), so that skill is already behind.

### LLM transport: HTTPS from Mono

- It uses `System.Net.HttpWebRequest`, not `UnityWebRequest`. A static constructor sets `ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12`, with the comment "KSP .NET 4.6 default is TLS 1.0". Source: [HttpHelper.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Protocol/HttpHelper.cs). The same override appears in [WebTools.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Unity/Tools/WebTools.cs).
  - **Lesson:** HTTPS to api.anthropic.com from KSP's Mono is workable if TLS 1.2 is forced. **Caveat (inference):** `ServicePointManager.SecurityProtocol` is process-wide, so assigning it (rather than OR-ing `Tls12` in) changes TLS for every other mod in the same KSP process. Claudia should OR in the flag, or confirm on a real install that it is not needed. Field evidence that DeepJeb's HTTPS works comes from a user report of real DeepSeek calls from in-game ([issue #1](https://github.com/Acea-K/DeepJeb/issues/1)). I found no issue confirming the Anthropic path specifically.
- Anthropic: native `POST /v1/messages` with SSE parsing of `content_block_start`, `text_delta` and `input_json_delta` (tool use), plus an optional thinking budget. The streaming request runs inside `Task.Run`. Source: [AnthropicClient.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Protocol/Anthropic/AnthropicClient.cs).
- Threading: a `SendCoroutine` starts the async pipeline, pushes tokens into a locked list, drains them each frame on the main thread, and updates the session only on the main thread. Source: [DeepJebMod.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Mod/DeepJebMod.cs). **Possible bug (inference, not verified at runtime):** `get_game_state` reads `FlightGlobals`/`HighLogic` inside `ExecuteAsync`, which the agent loop awaits off the streaming task ([GameStateTool.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Unity/Tools/GameStateTool.cs), [AgentLoop.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Core/Agent/AgentLoop.cs)). That may touch Unity/KSP state from a worker thread. Claudia should marshal every game read and write to the main thread.
- API keys: stored on disk as XOR plus Base64, explicitly "trivially reversible". The threat model is "don't show keys on stream". Gemini keys can leak into the Alt+F12 log because they travel in the query string. Source: README "Known Issues" and architecture.md decision 8.

### kOS integration

- **None at runtime.** DeepJeb explains and writes kOS text in chat. Its file tools are sandboxed to `GameData` (`new PathSandbox(KSPUtil.ApplicationRootPath + "GameData")` in [DeepJebMod.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Mod/DeepJebMod.cs); [PathSandbox.cs](https://github.com/Acea-K/DeepJeb/blob/main/src/DeepJeb.Core/Security/PathSandbox.cs) rejects any path that escapes the root). The kOS archive volume is `Ships/Script/`, outside GameData ([ksp-mission-control README](https://github.com/keenanbuckley/ksp-mission-control#download)). So DeepJeb cannot even save a script where kOS would find it, and the player has to copy it there and run it by hand. Claudia closes exactly this gap.

### UI

- IMGUI windows (chat, settings) and a toolbar AppLauncher button. The window is a fixed 600x500 and does not scale well. A markdown renderer is built on `GUIStyle.CalcHeight`. Streaming causes frame-rate dips on long replies. Generation stops on scene change. ClickThroughBlocker needs a double click. Sources: architecture.md, README "Known Issues", [CHANGELOG](https://github.com/Acea-K/DeepJeb/blob/main/CHANGELOG.md) (0.5.1 and 0.5.2 spend a lot of effort on IMGUI scroll and height bugs).
- **Lesson:** IMGUI chat works but is a time sink. Budget for it, or keep Claudia's rendering much simpler (plain text, no markdown tables).

### Known problems worth learning from (CHANGELOG)

- 400 errors when tool results piled up past the context window. Fixed in 0.5.6 by re-trimming between agent rounds, and the token estimate was lowered from 4 to 2 chars/token for CJK.
- 400 errors from saving assistant `tool_calls` without their tool results, and from switching to a model the provider does not have (0.5.1, 0.5.2).
- `/retry` after a failed first request pulled in context from an old session ([issue #1](https://github.com/Acea-K/DeepJeb/issues/1)). Fixed in 0.5.4 with session-ID isolation.
- Keyword safety filters produced false positives on normal modding questions and were loosened in 0.5.6. **Lesson:** don't build a homemade content filter. Rely on the model provider.

## ksp-mission-control (keenanbuckley/ksp-mission-control)

- **What it is:** "A Rust + Axum dashboard that plans and executes Kerbal Space Program launches via kRPC and kOS". It has four buttons: Launch, Plan Circ, Execute Node and Toggle AG1. Source: [README](https://github.com/keenanbuckley/ksp-mission-control/blob/main/README.md).
- **No LLM.** A grep of the source for llm/claude/openai/anthropic/gpt found nothing. All planning is deterministic Rust ([launch_planning.rs](https://github.com/keenanbuckley/ksp-mission-control/blob/main/src/launch_planning.rs), [planning.rs](https://github.com/keenanbuckley/ksp-mission-control/blob/main/src/planning.rs)). The ticket's framing ("parameterized launch/node scripts") is accurate. It is prior art for execution, not for AI.
- **License / status:** MIT. Created 2026-04-24, v0.2.0 on 2026-09-28, 32 commits, 22 merged PRs, CI with CodeQL/Sonar, no issues filed, 2 stars ([commits](https://github.com/keenanbuckley/ksp-mission-control/commits/main), [PRs](https://github.com/keenanbuckley/ksp-mission-control/pulls?q=is%3Apr)). Actively developed by one person. Prebuilt binaries are Linux x86_64 only.

### Architecture (external process)

- One Rust process using tokio channels: `krpc.rs` (kRPC client and telemetry stream), `web.rs` (WebSocket to the browser), `control.rs` (kIPC command dispatcher), `script_watchdog.rs`. It requires the kRPC, kOS and **kIPC** mods. Source: README "Architecture".
- **kIPC** bridges kRPC and kOS. The upstream `dewiniaid/ksp-kipc` is abandoned (last push 2018-11-11) and is **GPL-3.0** ([doc/LICENSE.md](https://github.com/dewiniaid/ksp-kipc/blob/master/doc/LICENSE.md)). The README points to the forks `keenanbuckley/ksp-kipc` (pushed 2026-05-19) and `roxik0/ksp-kipc` (2025-03-30). kIPC needs a JSON envelope `{"type":"dict","data":...,"keys":[],"values":[]}`, with nested containers pre-wrapped. Source: [control.rs](https://github.com/keenanbuckley/ksp-mission-control/blob/main/src/control.rs).
- kRPC itself is active: v0.6.0 (2026-07-22) and v0.7.0 (2026-10-21) ([krpc releases](https://github.com/krpc/krpc/releases)).

### How it talks to kOS (the part worth copying)

- Scripts are **pre-deployed** to `Ships/Script/` by a `deploy-kos` helper. The player sets the vessel's kOS boot file to `0:/boot/dispatch_listener.ks`. Source: README "Download".
- [`dispatch_listener.ks`](https://github.com/keenanbuckley/ksp-mission-control/blob/main/kos/scripts/boot/dispatch_listener.ks):
  - claims an `mc` part tag so only one CPU per vessel answers, and stays passive otherwise;
  - drains `core:messages` and handles ops `ping`, `toggle_ag`, `add_node` and `run_script`;
  - `run_script` runs only **whitelisted** scripts (`SCRIPT_SHAPE` lexicon) and passes a config Lexicon as the argument;
  - emits `command_ack`, `heartbeat` (1 Hz real time, through a `when` trigger, so it keeps going while the script runs) and `script_done` with a `BOOT_ID`.
- **Watchdog** ([script_watchdog.rs](https://github.com/keenanbuckley/ksp-mission-control/blob/main/src/script_watchdog.rs)): "kerboscript has no try/catch, and a CPU that loses power runs no cleanup". The server therefore declares a script dead after 10 s of silence, counted only while UT advances (pausing does not count), or immediately when UT jumps backwards (a save was loaded). It tracks liveness per boot session. Claudia needs this to give an honest Debrief when a Maneuver Sequence dies mid-flight.
- **Computation split** ([docs/launch.md](https://github.com/keenanbuckley/ksp-mission-control/blob/main/docs/launch.md#the-computation-split)): heavy maths (vis-viva, atmosphere solve) runs outside kOS and is shipped as constants; kOS runs only the real-time loop. This matters for Claudia. Having the LLM produce *parameters for a tested script* is far more reliable than having it write a full kOS script every time.
- **Rocket-design assumptions** baked into `launch.ks` (AG1 at the edge of space, launch clamps, auto-stage on flameout, qMax throttle cap) are documented in the README. This is an example of the "Craft Check" problem: a generic script imposes rules on how the craft is built.

### UI

- A browser dashboard (`static/index.html`, vanilla JS, no build step) at `127.0.0.1:8080`, with no authentication. The README warns that a non-loopback bind hands vessel control to anyone on the network. Buttons are disabled while a script runs.

## Other comparable projects

| Project | What it is | Architecture | LLM / kOS | License, status | Source |
|---|---|---|---|---|---|
| **NodeNestor/ClaudePilot** | In-game "chat with Claude" copilot, 80+ tools (flight, MechJeb, science, career, craft files, delta-v maps), optional MCP server | In-process plugin | `UnityWebRequest` to an **OpenAI-compatible** endpoint, defaulting to a local HTTP proxy at `127.0.0.1` (so no TLS in-game). Direct control through KSP/MechJeb APIs, **not kOS**. Open issue: Anthropic returns 400 because it sends `/v1/chat/completions` | **No license**; 1 commit-day (2026-03-08) | [repo](https://github.com/NodeNestor/ClaudePilot), [ClaudeClient.cs](https://github.com/NodeNestor/ClaudePilot/blob/main/src/claude/ClaudeClient.cs), [issue #2](https://github.com/NodeNestor/ClaudePilot/issues/2) |
| **Nodenester/KerbalMCP** | ClaudePilot with the in-game chat removed; MCP over HTTP+SSE on `localhost:8745` | In-process MCP server | External agent; MechJeb via reflection | MIT, pushed 2026-08-24 | [repo](https://github.com/Nodenester/KerbalMCP) |
| **engbman KSP Mission Control** | Voice CAPCOM: GO calls, telemetry-verified milestones, Apollo 11 recreation | Windows companion app + small KSP mod + kRPC | OpenAI Whisper/TTS (required), ElevenLabs optional; does not fly | **CC-BY-NC-ND**, 546 downloads; GitHub has release binaries only | [SpaceDock 4378](https://spacedock.info/mod/4378), [repo](https://github.com/engbman/ksp-mission-control-download) |
| **caseys/ksp-mcp** | MCP server driving kOS + MechJeb2; `--no-execute` plan-only mode | External Node process over **kOS telnet** | External agent; needs the kOS.MechJeb2.Addon fork | MIT, "EARLY DEV", pushed 2026-02-05 | [repo](https://github.com/caseys/ksp-mcp) |
| **codecraft3r/ksp-mcp** | .NET MCP server: part search, stage sizing, writes `.craft` to `Ships/VAB`, deploys kOS libs to `Ships/Script`, runs missions over kOS telnet (claims a Mun flight) | External process | External agent; kOS telnet port 5410 | No license, 2026-09 | [repo](https://github.com/codecraft3r/ksp-mcp) |
| **ziksjksk/kerbal-space-program-mcp** | In-game C# plugin with a loopback HTTP bridge on the main thread, plus a stdio MCP server; build, launch confirmation, closed-loop landing | Hybrid | External agent; KSP API direct | No license, 2026-09 | [repo](https://github.com/ziksjksk/kerbal-space-program-mcp) |
| **MaiaJP-AIXplain/krpc-mcp** | Curated kRPC + MechJeb copilot tool surface, optional raw mode | External Python | External agent | MIT, 2026-05 | [repo](https://github.com/MaiaJP-AIXplain/krpc-mcp) |
| **G4ertner GeePT MCP** | LLM writes and runs kRPC Python scripts; wiki/docs search; playbooks | External Python | External agent | MIT, last push 2025-12-31 | [repo](https://github.com/G4ertner/kRPC_docs_MCP) |
| **Ljove02/krpc-mcp** | Docs-only MCP so LLMs stop hallucinating kRPC calls | External | n/a | MIT | [repo](https://github.com/Ljove02/krpc-mcp) |
| **NEON-NULL-NARWHAL/ksp-wingman** | AI joins a Luna Multiplayer server as a player and chats in LMP chat | Second KSP + kRPC + MCP | Claude Code or local Ollama | GPL-3.0, very active (2026-10-23) | [repo](https://github.com/NEON-NULL-NARWHAL/ksp-wingman) |
| **cpuchip/ksp-hmi** | Voice MCP copilot, local models, "confirm gate" before acting | External Go (planned) | Local LLM, kRPC + MechJeb | Apache-2.0, 2026-07; its [RESEARCH.md](https://github.com/cpuchip/ksp-hmi/blob/master/RESEARCH.md) is a useful field survey | [repo](https://github.com/cpuchip/ksp-hmi) |
| **cjohnson74/KOSMOS** | Multi-agent GPT-4 over kRPC (flight agent working, mission/audit agents partial) | External Python | OpenAI | MIT, 2025-12 | [repo](https://github.com/cjohnson74/KOSMOS) |
| **mit-ll/spacegym-kspdg** | Benchmark environments; an LLM agent placed 2nd in its challenge | External Python + kRPC | Research | MIT, 141 stars | [repo](https://github.com/mit-ll/spacegym-kspdg), [arXiv 2404.00413](https://arxiv.org/abs/2404.00413) |

Not verified: `Mystic-Red/M.U.N.A.` (an in-game LLM companion part, per ksp-hmi's survey) now returns 404 on GitHub.

Community signal: a 2024 r/Kos thread found ChatGPT-written kOS poor, with wrong loop syntax and unrealistic TWR assumptions ([thread](https://www.reddit.com/r/Kos/comments/1g4nabw/chatgpt_can_generate_kos_code/), seen through a mirror in search results). This supports grounding Claude with kOS docs (DeepJeb-style skills) and with tested, parameterised script templates (ksp-mission-control-style).

## Open questions for later decision tickets

1. **In-process vs external process.** DeepJeb proves in-process chat plus HTTPS works. Everything that controls kOS robustly runs outside KSP (kRPC/kIPC or kOS telnet). Does Claudia call Claude from inside KSP and drive kOS in-process (C# kOS addon API, or writing to `Ships/Script` and triggering a run), or ship a companion process?
2. **How Claudia starts a kOS script and learns its outcome without kIPC.** Options: a kOS addon written in C# (`kOS.AddOns`), a file-drop plus boot-file poller in `Ships/Script`, or the kOS telnet server. Each needs its own feasibility check.
3. **LLM writes full scripts vs fills parameters for a vetted library.** ksp-mission-control and community feedback point towards a vetted library plus LLM-chosen parameters, with free-form kOS only when needed. That affects what the Approval screen shows.
4. **TLS and threading spike:** confirm on a real KSP 1.12.5 install (Windows and Linux) that `HttpWebRequest` reaches `api.anthropic.com` with TLS 1.2 OR-ed in. Also choose `HttpWebRequest` or `UnityWebRequest`.
5. **API key storage:** DeepJeb's XOR+Base64 is cosmetic. Decide on plaintext with a clear warning, an OS keychain, or an environment variable.
6. **Reuse and licensing:** copying DeepJeb or ksp-mission-control code (both MIT) needs their copyright notice. ClaudePilot has no license and must not be copied. kIPC is GPL-3.0.
7. **Liveness/Debrief inputs:** adopt ksp-mission-control's heartbeat semantics (real-time beats, UT-advancing silence, UT rewind means save reload) as the source of the Debrief's "what happened"?
