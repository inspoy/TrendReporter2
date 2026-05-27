# PERSISTENCE KNOWLEDGE BASE

## OVERVIEW
Persistence boundary. V2M0 owns PostgreSQL migrations and startup migration execution while retaining transitional LiteDB adapters for tests and pre-V2M1 repository code.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| PostgreSQL migrations | `SqlMigrationRunner.cs`, `Migrations/*.sql` | Discovers SQL files, verifies checksums, and applies startup migrations. |
| Legacy LiteDB indexes | `LiteDbInitializer.cs` | Transitional adapter only; do not register as the V2 default initializer. |
| Content ingest | `ContentIngestService.cs` | Transitional LiteDB-backed adapter until V2M1 PostgreSQL repositories replace it. |
| Event persistence | `LiteDbEventRepository.cs` | Event mappings, stale marking, scoring inputs, digest candidates, push logs. |
| App state persistence | `LiteDbAppStateRepository.cs` | `AppState` get/upsert by unique key. |
| Fetch run persistence | `FetchRunRepository.cs` | `fr:` ID format and run status updates. |

## CONVENTIONS
- Logical persistence names come from Core `TrendCollectionNames` or PostgreSQL migrations; `All` includes `app_state`.
- `SqlMigrationRunner` owns PostgreSQL schema setup; do not add LiteDB fallback or dual-write paths.
- `app_state` has a unique `Key` index and an `UpdatedAt` index.
- Content dedup key is lowercased source plus trimmed source item id.
- Content IDs use `ci:{category}:{source}:{short-hash}`; snapshots include run id and visual order.
- Event-item and push-log dedup keys are unique-indexed and duplicate insert races return false.
- Repository methods check cancellation before synchronous LiteDB work.

## ANTI-PATTERNS
- Do not mutate DB from debug-only/admin paths.
- Do not add collection names here without updating Core `TrendCollectionNames.All`.
- Do not bypass unique dedup indexes with precomputed IDs only.
- Do not leave a new PostgreSQL query path without a matching migration/index when it affects runtime scans.
- Do not store secrets or runtime DB files in the repo.

## VALIDATION
```bash
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
