# PERSISTENCE KNOWLEDGE BASE

## OVERVIEW
Persistence boundary. Owns PostgreSQL migrations, startup migration execution, and PostgreSQL-backed repositories.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| PostgreSQL migrations | `SqlMigrationRunner.cs`, `Migrations/*.sql` | Discovers SQL files, verifies checksums, and applies startup migrations. |
| Content ingest | `PostgresContentRepository.cs` | Content upserts, snapshots, rank/freshness scoring metadata. |
| Event persistence | `PostgresEventRepository.cs` | Event mappings, stale marking, scoring inputs, digest candidates, push logs. |
| App/fetch state persistence | `PostgresFetchRunRepository.cs` | Fetch run status updates and `AppState` get/upsert by key. |

## CONVENTIONS
- `SqlMigrationRunner` owns PostgreSQL schema setup; do not add LiteDB fallback or dual-write paths.
- `app_state` has a unique `Key` index and an `UpdatedAt` index.
- Content dedup key is lowercased source plus trimmed source item id.
- Content IDs use `ci:{category}:{source}:{short-hash}`; snapshots include run id and visual order.
- Event-item and push-log dedup keys are unique-indexed and duplicate insert races return false.

## ANTI-PATTERNS
- Do not mutate DB from debug-only/admin paths.
- Do not bypass unique dedup indexes with precomputed IDs only.
- Do not leave a new PostgreSQL query path without a matching migration/index when it affects runtime scans.
- Do not store secrets or runtime DB files in the repo.

## VALIDATION
```bash
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
