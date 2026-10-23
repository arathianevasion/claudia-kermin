# Claude API fit and per-mission cost

Research for issue #5. Sources checked 2026-10-23. Prices are Anthropic first-party Claude API list prices in USD.

## Answer

- **Model:** Use **Claude Opus 5.5 (`claude-opus-5-5`)** for Claudia's main loop: Craft Check, Maneuver Sequence planning, kOS script writing and Debrief. Anthropic recommends it as the starting model for most workloads [1]. It is good at agentic coding, and this loop is mostly agentic coding: the kOS scripts have to run correctly the first time. **Claude Sonnet 5.5 (`claude-sonnet-5-5`)** costs half as much and is the main alternative; run an eval before choosing it. **Claude Haiku 5.5** is suited to cheap side jobs such as summarising telemetry or classifying chat, not to writing scripts. **Claude Fable 5.1** costs 2.5x Opus 5.5 and is only worth it if Opus 5.5 at high effort still fails on your evals [1].
- **Features to use:**
  - Client **tool use** with `strict: true` on each tool: read craft data, read telemetry, submit a Maneuver Sequence plan, submit a kOS script.
  - **Structured outputs** for the plan and Debrief payloads that the mod's UI parses.
  - **Adaptive thinking** with the **effort** setting.
  - **Prompt caching** with the **1-hour TTL**, because each kOS execution leaves a gap of several minutes.
  - **Streaming** for the chat.
  - **Refusal handling** with server-side fallbacks.
  - **Context editing or compaction**, or a mission-state summary per Maneuver Sequence, so prompts do not keep growing.
  - Not useful here: the Batch API (the loop is interactive), web search, and fast mode.
- **Cost of a Mun landing and return.** Assumptions: 8 Maneuver Sequences, about 47 API calls, about 4.4M input tokens processed and about 127K output tokens including thinking, with the full mission history kept in context. Estimated cost per mission:
  - **Opus 5.5:** about **$4.80** with 1-hour caching, **$8** with 5-minute caching, **$20** with no caching.
  - **Sonnet 5.5:** about **$2.40** with 1-hour caching.
  - **Haiku 5.5:** about **$0.45** with 1-hour caching.
  - **Fable 5.1:** about **$11** with 1-hour caching.
  - The 6 to 10 sequence range for Opus 5.5 is **$3.50 to $6** with 1-hour caching.
- **Trimming the history** (a mission-state summary instead of the full transcript) would roughly halve input cost again. Output tokens set a floor of about $2.50 per mission on Opus 5.5.
- **Uncertainty:** These token counts are my assumptions, not measurements. The real cost could easily be 0.5x to 2x these figures. The biggest unknowns are how long the kOS reference in the system prompt is, how many tokens of thinking each script needs at the chosen effort, and how many tries a script takes before it flies.

## 1. Model capabilities (verified)

From the Models overview [1]:

| | Fable 5.1 | Opus 5.5 | Sonnet 5.5 | Haiku 5.5 |
|---|---|---|---|---|
| API ID | `claude-fable-5-1` | `claude-opus-5-5` | `claude-sonnet-5-5` | `claude-haiku-5-5` |
| Positioning | "demanding reasoning and long-horizon agentic work" | "long-running agentic coding and knowledge work" | "best combination of speed and intelligence" | "high-volume, latency-sensitive tasks such as classification, extraction, and routing" |
| Latency | Slower | Moderate | Fast | Fastest |
| Thinking | Adaptive (always on) | Adaptive (always on) | Adaptive | Adaptive |
| Default effort | `high` | `medium` | `high` | `medium` |
| Context / max output | 1M / 128K | 1M / 128K | 1M / 128K | 1M / 128K |
| Earliest retirement | Sep 1 2027 | Sep 22 2027 | Sep 28 2027 | Oct 7 2027 |

All four models accept text and image input and support tool use [1].

## 2. Pricing (verified)

From the Pricing page [2], in $ per million tokens:

| Model | Input | 5m cache write | 1h cache write | Cache read | Output |
|---|---|---|---|---|---|
| Fable 5.1 | 10 | 12.50 | 20 | 0.25 | 50 |
| Opus 5.5 | 4 | 5 | 8 | 0.20 | 20 |
| Sonnet 5.5 | 2 | 2.50 | 4 | 0.10 | 10 |
| Haiku 5.5, prompt up to 100K tokens | 0.10 | 0.125 | 0.20 | 0.01 | 0.50 |
| Haiku 5.5, prompt over 100K tokens | 0.50 | 0.625 | 1 | 0.05 | 2.50 |

Other pricing facts from [2]:

- Thinking tokens are billed as output tokens, even when the thinking text is not returned [3].
- The Batch API is 50% off.
- Requesting US-only inference (`inference_geo`) multiplies all prices by 1.1x.
- Haiku 5.5 is priced by prompt length: every request whose prompt is over 100K tokens, cache reads included, pays the higher prices.
- Every other current model gets the full 1M context at standard prices.
- Any request with tools adds a hidden tool-use system prompt of 286 input tokens on Opus 5.5, Sonnet 5.5 and Haiku 5.5. I did not find a figure for Fable 5.1.
- Fast mode on Opus 5.5 costs $8 / $40 per million tokens.

**Discrepancy:** the bundled `claude-api` skill lists the Sonnet 5.5 cache read price as $0.20. The official pricing page lists $0.10, which is 0.05x of $2. I used the official page.

## 3. Feature fit for the loop

Claudia's loop is: Mission Goal, then Craft Check, then for each Maneuver Sequence: plan, chat, Approval, kOS runs the script, Debrief. Everything goes through one Messages API conversation per mission. The mod's C# code owns the loop, so this is a **"workflow / Claude API + tool use"** design, not a hosted agent. Managed Agents would put the loop and sandbox on Anthropic's side, but kOS runs inside the player's KSP process, so the tools have to execute locally anyway [4].

| Feature | How it fits | Notes / gotchas |
|---|---|---|
| **Client tool use** [4] | Possible tools: `get_craft_data` for the Craft Check, `get_telemetry`, `propose_maneuver_sequence`, `submit_kos_script`. Approval is enforced by the mod: it does not run `submit_kos_script` until the player has approved in chat. | Set `strict: true` so tool inputs always match the schema [4]. **Forced `tool_choice` (`any` or `tool`) returns a 400 on Opus 5.5, Sonnet 5.5 and Fable 5.1**. Use `auto` and tell the model in the prompt which tool to call [4]. |
| **Structured outputs** (`output_config.format`) [5] | Typed JSON for a Maneuver Sequence plan (burns, Δv budget, abort criteria) and for Debrief fields the UI shows. Supported on all four models [5]. | The schema cannot use numeric `minimum`/`maximum`, string length limits, or recursive structures. The mod has to check those ranges itself. The first request with a new schema is slower while the grammar compiles; the compiled grammar is cached for 24h. Changing the format invalidates the prompt cache [5]. Do not ask for a "reasoning" field: it can trigger a `reasoning_extraction` refusal [5]. |
| **Adaptive thinking + effort** [3][1] | Planning and script writing get real benefit from thinking. Chat turns need only low effort. | Thinking cannot be disabled on Opus 5.5 or Fable 5.1; `budget_tokens` returns a 400 [4][3]. Effort is the only control, and Opus 5.5 defaults to `medium` [1]. Changing the top-level effort invalidates the message cache [6]. Use the per-message effort beta instead (`mid-conversation-output-config-2026-07-01`, Opus 5.5 and Sonnet 5.5) [4]. **All current models keep earlier turns' thinking in context and bill it as input** [3]. That is why the history grows quickly in the estimate below. |
| **Prompt caching** [6] | A large fixed prefix (Claudia persona, kOS language reference, tool definitions) is reused on every call, and the conversation grows by appending. Automatic top-level `cache_control` moves the breakpoint forward on its own [6]. | Minimum cacheable prefix is 512 tokens on all four models [6]. The default TTL is 5 minutes, and a kOS burn plus coast often runs longer than that. Use `ttl: "1h"` (2x write cost) or a `max_tokens: 0` keep-alive ping [6][4]. Never put timestamps or changing telemetry ahead of the breakpoint. |
| **Mid-conversation system messages** [4] | Inject telemetry or game-state notices as `role: "system"` messages without breaking the cached prefix. This is also the channel that cannot be spoofed by prompt injection. Supported on Opus 5.5, Sonnet 5.5, Fable 5.1 and Haiku 5.5. | Must follow a user message [4]. |
| **Streaming** [4] | Claudia's chat replies appear token by token in the KSP UI, which avoids long silent pauses while she thinks. | Thinking display defaults to `omitted`, so the player sees nothing until the answer starts. Set `display: "summarized"` or `"updates"` to show progress [4]. |
| **Refusal handling** [4] | Safety classifiers can return HTTP 200 with `stop_reason: "refusal"`. Talk of rockets and trajectories could plausibly cause false positives. | Always check `stop_reason`. Enable server-side `fallbacks: "default"` (beta `server-side-fallback-2026-07-01`) on Opus 5.5 and Sonnet 5.5 [4]. Haiku 5.5 has no server-side fallback [4]. |
| **Context editing / compaction** [4] | Clear old tool results (raw telemetry) or summarise once the mission history gets large. | Beta. Has to be compatible with preserved thinking: editing earlier turns invalidates thinking blocks, and **accounts created on or after 2026-08-31 get a 400** for edited history [4]. Design the conversation to only ever append. |
| Batch API, web search, fast mode | Not useful. The loop is interactive, the needed knowledge is in the prompt, and fast mode doubles the price [2]. | |
| Vision | Optional later: screenshots of the craft or the map view. | Not in the estimate. |

## 4. Cost estimate: Kerbin to Mun landing and return

### Assumptions (my own, not measured)

- **Fixed prefix: 12K tokens.** Claudia's persona and rules, a condensed kOS reference, and the tool definitions (the 286-token tool-use system prompt included). A full kOS manual would be much larger, and this assumption matters a lot.
- **Mission setup.** One Mission Goal chat turn, then a Craft Check: 4K tokens of craft data in, 1.5K tokens of visible output, 3K tokens of thinking. Then one player reply.
- **Each Maneuver Sequence** (launch, circularise, transfer, mid-course correction, capture, descent and landing, ascent, return and re-entry):

  | Call | Input appended | Visible out | Thinking |
  |---|---|---|---|
  | Plan | 0.3K | 0.8K | 2K |
  | Two chat turns, including Approval | 0.15K each | 0.3K each | 0.3K each |
  | Write kOS script | 0.2K | 2.5K | 4K |
  | Debrief | 3K of telemetry | 0.8K | 2K |
  | Script fix, every other sequence | 1.5K | 1.5K | 2K |

- **Effort:** the same token profile for every model, roughly what medium effort would produce. In practice Fable 5.1 at its `high` default would think more, and Haiku would probably need more retries.
- **History:** the full history is kept, including earlier turns' thinking, which all current models retain and bill as input [3]. With 8 sequences the final prompt is about 180K tokens.
- **Caching scenarios:**
  - **none:** no caching.
  - **5m:** a cold cache before each Debrief, because the flight outlasts the TTL.
  - **1h:** everything stays warm; every newly added token is written once at 2x, and the rest is read from cache.
- **Not included:** retries after API errors, refusals, the player abandoning a mission, and US-only inference (+10%).

### Results

The calculation is a short script; the per-call model above reproduces these numbers.

**8 Maneuver Sequences (typical):** 47 calls, 4.38M input tokens processed, 127K output tokens.

| Model | No cache | 5m cache | **1h cache** | Output share of 1h |
|---|---|---|---|---|
| Fable 5.1 | $50.15 | $19.18 | **$10.94** | $6.34 |
| Opus 5.5 | $20.06 | $8.01 | **$4.79** | $2.54 |
| Sonnet 5.5 | $10.03 | $4.01 | **$2.40** | $1.27 |
| Haiku 5.5 | $1.83 | $0.75 | **$0.44** | $0.18 |

**Range across mission lengths (1h cache):**

| Model | 6 sequences (36 calls) | 8 sequences (47 calls) | 10 sequences (58 calls) |
|---|---|---|---|
| Fable 5.1 | $8.22 | $10.94 | $13.76 |
| Opus 5.5 | $3.54 | $4.79 | $6.13 |
| Sonnet 5.5 | $1.77 | $2.40 | $3.07 |
| Haiku 5.5 | $0.24 | $0.44 | $0.66 |

Haiku 5.5 crosses its 100K-prompt price tier partway through the mission. That is why it costs more than a straight 1/40 of Opus.

### What moves the number

- **Caching is the biggest lever.** It cuts the cost 2.5x to 4x. The 1-hour TTL is worth paying for because each kOS execution leaves a gap of several minutes.
- **Limit how much history each call carries.** Start each Maneuver Sequence from a short mission-state summary of about 3K tokens instead of the full transcript, or use thinking or tool-result clearing. Prompts would then stay at roughly 20 to 30K tokens, and Opus 5.5 input cost would fall to roughly $1 per mission. This is a rough figure I did not compute separately. It must be designed as append-only, or as a new conversation per sequence, because of the preserved-thinking rule [4].
- **Output is the floor:** about 127K output tokens, roughly 60% of which is thinking. On Opus 5.5 that is $2.54 per mission whatever the caching. Lower effort on chat turns cuts it; higher effort on scripts raises it.
- **Mixing models:** Opus for scripts and Haiku for chat saves little, because the chat turns are a small share of the tokens. Each model also keeps a separate cache, so switching models forfeits cache reuse [4].

## 5. Open questions for a decision ticket

1. **Which SDK can the mod use?** KSP 1.12.5 runs on Unity 2019.4 with Mono (.NET Framework 4.x profile). The official C# SDK (`Anthropic` NuGet) may need a newer .NET runtime. I have not verified this. The fallbacks would be raw HTTP from C#, or a small local sidecar process.
2. **Who pays, and how is the API key stored?** The player would supply their own API key. Options include a per-mission budget (task budgets, beta) and showing the player a running cost.
3. **How big is the kOS reference prompt?** That is, how much kOS documentation Claudia needs in context to write correct scripts. This single number drives the cost estimate.
4. **Conversation strategy:** one append-only conversation per mission, or a new conversation per Maneuver Sequence seeded with a summary. This interacts with caching, preserved thinking and compaction.
5. **Effort per call type** (chat vs plan vs script), and whether Sonnet 5.5 is good enough at kOS scripting. This needs a small eval of kOS scripts that run in a simulator or in KSP.
6. **Refusal false positives** on rocketry and trajectory content, and fallback behaviour on Haiku, which has no server-side fallback.

## Sources

1. Anthropic, *Models overview*: https://platform.claude.com/docs/en/about-claude/models/overview (fetched 2026-10-23)
2. Anthropic, *Pricing*: https://platform.claude.com/docs/en/about-claude/pricing (fetched 2026-10-23): model prices, cache multipliers, batch, long context, tool-use system prompt tokens, fast mode
3. Anthropic, *Thinking*: https://platform.claude.com/docs/en/build-with-claude/thinking (fetched 2026-10-23): thinking billed as output; "Thinking block preservation by model" (all current models keep all prior turns)
4. Anthropic `claude-api` skill reference, bundled with Claude Code 2.1.307 (cached 2026-10-06), summarising the migration guide and the tool-use docs: forced `tool_choice` 400s, effort defaults, per-message effort, mid-conversation system messages, refusal fallbacks, preserved thinking, compaction, display modes. Secondary to [1] to [3], [5] and [6]; I did not re-fetch these points from the live docs.
5. Anthropic, *Structured outputs*: https://platform.claude.com/docs/en/build-with-claude/structured-outputs (fetched 2026-10-23)
6. Anthropic, *Prompt caching*: https://platform.claude.com/docs/en/build-with-claude/prompt-caching (fetched 2026-10-23): 512-token minimum, TTLs, automatic caching, invalidation by tools, thinking and effort
