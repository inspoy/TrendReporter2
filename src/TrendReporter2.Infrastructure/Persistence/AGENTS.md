# PERSISTENCE KNOWLEDGE BASE

## OVERVIEW
LiteDB implementation boundary. Owns DB path resolution, collection/index initialization, content ingest, event repository queries, app state repository queries, fetch-run persistence, dedup enforcement, and snapshot writes.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| DB path/open | `LiteDbConnectionFactory.cs` | Expands env vars; resolves relative to current directory. |
| Collections/indexes | `LiteDbInitializer.cs` | Add indexes when adding collections or query paths. |
| Content ingest | `ContentIngestService.cs` | Content upsert plus per-run snapshot insert. |
| Event persistence | `LiteDbEventRepository.cs` | Event mappings, stale marking, scoring inputs, digest candidates, push logs. |
| App state persistence | `LiteDbAppStateRepository.cs` | `AppState` get/upsert by unique key. |
| Fetch run persistence | `FetchRunRepository.cs` | `fr:` ID format and run status updates. |

## CONVENTIONS
- Collection names come from Core `TrendCollectionNames` only; `All` includes `app_state`.
- `LiteDbInitializer.Initialize()` owns `EnsureIndex`; normal readers should not create schema.
- `app_state` has a unique `Key` index and an `UpdatedAt` index.
- Content dedup key is lowercased source plus trimmed source item id.
- Content IDs use `ci:{category}:{source}:{short-hash}`; snapshots include run id and visual order.
- Event-item and push-log dedup keys are unique-indexed and duplicate insert races return false.
- Repository methods check cancellation before synchronous LiteDB work.

## ANTI-PATTERNS
- Do not mutate DB from App data-view code.
- Do not add collection names here without updating Core `TrendCollectionNames.All`.
- Do not bypass unique dedup indexes with precomputed IDs only.
- Do not leave a new query path without a matching initializer index when it affects runtime scans.
- Do not store secrets or runtime DB files in the repo.

## VALIDATION
```bash
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
