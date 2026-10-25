# Schema probe

Asks the API whether it compiles a structured-output schema (#199). It sends one Messages request through the mod's own `ClaudeClient`: `max_tokens` 16, a one-line user message, `output_config.format` set to the schema, and no retries. It prints the schema's union and optional-parameter counts, the HTTP status, and the API's error message. It never prints the key or any request header. Each run costs a few tokens, so run it by hand and never in CI.

```sh
export CLAUDIA_PROBE_KEY_FILE=/path/to/a/file/holding/the/key
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply            # MissionReply.SchemaJson as built
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- craft-check              # CraftCheckRules.SchemaJson()
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- tools/ClaudiaKermin.SchemaProbe/schemas/mission-reply-main-34ccd14.json
dotnet run --project tools/ClaudiaKermin.SchemaProbe -- mission-reply --count    # counts only, sends nothing
```

`--model <id>` overrides the default model (`claude-haiku-5-5`, the cheapest model with structured outputs). Exit code 0 means the schema compiled (2xx) or was counted. 1 means the API refused it or the request failed. 2 means bad usage.

`schemas/` holds frozen MissionReply schemas for comparison:

| File | Unions | What |
|---|---|---|
| `mission-reply-pre-65.json` | 11 | `36f6463^`, the Child Vessels branch before #65 |
| `mission-reply-before-child-vessels-merge.json` | 12 | `6ea3805^1`, main just before the Child Vessels merge |
| `mission-reply-main-34ccd14.json` | 14 | main when #199 was filed: every Mission request was a 400 |
| `mission-reply-fixed-plus-one-nullable.json` | 5 | the #199 schema plus one nullable number inside `child_vessels` items, to check headroom |

Before adding a nullable field (an `anyOf` or a type array) to a schema, probe the new schema plus one more. Raise the budget in `MissionReplySchemaTests` only after that probe compiles.
