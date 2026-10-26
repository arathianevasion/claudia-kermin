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
