# cost-baseline

Measures what each reply kind costs, from the mod's debug stream dumps. Written for #244 so a re-flight can be
measured the same way.

## Rerun

Fly with `debug_tools` on, so each Mission folder gets `stream-dumps/`. Then, from the repo root:

```sh
python3 -I tools/cost-baseline/baseline.py ~/KSP_spike/saves --out docs/research/cost-baseline-<date>.md
```

- The saves folder defaults to `~/KSP_spike/saves`. Without `--out` the report goes to stdout.
- `--reference <mission id>` names the Mission reported on its own (default `m20261026-031725-724a`, the Mun flight).
- Run it with `-I` and keep it outside the save folders: Mission folders are untrusted data. The script only reads them.

## How it counts

- Each `request-*.sse` is one request. Its last attempt with a `message_delta` gives the final usage. Cost is summed
  over every `usage.iterations` entry (message, compaction, fallback_message), and each cache write is priced at the
  TTL the API reported for it.
- A response is committed when its streamed `text_delta` length equals an assistant turn's text in `transcript.json`.
  The reply kind is the one the mod asked for in the system notice before that turn. A response that never committed
  (cut off, off-schema, refused, or abandoned) takes the kind of the next turn still to commit, or of
  `unanswered-turn.json`.
- Reply time is `pending` to `paid` (or `kept`/`released`) in `cost-log.jsonl`. Main requests pair with the dumps in
  order. Craft Checks and margin rechecks are not dumped, so only the mod's own `paid` amount is reported for them.
- Prices are in `PRICES` at the top of the script. Check them against
  <https://platform.claude.com/docs/en/about-claude/pricing> before each run.

## Replay screen for effort cuts (`replay.py`)

Proves an effort cut on recorded requests (#269, spec #265). Developer tooling outside the mod; stdlib only.

```sh
python3 -I tools/cost-baseline/replay.py ~/KSP_spike/saves --dry-run
python3 -I tools/cost-baseline/replay.py ~/KSP_spike/saves --kinds debrief --new-effort medium --out replay-debrief.md
```

- **Input.** Mission folders, or a saves folder holding them. Per request it uses the saved body
  (`stream-dumps/request-*.json`, written with `debug_tools` on) as it was sent. Without one it rebuilds the request from
  `transcript.json`: the Mission system layers and the history up to the reply. Kinds are told apart by the mod's notice
  before the reply: `debrief`, `change`, `reply`, `settings`. The Craft Check and the Margin recheck are in no transcript
  and the mod saves no request body for them yet, so they run only from `craft-checks/*.json` case files
  (`{"kind": "craft_check" | "margin_recheck", "body": {...}, "reply": "<text>", "input_tokens": n}`).
- **Rebuild check.** The cost log holds dollars, not tokens, so the check is against the input tokens in the matching
  stream dump. With no tokenizer, a rebuilt request must have characters per recorded token within `--tolerance`
  (default 15%) of its Mission's median over its recorded requests (at least 3 are needed). Anything else is listed as
  unusable with the reason and left out.
- **Replay.** About `--per-kind` (20) usable requests per kind, each `--repeats` (1) times at the old effort (the one
  recorded, or `--old-effort`) and at `--new-effort` (medium). Each goes through Claude Code print mode on the
  subscription: `claude -p` with model, `--effort`, the Mission system prompt, `--json-schema` (the saved body's, else a
  reduced one), `--tools ""`, the history flattened into the prompt on stdin, and `--max-budget-usd` (1.00 a call, set
  with `--max-budget-usd` here) as the safety net. Requests rebuilt from a transcript, which holds no model, use `--model`.
  The `claude` flags are untested against a real install: check them with `claude --help` before the first real run.
- **Agreement** is on parsed fields, not wording: Debrief `outcome`; the sets of Blockers and of Warnings for Craft Checks
  and Margin rechecks (Blocker codes are `BLOCKERS`, which a test checks against `CraftCheckRules.cs`); for Change, Reply
  and Settings, whether a revision (a Mission Plan or Sequence Plan) came back and the names of the Maneuver Sequences
  in it. A failed call or a reply that is not JSON is a disagreement.
- **Pass or fail** per kind: agreement at the new effort no more than 1 in 20 below the old effort's, and for Craft
  Checks and Margin rechecks no Blocker missed. A kind whose old effort never reproduces an original fails as having no
  yardstick. The exit code is 0 only when every kind passes. The pass is advice: the report prints every disagreement
  (case, effort, original and replay fields, `unsure_because`, errors) for a person to read, and the final call is theirs.
- **`unsure_because`** (Second look ticket): for replies that have the field, per kind and effort, how many said they were
  unsure, how many disagreed with the original, and how many of those disagreements were flagged unsure.
- **`--dry-run`** reads and checks the requests, prints how many calls it would make, and sends nothing.
- **Testing.** The runner is injectable (`replay.main(argv, runner=...)`, `replay.run(cases, runner)`), so the tests use
  a fake and never call `claude`.

## Batch API confirmation (`batch_confirm.py`)

Confirms a kind that passed the screen on about 5 exact requests through the Batch API, at the new effort.

```sh
python3 -I tools/cost-baseline/batch_confirm.py ~/KSP_spike/saves --kinds debrief,reply --dry-run
ANTHROPIC_API_KEY=... python3 -I tools/cost-baseline/batch_confirm.py ~/KSP_spike/saves --kinds debrief --approve debrief
```

- Only requests with a saved body are exact; rebuilt ones are skipped and counted. The body is sent as saved, without
  `stream`, with the turn's effort message set to `--new-effort`.
- For every kind in `--kinds` it first prints the cost estimate: the recorded input tokens at the full input rate (no
  cache credit) plus the original replies' output tokens, at the Batch API's 50% discount, and a worst case with
  `max_tokens` of output. Prices are `PRICES` in `baseline.py`.
- It sends a kind only when that kind has its own `--approve <kind>` flag (repeatable; there is no approve-all, and a kind
  not in `--kinds` is an error). With none it exits 2 having sent nothing. `--dry-run` never sends.
- Results are judged on the same parsed fields. It prints each disagreement and whether the kind is CONFIRMED (every
  request agrees and no Blocker missed) or NOT CONFIRMED.
- Only `api_submit` touches the network (stdlib `urllib`, key from `ANTHROPIC_API_KEY`, never printed). Tests pass a fake
  `submit`. The Batch request shape (the saved body, including `fallbacks`, as `params`) is unverified against the live
  API.

## Tests

```sh
python3 -I -m unittest discover -s tools/cost-baseline/tests -t tools/cost-baseline
```

The fixture Missions are written by `tests/fixtures.py`, hand-made and tiny; no real Mission folder is in the repo.
