# Recorded Claude streams

Hand-written server-sent event streams in the shapes the Messages API sends (per the streaming,
compaction and fallback docs). Tests replay them byte for byte through the real parser via a fake
transport. None came from a live API call, and none holds a key.

- `text.sse`: a text reply with pings, escaped and multi-byte characters, and `usage.iterations`.
- `thinking-tool-use.sse`: thinking (with a signature), text, then a tool_use with streamed input.
- `unknown-block.sse`: an unknown block type, an unknown event, an unknown delta and new fields everywhere.
- `compaction-fallback.sse`: a fallback block, a compaction block and per-attempt usage.
- `refusal.sse`, `max-tokens-tool.sse`, `context-window.sse`: stop reasons.
- `overloaded-error.sse`: an `error` event before any content.
- `partial-text.sse`, `start-only.sse`: streams cut short; the tests add the stall or the break.
- `mission-*.sse`: one Mission's replies in the Mission reply schema (structured output as text deltas):
  the Mission Plan, the Sequence Plan with its script, the Debrief, an ambiguous reply and an answer.
  `mission-sequence-plan-unsafe.sse` is a Custom Script that quicksaves, so it fails the Script Check;
  `mission-sequence-plan-rewritten.sse` is its rewrite, which passes.

- `mission-plan-manual.sse`: a Mission Plan of a link check then a manual stretch ("Hop", `manual: true`).

- `mission-plan-compacted.sse`: the Mission Plan reply to a long transcript (180000 tokens in): a compaction block
  with a summary holding quotes, a newline and non-ASCII text, then the plan, and a compaction iteration in `usage`.

- `craft-check-*.sse`: Craft Check replies (a summary and findings by code): pass, two Warnings, a Blocker with a Warning,
  and a finding outside the fixed lists.

`../snapshots/on-pad.json` is a hand-written Game Snapshot in the format the debug capture button
writes; replace or add captures from real play as they are taken.

`.gitattributes` keeps git from changing their bytes.
- `mission-*-priced.sse`, `mission-plan-big.sse`: the Mission replies above with large `usage.iterations`
  (100000 input, 50000 cache write, 200000 cache read and 10000 output tokens, which costs $0.54 on
  Sonnet 5.5; the big plan 100000 input, 100000 cache write and 20000 output, $0.80), for the Spending Cap tests.
- `mission-alert-*.sse`: Alert verdicts in the Mission reply schema: recommend Abort, carry on with a follow-up
  threshold of 1000, and a plain carry on.
- `compaction-extra-field.sse`: compaction blocks with an unknown field, one whole in its start event and one streamed through compaction deltas.
