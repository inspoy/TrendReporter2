# DATA VIEW KNOWLEDGE BASE

## OVERVIEW
Read-only CLI path for inspecting whitelisted LiteDB collections. It is an admin/debug utility, not part of the background host pipeline.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Mode branching | `../Program.cs` | Branches on `CliMode.DataView` immediately after config load. |
| Collection read | `DataViewReader.cs` | Reads `BsonDocument` from whitelisted collections. |
| Table/JSON output | `DataViewRenderer.cs` | Deterministic columns and JSON serialization. |
| Result shapes | `DataViewResult.cs`, `DataViewRow.cs` | Simple row/result DTOs. |
| Collection whitelist | `../../TrendReporter2.Core/Persistence/TrendCollectionNames.cs` | `TrendCollectionNames.All` only, including `app_state`. |

## CONVENTIONS
- `data-view <collection> [--limit <n>] [--json] [--config <path>]` is parsed in `Program.cs`.
- Collections are validated against `TrendCollectionNames.All` at parse/read time; `app_state` is just another whitelisted collection.
- Missing DB file fails explicitly; do not create an empty LiteDB file as a side effect.
- JSON mode must write valid JSON only to stdout, with no host/log noise.
- Nested documents/arrays become plain dictionaries/lists; JSON output reorders dictionaries deterministically.
- Table mode may truncate long cell values; JSON mode must not truncate.

## ANTI-PATTERNS
- Do not call `Host.CreateApplicationBuilder`, register hosted services, or initialize DB for data-view.
- Do not fetch, enrich, match, score, push, digest, or schedule from this path.
- Do not allow arbitrary collection names, editing, sorting, filtering, paging, CSV, or interactive UI unless explicitly requested.
- Do not move generic LiteDB inspection into Core; it depends on LiteDB.
- Do not print normal operational logs to stdout in JSON mode.

## VALIDATION
Use an isolated temp config/database when testing data-view. Do not rely on committed runtime data.
