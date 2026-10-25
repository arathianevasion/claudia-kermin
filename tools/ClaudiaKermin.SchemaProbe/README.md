# Schema probe

Asks the API whether it compiles a structured-output schema (#199). It sends one Messages request through the mod's own `ClaudeClient`: `max_tokens` 16, a one-line user message, `output_config.format` set to the schema, and no retries. It prints the schema's union count, optional-parameter count and size (`SchemaComplexity.Size`: property definitions plus enum values), then the HTTP status and the API's error message. It never prints the key or any request header. Each run costs a few tokens, so run it by hand and never in CI.

```sh
export CLAUDIA_PROBE_KEY_FILE=/path/to/a/file/holding/the/key
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-planning   # as the mod sends it
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-flight     # as the mod sends it
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply:answer,sequence_plan,debrief   # any set of kinds
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- craft-check              # CraftCheckRules.SchemaJson()
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- tools/ClaudiaKermin.SchemaProbe/schemas/round2-flight-plus-one.json
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-flight --count   # counts only, sends nothing
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply-flight --print   # the schema JSON, sends nothing
```

A Mission request sends one of two schemas. The planning schema (answer, mission_plan, sequence_plan) goes with Mission Plan, Mission Settings, typed reply and change requests, and with questions before a Mission Plan is approved. The flight schema (answer, sequence_plan, debrief, alert) goes with everything else. `mission-reply-all` is every kind in one schema, which the API refuses.

`--model <id>` overrides the default model (`claude-haiku-5-5`, the cheapest model with structured outputs). Exit code 0 means the schema compiled (2xx), or was counted or printed. 1 means the API refused it or the request failed. 2 means bad usage.

`schemas/` holds frozen schemas for comparison:

| File | Unions | Size | What |
|---|---|---|---|
| `mission-reply-pre-65.json` | 11 | 75 | `36f6463^`, the Child Vessels branch before #65: compiled |
| `mission-reply-before-child-vessels-merge.json` | 12 | 89 | `6ea3805^1`, main just before the Child Vessels merge: worked in play |
| `mission-reply-main-34ccd14.json` | 14 | 95 | main when #199 was filed: refused |
| `mission-reply-fixed-plus-one-nullable.json` | 5 | 98 | every part with sentinels, plus one nullable field: refused |
| `round2-planning.json` | 2 | 65 | the planning schema as shipped |
| `round2-flight.json` | 3 | 78 | the flight schema as shipped |
| `round2-flight-plus-one.json` | 4 | 79 | the flight schema (the larger) plus one nullable number in `sequence_plan`, to check headroom |
| `round2-mission-plan-flat-child-vessels.json` | 2 | 64 | the planning schema with `child_vessels` as three parallel arrays, a fallback if planning is refused |

Before a schema grows, probe the bigger schema plus one more nullable field. Raise the budgets in `MissionReplySchemaTests` only after that probe compiles.
