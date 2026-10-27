# What the mod records about request costs and Spending Cap checks, 2026-10-26

Answers #321. It feeds the Auto Mode decision on judging whether the Spending Cap has room for the Debrief and the next plan. It describes the code at `8dda779` and the Missions in `~/KSP_spike/saves`. It does not make that decision.

Paths are under `src/ClaudiaKermin.Core/` unless they say otherwise.

## Summary

- **Cost is computed per request and stored per Mission, without the request's kind.** Each request gets a worst-case `pending` line before it is sent and a `paid`/`kept`/`released` line after. These lines go to the append-only `cost-log.jsonl`. Its lines carry no Purpose (Sequence Plan, Debrief, ...). Two exceptions: margin recheck and compaction `pending` lines carry that word as their `outcome`, and Craft Check lines have `craft_check_*` events. Per-kind cost per turn is written only to `plans.jsonl` (Sequence Plans and their Debriefs). The mod never reads that file back.
- **The mod reads back only running totals.** `ReadCostTotals` takes the last line's `spent`/`held`/`cap`. Nothing in the mod reads per-request or per-kind history.
- **The cap is checked both before and after a request.** Before: Committed + the request's own worst case must be ≤ cap (`MaySend`). After: settle the real cost, warn at 80%, and Pause once Committed ≥ cap (`AfterCharge`). The worst case is the request JSON length / 3 tokens priced as cache writes, plus a full `max_tokens` (32,000) of output. On Sonnet 5.5 that is at least $0.32 for any Mission request.
- **The skip checks use the same worst-case formula.** The margin recheck and its Second look also reserve a worst case for the Debrief that follows (`DebriefReserve`). The Mission Second look uses the just-answered request + reason + 400 characters. The Craft Check Second look uses the check's own spend against the entered cap.
- **In a Blackout, a request over the cap becomes the waiting call.** It is held unsent, nothing Pauses, and the Mission carries on until Raise cap. `AfterCharge` also skips its Pause at the cap.
- **Real cost is a small, highly variable fraction of the worst case.** Over 39 recorded Missions, all on Sonnet 5.5, a next-leg Sequence Plan cost p50 $0.096, p90 $0.197 and max $0.70. A sequence Debrief cost p50 $0.043, p90 $0.10 and max $0.66. Paid was p50 25% (Sequence Plan) and 10% (Debrief) of the pending worst case. The ratio's maximum was 0.64 and 0.93.

## 1. Where a request's cost is computed and stored

### Computed

- `Config/PriceTable.cs:30-111` holds the price table: Sonnet 5.5 at $2/$10 per MTok and Opus 5.5 at $4/$20.
  - A cache write costs 2× the input price and a cache read 0.1× (`:32-33`).
  - `CostOf` (`:81-89`) sums every `usage.iterations` entry, so compaction and fallback iterations are counted.
  - `WorstCase` (`:92-95`) prices every estimated input token as a cache write, plus full `maxTokens` output.
  - `docs/research/cost-baseline-2026-10-26.md` found that the 0.1× read multiplier over-charges by about 4.7%: the published read rate is 0.05×.
- `RequestCharge.WorstCase` (`Missions/RequestCharge.cs:43-47`) estimates input as `(request.ToJson().Length + 2) / 3` tokens.
- `RequestCharge.Settle` (`RequestCharge.cs:55-70`) decides how a request settles:
  - **paid**: real usage. This includes refused, cut-off and off-schema replies.
  - **kept**: the worst case is kept as Held when the request was cancelled or lost.
  - **released**: nothing is charged when the request was turned away before any reply (401, low credit, model not found, no network, HTTP status; `:72-85`).

### Stored in memory, per Mission

`MissionControl.cs:280-304` keeps four running figures: `Spent` (real usage), `Held` (kept worst cases, never refunded), `PendingCharge` (the in-flight request's worst case) and `Committed`.

`Committed = Spent + Held + CompactionCharge` (`:301-304`). The in-flight `PendingCharge` is not in `Committed`, but a compaction in flight is.

### Per request, at send and settle

1. `Send` (`MissionControl.cs:964-993`) runs `MaySend`. It then sets `PendingCharge = worstCase`, increments `Requests` and logs `pending` (`:980-983`).
2. `Finished` (`:1177-1187`) runs `Settle` (`:1772-1781`), which adds to Spent and Held. It then adds the request to `turn.Stats` and logs the settlement.
3. `AfterCharge` follows at `:1202`.

### The cost log

`MissionFolder.AppendCostEvent` (`Missions/MissionFolder.cs:75-99`) writes `cost-log.jsonl` in the Mission folder. It is append-only and never rewritten. Each line has these fields: `event`, `request` (a sequence number), `model`, `amount`, `outcome`, `spent`, `held`, `cap` and `at`.

The `event` values are `pending`, `paid`, `kept`, `released`, `blocked` (refused at the cap, outcome `over_cap`) and `cap_raised`. A Craft Check adds `craft_check_pending` and `craft_check_paid`/`craft_check_kept`/`craft_check_released`.

**No kind field.** The `outcome` of a paid line is completed, refused, cut_off, tool_use or off_schema (`MissionControl.cs:1784-1792`), not the Purpose. These lines are the exceptions:

| Request | Line that identifies it |
|---|---|
| Margin recheck | `pending` with outcome `margin_recheck` (`MissionControl.Debrief.cs:180`) |
| Compaction | `pending` with outcome `compaction`, then `compaction`/`compaction_failed` on its settlement (`MissionControl.Compaction.cs:177`, `:204`) |
| Craft Check | `craft_check_*`, folded into the Mission's log as it starts (`MissionControl.CraftCheckCost.cs:15-45`) |

Sequence Plans, Debriefs, Second looks, Script Check rewrites, re-plans (revised Mission Plans), Alerts and answers all log as plain `pending`/`paid`. They can be told apart only by matching debug `stream-dumps/` against `transcript.json`, as `tools/cost-baseline/baseline.py:159-274` does.

### `plans.jsonl`: the only per-kind cost record

`Missions/PlanLog.cs:17-140` writes one line per Sequence Plan offered. Each line has `purpose`, `script_rewrite`, `effort`, `requests`, `cost`, `output_tokens`, `thinking_tokens`, `reply_seconds`, `second_look` and `cut_off_retry`. When that sequence's Debrief commits, the line gains a `debrief` object with the same fields (`:112-126`).

`TurnStats.Cost` (`:22`, `:34-43`) sums every request the turn took: its Second look, its cut-off retry, its tool steps, and a Script Check rewrite, which carries its turn's stats on (`MissionControl.cs:203`).

The margin recheck is settled against a stand-in `Turn` (`MissionControl.Debrief.cs:207-214`), not the Debrief turn's `Stats`. So the Debrief's `cost` in `plans.jsonl` excludes its recheck.

Only Sequence Plans and their Debriefs get lines. Mission Plans, revised Mission Plans, Alerts, answers and compaction are not recorded per kind.

### What the mod reads back

- `MissionStore.ReadCostTotals` (`Missions/MissionStore.cs:301-345`) scans from the end of the log for the last parseable line and takes its `spent`, `held`, `request` and `cap`. A trailing `pending` with no settlement is added to Held.
- Its callers:
  - The load (`MissionControl.Persistence.cs:466-479`). Spend comes from the log, never from the save.
  - The Mission list (`Missions/MissionHistory.cs:74-85`). It shows "$x of $cap" (`Ui/MissionListRules.cs:24-28`).
  - The unreadable-log check (`MissionControl.cs:1695`).
- `PlanLog` has no reader in `src/`: `grep` finds only writes. Its only reader is the offline tool `tools/cost-baseline/baseline.py:423-437`.

**So the Mission's per-request history is on disk, but the mod reads none of it back.** Neither per request, per kind, nor across Missions.

## 2. How and when the Spending Cap is checked

### Before every Mission request: `MaySend` (`MissionControl.cs:1135-1149`)

`Committed + RequestCharge.WorstCase(request) <= SpendingCap`, or the request is not sent and a `blocked`/`over_cap` line is logged.

What happens to a blocked request depends on `waits`:

- **The request waits** (`WaitForCap`, `:1151-1164`). This applies when the turn has `Waits` set, the Mission is Running, or there is a Blackout (`:975-976`). The request is held unsent, a screen message is shown, and the Mission carries on.
- **The Mission Pauses** with Raise cap / End Mission (`PauseForCap`, `:1815-1819`). This applies otherwise.

These turns set `Waits`:

- a hand-back Debrief and its revision, and a late-Approval re-plan (`MissionControl.cs:181-186`, `:677`)
- Live questions (`MissionControl.Live.cs:207`)
- Manual Control turns (`MissionControl.Manual.cs:232`, `:245`)
- held words after a Blackout (`MissionControl.Blackout.cs:123`)

When a run ends, a question waiting at the cap goes back into the queue, so the Debrief goes first and Pauses at the cap (`MissionControl.Live.cs:110-127`).

### After every settled request: `AfterCharge` (`MissionControl.cs:1794-1813`)

1. At `Committed >= 0.8 × cap` it gives a one-time warning (`Warned`).
2. At `Committed >= cap` it Pauses for the cap. It does not Pause while Running or in a Blackout (`:1806-1808`).

A request that passed the pre-send check can still overrun the cap only if its real cost exceeds its worst case. By construction it does not, apart from the estimate of 3 characters a token. `AfterCharge` is the backstop.

### Raise cap

`Raised` (`:1821-1833`) logs `cap_raised` and re-arms the 80% warning. It resumes a cap-Paused Mission (`CarryOnIfCapCovers`, `:1836-1847`) or sends a waiting call.

### Other checks before sending

These checks never Pause:

- **Cut-off retry** (`CutOff`, `:1046-1066`). It resends automatically only if `HasCapRoom` (`:1107-1111`): the plain worst case of the rebuilt turn.
- **Compaction** (`StartCompaction`, `MissionControl.Compaction.cs:161-178`). It needs Committed + its worst case ≤ cap. The output part of that worst case is `CompactionMaxTokens` = 16,000 (`MissionPrompts.cs:98`). If there is no room it is deferred to the next start of Running.
- **Craft Check.** The first check is run against the cap the player entered (`MissionControl.cs:343`, `:415`). Its cost folds into the Mission's spend when the Mission starts.

### The "skipped when the Spending Cap has no room" checks

| Check | Estimate | Where |
|---|---|---|
| Margin recheck (first request) | `Committed + worst(recheck) + DebriefReserve > cap` → skipped. `worst` is (JSON length + 2)/3 input as cache writes, plus `max_tokens` 8,000 (`CraftCheckRecheck.cs:27`). | `MissionControl.Debrief.cs:148-174` |
| Margin recheck, each tool-loop step | The same test with the step's request | `MissionControl.Debrief.cs:345-352` |
| Margin recheck Second look | The same test with the high-effort resend's request | `MissionControl.Debrief.cs:251-275` |
| `DebriefReserve` | A probe of the Debrief request as it would be sent. It holds the system prompt, the sent conversation, the turn's user text and notice, the tools and the flight schema. Its JSON is padded with 4,000 characters and priced as `WorstCase(model, (len + 4000 + 2)/3, 32000)`. | `MissionControl.Debrief.cs:195-204` |
| Mission Second look (Sequence Plan, Debrief, etc.) | `Committed + worst <= cap`. `worst` is the request just answered, plus the Second-look note, plus 400 characters, all /3. It is priced at that request's `MaxTokens` (32,000). No reserve for anything after it. | `MissionControl.cs:1094-1105` |
| Craft Check Second look | The check's own spend so far (all steps, Spent + Held) + worst(resend) > the entered cap → skipped, with a KSP.log line only. | `Missions/CraftCheckSecondLook.cs:23-49` |

When one of these checks skips, it logs a `blocked`/`over_cap` line with the worst case it judged, except the Craft Check Second look. It also adds a Mission log notice, and the request it would have sent is simply not sent.

All the estimates are worst cases. None uses a measured or historical cost.

## 3. Blackout at the cap

`MissionControl.Blackout.cs:9-20` describes the Blackout. The mechanism is in `MissionControl.cs`:

- `Send` passes `waits = true` whenever `mission.Blackout` (`:975-976`, comment: "the player couldn't answer, so the request waits").
  - A request over the cap is held as `Pending` with `WaitingForCap = true` and a screen message. Nothing is sent and the Mission does not Pause.
  - In a Blackout the mod flies the next Sequence Plan and takes a revised Mission Plan without Approval. So a cap-blocked Debrief or plan request in a Blackout just stops the conversation where it is.
- `AfterCharge` returns before its Pause-at-cap when `mission.Blackout` (`:1806-1808`). The 80% warning still fires.
- The waiting call goes only on Raise cap (`Raised`, `:1830-1832`). Raise cap is the player's action, so in practice it happens after the link returns.
  - `BlackoutEnds` (`MissionControl.Blackout.cs:89-105`) does not itself re-check or resend a waiting call.
  - A run ending re-queues a waiting question (`MissionControl.Live.cs:117-126`), so the next request can Pause normally once there is no Blackout.

## 4. How much one kind's cost varies

### Recorded Missions

These figures come from 39 Missions in `~/KSP_spike/saves` (save "Claudia Test 2"), every request on `claude-sonnet-5-5`.

They were computed with `tools/cost-baseline/baseline.py`'s own `analyse_mission`: per-request cost over all usage iterations at published prices, kind matched from the stream dumps. The earlier `docs/research/cost-baseline-2026-10-26.md` covers 32 of these Missions and has the per-kind totals.

Real cost per request:

| Kind / asked for | n | min | p50 | mean | p90 | max | max/min |
|---|---:|---:|---:|---:|---:|---:|---:|
| Sequence Plan / next leg | 101 | $0.013 | $0.096 | $0.115 | $0.197 | $0.701 | 53× |
| Debrief / sequence | 76 | $0.007 | $0.043 | $0.064 | $0.101 | $0.659 | 92× |
| revised Mission Plan / after Off-Nominal Debrief | 10 | $0.067 | $0.204 | $0.250 | $0.427 | $0.517 | 8× |
| Mission Plan / first | 40 | $0.008 | $0.053 | $0.053 | $0.098 | $0.146 | 19× |
| Margin recheck (cost log `paid`) | 43 | $0.013 | $0.027 | $0.031 | $0.040 | $0.123 | 9× |
| Sequence Plan / Script Check rewrite | 11 | $0.033 | $0.075 | $0.079 | $0.101 | $0.159 | 5× |
| Craft Check (cost log `paid`) | 15 | $0.026 | $0.047 | $0.050 | $0.065 | $0.067 | 3× |
| Alert | 27 | $0.007 | $0.012 | $0.019 | $0.044 | $0.064 | 9× |
| answer | 22 | $0.003 | $0.009 | $0.018 | $0.029 | $0.105 | 36× |
| revised Mission Plan / after manual stretch | 5 | $0.020 | $0.075 | $0.065 | $0.105 | $0.118 | 6× |
| Debrief / manual stretch | 6 | $0.021 | $0.041 | $0.038 | $0.051 | $0.057 | 3× |

The pending worst case the mod charged, compared with what was paid, for requests paired in the cost log:

| Kind | n | pending p50 | pending max | paid ÷ pending min | p50 | max |
|---|---:|---:|---:|---:|---:|---:|
| Sequence Plan / next leg | 78 | $0.468 | $1.40 | 0.04 | 0.25 | 0.64 |
| Debrief / sequence | 54 | $0.516 | $1.34 | 0.05 | 0.10 | 0.93 |
| Mission Plan / first | 28 | $0.371 | $0.44 | 0.03 | 0.20 | 0.29 |
| revised Mission Plan / after Off-Nominal Debrief | 10 | $0.660 | $1.08 | 0.16 | 0.36 | 0.62 |
| Alert | 25 | $0.463 | $0.81 | 0.02 | 0.04 | 0.14 |

### What drives the spread

The earlier baseline and the reference Mun Mission `m20261026-031725-724a` show where the spread comes from:

- **Context growth.** That Mission reached 147,520 tokens of context.
- **Thinking.** Thinking is 76% of output tokens.
- **Compaction.** A compaction riding on a request is included in that request's usage.
- **Cut-offs.** A cut-off pays a full 32,000 tokens of output, about $0.32 on Sonnet 5.5, for nothing committed.
- **Mid-Mission cache misses.** These re-write the whole prefix.

The top values are these events. The $0.70 Sequence Plan and the $0.66 Debrief are tail requests of the Mun Mission. The floor of every Mission request's worst case is the 32,000-token output: $0.32 on Sonnet 5.5, $0.64 on Opus 5.5.

### Test fixtures

Tests such as `tests/ClaudiaKermin.Core.Tests/MissionDebriefTests.cs`, `MissionSecondLookTests.cs` and `CraftCheckCostTests.cs` write and read `cost-log.jsonl`. Their usage figures are set by each test to exercise the rules. They are not samples of real cost.

### Caveats

- Every recorded Mission is Sonnet 5.5, in one career save, made by one developer.
- The kind is inferred offline, and needs `debug_tools` stream dumps.
- Responses with no final usage (dropped streams) are unpriced.
- Per-request numbers count each request on its own. A turn's total (with its Second look or cut-off retry) is larger; `plans.jsonl` has turn totals for Sequence Plans and Debriefs only, and only for recent Missions (1 file found).
