# Compaction on purpose: on-demand compaction, its own settings, and keeping it from a cut-off reply

Research for #261 (part of map #243; #250's cut-off policy waits on it). Sources are the Claude API docs on platform.claude.com and the Python SDK source, read 2026-10-26, plus the Mun Mission's stream dumps. Every claim cites its source. "Not documented" means the pages below don't settle it. API facts from #245 (`docs/research/api-effort-tools-cache.md` on `research/api-effort-tools-cache`) are not repeated.

Scope: `claude-sonnet-5-5` and `claude-opus-5-5`, adaptive thinking, `output_config.format` json_schema, per-message effort, automatic tail caching plus system breakpoints at the 1-hour TTL. Today `MessagesRequest.ToJson()` always sends `context_management.edits: [{type: "compact_20260112", trigger: {input_tokens, CompactionTriggerTokens}, instructions: MissionPrompts.CompactionInstructions}]` with beta `compact-2026-01-12`. `MissionControl.Answered` fails the turn on `stop_reason: "max_tokens"` and commits nothing, so a compaction block in that response is lost and the retry compacts again.

Pages read:

- Overview: https://platform.claude.com/docs/en/build-with-claude/compaction
- On demand: https://platform.claude.com/docs/en/build-with-claude/compaction-on-demand
- Keep recent turns: https://platform.claude.com/docs/en/build-with-claude/compaction-keep-recent-turns
- Background: https://platform.claude.com/docs/en/build-with-claude/compaction-background
- With preserved thinking: https://platform.claude.com/docs/en/build-with-claude/compaction-thinking-blocks
- Threshold (what the mod uses): https://platform.claude.com/docs/en/build-with-claude/compaction-threshold
- Preserved thinking: https://platform.claude.com/docs/en/build-with-claude/preserved-thinking
- API reference, `POST /v1/messages` (beta): https://platform.claude.com/docs/en/api/beta/messages/create
- Python SDK tool runner: https://github.com/anthropics/anthropic-sdk-python/blob/main/src/anthropic/lib/tools/_beta_runner.py
- Structured outputs, effort and prompt-caching pages: none of them mentions compaction.

---

## Local evidence: what the Mun Mission's dumps show

From `~/KSP_spike/saves/Claudia Test 2/ClaudiaKermin/missions/m20261026-031725-724a/stream-dumps/` (Sonnet 5.5, threshold compaction at 150k, `max_tokens` 32000):

| Request | Compaction iteration (in / cache read / cache write 1h / out) | Message iteration (cache read / write / out) | stop |
| --- | --- | --- | --- |
| -48 | none | read 141,291, write 6,227 (1h), out 1,488 | end_turn |
| -49 | 320 / 13,136 / 112,323 / 4,321 | read 13,136, write 2,284 (**5m**), out 1,532 | end_turn (committed) |
| -50 | none | read 13,317, write 8,753 (1h) | end_turn |
| -70 | none | read 134,496, write 8,284 (1h), out 12,989 | end_turn |
| -71 | 320 / 13,317 / 80,321 / 4,888 | read 13,317, write 3,016 (**5m**), out 32,000 (all thinking) | **max_tokens** |
| -72 (retry) | 320 / 93,638 / 0 / 6,292 | read 13,317, write 3,014 (5m), out 32,000 thinking | max_tokens |
| -73 (retry) | 320 / 93,638 / 0 / 5,148 | read 13,317, write 3,488 (5m), out 32,000 thinking | max_tokens |

The cost log bills the three cut-offs at $0.71, $0.42 and $0.41.

What this shows:

1. **Every compaction summary (-49, -71, -72, -73) is a Mission reply JSON** (`{"kind":"answer","say":"MISSION SUMMARY ...","reply_intent":"none",...}`). `CompactionInstructions` asks for plain text.
2. **The compaction step reads only the system and tools prefix from the cache** (13,136 or 13,317 tokens), not the conversation's cached messages. It then writes the rest (80k to 112k tokens) as a new 1-hour entry, at 2x input price. The retries -72 and -73 read that entry back (93,638 tokens). So the summarizer's input is a different rendering from the conversation's, and it is about 15% to 40% smaller than the conversation it summarizes (the previous request plus its reply). That fits the threshold page's note that with custom `instructions`, "earlier thinking blocks are not part of the summarizer's input" on 5.1-and-later models.
3. **The compaction step's output did not come out of `max_tokens`.** The message iteration still produced the full 32,000.
4. **Each retry compacted again from scratch** (about 5k to 6k output tokens each time), because the failed turn committed nothing.
5. **The block from threshold compaction has no `signature`.** `content_block_start` carries `{"type":"compaction","content":null}`, and a single `compaction_delta` carries the content.
6. After compaction, the message iteration's cache write is billed at the **5-minute** TTL even though the request asks for 1 hour. The docs don't explain this.
7. In -71 to -73, all 32,000 output tokens after the compaction were thinking. Why the model thought that long is outside this ticket. Note that it happened on all three attempts, each time after a summary that was itself a reply JSON.

---

## 1. On-demand compaction: can the client trigger it, as its own request?

**Yes. A request with top-level `"compaction": {"type": "summarize"}` and beta `compact-2026-09-04` is a compaction request. It summarizes every message sent and returns only a signed `compaction` block, with `stop_reason: "compaction"`. Opus 5.5 and Sonnet 5.5 are both supported (beta on the Claude API).** Confidence: high.

- Shape of the request: same `model`, `system` and `tools` as the conversation, the messages as they stand, plus `"compaction": {"type": "summarize", "instructions"?: "..."}`. It "generates no reply after it, and returns the block alone with `stop_reason` `"compaction"`". The supported models list includes `claude-opus-5-5` and `claude-sonnet-5-5`. — compaction-on-demand, "Request a summary" and front matter
- Shape of the response: `content: [{"type":"compaction","content":"<summary text>","signature":"..."}]`, top-level `usage.input_tokens`/`output_tokens` both 0, and the cost in `usage.iterations[{type:"compaction",...}]`. When streamed, the block arrives whole in `content_block_start`, followed by `content_block_stop`, with no deltas. — compaction-on-demand, response sample and streaming note
- Continuing: send the block **first** in `messages`, either as an assistant message of its own or as the first content block of the first message. Remove the summarized messages, send exactly one block on every later request, keep the beta header, and send the block byte-exact, `signature` included. Changing it gives 400 `compaction_signature_invalid` or `compaction_content_mismatch`. A summarized message left in front of the block gives 400 `compaction_block_misplaced`. Two assistant messages in a row are fine after the block. — compaction-on-demand, "Continue from the summary" and "Errors"
- **It can't be mixed with the threshold kind.** "You can't send `compaction` and `context_management` on the same request. Threshold compaction (`compact_20260112`) can't run on a request that carries a signed block." The SDK runner refuses `compact_before_next_turn()` while a compaction edit is set, "because the API doesn't accept a compaction block together with one". So if the mod adopts on-demand compaction, it must drop the `compact_20260112` edit from every Mission request, or at least from every request that carries the signed block. — compaction-on-demand, "Limits and interactions"; `_beta_runner.py` `_check_can_compact`
- Constraints on the request: it is rejected if the last assistant turn ends in a `tool_use` with no result yet. The conversation must still fit the context window. `compaction_nothing_to_summarize` is returned if `messages` is empty. Images, documents and fetched URLs in the summarized range are gone afterwards. — compaction-on-demand
- Variants: keep the last N turns verbatim by leaving them out of the compaction request (no parameter for this; you pick the cut). Or run the request in the background while the conversation continues, then replace exactly the messages it held. — compaction-keep-recent-turns; compaction-background
- Failure: a missing summary is still a 200, with empty `content` and the summarizer's own `stop_reason` (`max_tokens`, `model_context_window_exceeded`, `tool_use`, `refusal`, `end_turn`). It is still billed. Check `stop_reason` before looking for the block. 529 `compaction_unavailable` is transient. — compaction-on-demand, "Handle a missing summary or an error"
- Threshold compaction can also stop after the block: see `pause_after_compaction` in question 4. That still runs inside an ordinary request, only when the threshold is reached, and the client can't call for it on demand.

## 2. Its own effort, thinking, max_tokens and instructions

**On-demand: yes. It is its own request, so it has its own `max_tokens`, thinking settings and `instructions`, and effort goes in its own `output_config`. Threshold: no. The step runs under the reply's settings, and only `instructions` is its own. A non-blank `instructions` replaces the default prompt entirely, in both kinds.** Confidence: high for `max_tokens`, thinking and instructions. Medium for effort: the docs don't name it, but the SDK sends it.

- On demand: "The summarization call uses the request's model, `system`, `tools`, thinking settings, and `max_tokens` … `max_tokens` caps the whole call, including any thinking … allow several thousand tokens." The docs' examples use 4096 to 8192 for the compaction request, against the conversation's own value. — compaction-on-demand, "Request a summary"
- Effort on the compaction request: **not documented** on the page. The rejected fields are only `stop_sequences`, `output_config.format`, `tool_choice` `any`/`tool`, and `output_config.task_budget.remaining`. The Python SDK's runner builds the compaction request from its own parameters and strips **only** `format` from `output_config` (`_without_format`). It also drops `context_management`, `stop_sequences` and `output_format`. So `output_config.effort` is sent and accepted. Whether effort changes how hard the summarizer thinks is not documented. — compaction-on-demand; `_beta_runner.py` `_without_compaction_incompatible_params`
- A cache trade-off with effort: per #245, a top-level effort change invalidates the messages cache. A compaction request at a different top-level effort from the conversation therefore can't read the conversation's cached messages, if it could read them at all (see question 5).
- Per-message effort inside the summarized range is lost. "A per-message effort level they set isn't carried over … turns run at the request's top-level `output_config.effort`, or the model's default." The same goes for mid-conversation system text instructions. Restate them in a `role: "system"` message right after the next new `user` turn. — compaction-on-demand, "Limits and interactions"; compaction-thinking-blocks, "Change the system prompt or tools"
- This matters for the mod. Mission effort lives only in `Message.SystemEffort` messages, and `mission.Effort` is set from them. After an on-demand compaction, the mod must re-send its effort message, or Sonnet 5.5 falls back to `high` and Opus 5.5 to `medium` (defaults per #245).
- Instructions, on demand: "Without `instructions`, the API uses its own summarization prompt. A non-blank `instructions` string (up to 16,384 characters) replaces that prompt entirely." It also says "The summarizer reads the whole conversation, earlier thinking included, with or without `instructions`." Tell it not to call tools. — compaction-on-demand, "Write your own summarization prompt"; API reference `compaction.instructions` (maxLength 16384; empty or whitespace counts as absent)
- Instructions, threshold: "Custom instructions don't supplement the default prompt. They replace it completely." On 5.1-and-later models, a request with custom `instructions` "summarizes from the visible conversation only: earlier thinking blocks are not part of the summarizer's input". The default prompts ask for `<summary></summary>` tags. — compaction-threshold, "Custom summarization instructions". The API reference describes the threshold field as "Additional instructions for summarization", which conflicts with the guide's "replace". The guide is more specific, so trust it.
- Threshold has no separate effort or `max_tokens` for the step. Its parameters are only `type`, `trigger` (at least 50,000), `pause_after_compaction` and `instructions`, and "The model specified in your request is used for summarization". Whether the summarizer's output counts against the request's `max_tokens` is **not documented**. The dumps show it does not: the message iteration still got its full 32,000 after the compaction step's 4,888. — compaction-threshold, "Parameters" and "Current limitations"; local dumps

## 3. Does `output_config.format` constrain the compaction summary?

**On demand: the question doesn't arise, because `output_config.format` on a compaction request is a 400. Threshold: the docs are silent, but the Mun Mission's dumps show the reply grammar does constrain the summary. All four summaries came back as Mission reply JSON, despite instructions asking for plain text.** Confidence: high for on-demand (stated). High for the threshold behaviour as observed (4 of 4 summaries over two compactions), but it is undocumented, so it could change.

- On demand: "leave out `stop_sequences`, structured-output `output_config.format`, and a `tool_choice` of type `any` or `tool`. They would do nothing on a summarization call, and the API rejects them." The SDK runner strips `format` for the compaction request and sends it again afterwards. — compaction-on-demand, "Request a summary"; `_beta_runner.py`
- Threshold: neither the compaction-threshold page nor the structured-outputs page mentions the other feature. Structured outputs says grammars "apply only to Claude's direct output, not to tool use calls, tool results, or thinking". The threshold summarizer's output evidently counts as direct output. — https://platform.claude.com/docs/en/build-with-claude/structured-outputs#feature-compatibility; local dumps -49, -71, -72, -73
- Effect on the mod: the summary is a JSON string with the summary inside `say`. It costs some tokens for the JSON wrapping, and the model sees a fake "answer" reply as the start of its context. That fake reply may have played a part in the 32,000-token thinking runs after it. That is a guess the docs don't support. On-demand compaction removes this, because the summary is written without the grammar.

## 4. Keeping a compaction from a cut-off reply

**Committing a complete threshold `compaction` block alone, dropping the truncated thinking, and then asking for the reply again isn't described for the cut-off case. Every rule that applies allows it, and it is exactly the shape `pause_after_compaction` produces. `pause_after_compaction: true` exists on `compact_20260112` and stops right after the block. On demand, the problem can't happen, because the compaction request has no reply to cut off.** Confidence: high that `pause_after_compaction` exists and works this way (stated). Medium that a block from a `max_tokens` response can be committed the same way (follows from the rules, but not stated for that case).

- `pause_after_compaction` (boolean, default `false`): "When enabled, the API returns a message with the `compaction` stop reason after generating the compaction block." The SDK example: `if response.stop_reason == "compaction": messages.append({"role":"assistant","content": response.content})`, then send the same request again with the compaction edit, and the API carries on with the reply. "This allows you to add additional content blocks … before the API continues." — compaction-threshold, "Pausing after compaction"; API reference `pause_after_compaction` ("Whether to pause after compaction and return the compaction block to the user")
- Why committing the block alone holds:
  - "When the API receives a `compaction` block, all content blocks before it are ignored." The docs let you keep the old messages or drop them yourself. — compaction-threshold, "Passing compaction blocks back"
  - Preserved thinking: "With server-side compaction, the checked prefix starts at the most recent compaction block." Removing `thinking` blocks "from the end" is listed as **Valid**. The truncated thinking was never sent back in any case. The edit-table row "Server-side compaction or context editing removes or replaces content" is also Valid. — preserved-thinking, "Keeping the prefix unchanged" and "What counts as an edit"
  - The threshold block carries no signature (dump), so nothing in it binds it to the cut-off thinking that followed.
- Does the next request compact again? It shouldn't. The trigger is input tokens, and with the block present the effective input is only system, tools, summary and the new turn. That was about 16k tokens in the dumps, against a 150k trigger. `count_tokens` "applies existing `compaction` blocks", and the pause example resends with the same edit and carries on. The docs never state outright that the trigger counts only tokens after the block. — compaction-threshold, "Token counting" and the pause example
- Cache: `cache_control` markers are outside the preserved-thinking check, and changing `max_tokens` or effort for the retry is also Valid. — preserved-thinking edit table
- **Not documented:** the max_tokens-then-commit-the-block case itself, and whether the response's other content can be partly kept. Don't keep any of it: the truncated thinking has an unfinished signature and no reply text.
- What #250 could do, in order of how well the docs back it:
  1. **Switch to on-demand compaction.** Compact between turns on the mod's own size check (`input + cache_read + cache_creation + output` from the last usage, per compaction-on-demand "When to compact"). Use its own `max_tokens` (around 8k), no `format`, the same `system` and `tools`, and the custom instructions. Drop the `compact_20260112` edit and send `compact-2026-09-04`. A cut-off reply can then never take a compaction with it, the summary is plain text, and a failed summary just means "continue and compact later". The cost is one extra request per compaction, plus re-sending the effort message afterwards (question 2).
  2. **Keep threshold compaction and set `pause_after_compaction: true`.** On `stop_reason: "compaction"`, commit the turn's messages plus `assistant{[compaction block]}`, then send again for the reply. A later `max_tokens` then only loses the reply. The summary stays grammar-constrained (question 3). `MissionControl.Answered` needs a `StopReason.Compaction` path, which it doesn't have today.
  3. **Salvage the block from a `max_tokens` response.** Same commit as option 2, done after the fact. The docs back the shape but not this case.
- Note for any option that keeps thinking: the turn after a compaction must not put a system message between the block and kept turns. "A system message placed between the block and the kept turns breaks their thinking." — compaction-thinking-blocks

## 5. Cache: what compaction does to the prefix, and where a deliberate compaction should go

**Compaction replaces the messages, so the cached prefix survives only up to the end of `system` and `tools`, and everything from the block on is written fresh. Keep a breakpoint at the end of `system`, which the mod already does, and one on or after the block. Compact on purpose at a turn boundary, not inside a tool round, with the same `system`, `tools`, model and top-level effort as the conversation, and only when the summary will be used.** Confidence: high for the prefix behaviour and breakpoint advice (stated). The docs are silent on whether a compaction request reads the conversation's cached messages. For threshold compaction, the dumps show it reads only system and tools.

- "When compaction occurs, the summary becomes new content that needs to be written to the cache. Without additional cache breakpoints, this would also invalidate any cached system prompt." Add `cache_control` at the end of the system prompt so "the system prompt cache remains valid and is read from cache" and "only the compaction summary needs to be written". `cache_control` on the block caches the summary. — compaction-threshold, "Prompt caching"; compaction-on-demand ("`cache_control` on the block places a breakpoint after the summary")
- The mod's `system` layers each carry a 1-hour breakpoint, and the dumps confirm the 13k system and tools prefix is read across the compaction. The automatic tail breakpoint covers the block.
- Cost of the compaction step itself:
  - Threshold: it is billed like any request. Its `usage.iterations` entry has its own `cache_read`/`cache_creation` fields (API reference `BetaCompactionIterationUsage`).
  - In the dumps, the threshold summarizer **did not** read the conversation's cached messages. It wrote 80k to 112k tokens as a new 1-hour entry, which only a retry of the same compaction ever reads. At Sonnet 5.5's 1-hour write rate (2x input), that is the largest single cost of a compaction, larger than its summary output.
  - On demand: the docs don't say whether a compaction request reads the conversation's cached messages. The page says the summarizer reads earlier thinking "with or without `instructions`", unlike threshold with custom instructions, so its rendering may be closer to the conversation's. **Not documented. Measure it**: send one on-demand compaction of exactly the last request's `messages`, then read `usage.iterations[0].cache_read_input_tokens`. The `diagnostics.previous_message_id` field (API reference) can explain a miss.
- Where to put a deliberate compaction:
  - **At a turn boundary**: after a committed reply, with no open `tool_use`. Compaction-on-demand rejects an open tool call, and preserved-thinking says "Don't compact between an assistant turn's `tool_use` and the `tool_result`." Compacting "exactly the `messages` of a request you already sent" is the cut the docs recommend for keeping kept turns' thinking valid. — compaction-on-demand; compaction-keep-recent-turns; preserved-thinking, "Compacting in the middle of a tool round"
  - **Same `system`, `tools` and model** as the conversation. The summarizer reads them, kept-turn thinking depends on them, and the cached system and tools prefix only matches if they are identical. **Same top-level effort too**: a top-level effort change invalidates the messages cache (#245), and any later request carrying the block can switch effort through a per-message system message.
  - **Before the threshold, and while the conversation is idle** (for example after a Debrief, before the next Maneuver Sequence), or in the background during a long run. Swap it in on the first request after it lands. Each compaction, and each failed one, costs one summarizer pass over the whole history.
  - **Once.** The dumps' cost was three summarizer passes and three cut-off replies for one compaction. Committing the block, by any of the options in question 4, makes the retry a cheap request over a ~16k prefix.
  - **Sending the block back costs nothing extra**: "Sending a block back on later requests adds no compaction cost." — compaction-on-demand, "Count compaction usage"
- **Not documented:** why the message iteration after a compaction wrote its tail at the 5-minute TTL in all four dumps when the request asked for 1 hour.
