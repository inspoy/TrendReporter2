# CORE PROJECT KNOWLEDGE BASE

## OVERVIEW
Dependency-free project for configuration models, domain entities, service contracts, shared constants, and core business rules. Core is not just abstractions; Events and Enrichment contain real algorithms.

## STRUCTURE
```text
TrendReporter2.Core/
├── Configuration/  # YAML model, validation, timezone helper
├── Content/        # content_item/content_snapshot models and ingest contract
├── Enrichment/     # enrichment contracts, statuses, policy
├── Events/         # event aggregate, matching, scoring, blacklist, push/app-state contracts
├── Fetch/          # fetch_run model and repository contract
└── Jobs/           # fetch and digest job contracts
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Config shape/defaults | `Configuration/AppConfig.cs` | Mirrors YAML keys via camelCase binding. |
| Config validation | `Configuration/AppConfigValidator.cs` | Positive values, ratios, push times, timezone. |
| Event domain | `Events/EventAggregate.cs` | Event lifecycle fields, blacklist fields, status strings. |
| Matching algorithm | `Events/EventMatcher.cs` | Active/stale recall, stable anchors, LLM decision handling. |
| Scoring/push rules | `Events/EventScoringService.cs` | Eligibility, blacklist, progress stages, push dedup. |
| Blacklist policy | `Events/EventBlacklistPolicy.cs` | Applies configured blacklist keywords to event title/summary. |
| App state contract | `Events/AppState.cs`, `Events/EventContracts.cs` | `AppState` model and `IAppStateRepository` contract. |
| Adapter contracts | `Events/EventContracts.cs`, `Sources/`, `Enrichment/` | Implement in Infrastructure. |

## CONVENTIONS
- Core has no project references; keep third-party adapters out.
- Constants are static string classes instead of enums when values persist across repository boundaries.
- IDs and dedup keys are deterministic where possible; repositories may add run-time uniqueness.
- Business thresholds come from `AppConfig.Analysis` and `AppConfig.System`.
- User-facing validation errors are Chinese and specific.
- Prefer adding new contracts/models here, then implement adapters in Infrastructure.
- App wires CLI, host, and jobs; Infrastructure implements PostgreSQL, HTTP, YAML, LLM, and push adapters.

## ANTI-PATTERNS
- Do not reference PostgreSQL, LiteDB, YamlDotNet, HttpClient adapters, LLM clients, or push SDKs from Core.
- Do not duplicate collection/status names as literals outside the existing constants.
- Do not weaken `AppConfigValidator` to make examples pass; fix config/defaults instead.
- Do not move event matching/scoring/blacklist rules into Infrastructure just because they use LLM or persistence contracts.

## VALIDATION
```bash
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
