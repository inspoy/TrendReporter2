# CORE PROJECT KNOWLEDGE BASE

## OVERVIEW
Dependency-free project for configuration models, domain entities, service contracts, shared constants, and core business rules. Core is not just abstractions; Events and Enrichment contain real algorithms.

## STRUCTURE
```text
TrendReporter2.Core/
├── Configuration/  # YAML model, validation, timezone helper
├── Content/        # content_item/content_snapshot models and ingest contract
├── Enrichment/     # enrichment contracts, statuses, policy
├── Events/         # event aggregate, matching, scoring, push contracts
├── Fetch/          # fetch_run model and repository contract
├── Jobs/           # app job contracts
├── News/           # raw news item and source-client contract
└── Persistence/    # database initializer contract and collection names
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Config shape/defaults | `Configuration/AppConfig.cs` | Mirrors YAML keys via camelCase binding. |
| Config validation | `Configuration/AppConfigValidator.cs` | Positive values, ratios, push times, timezone. |
| Collection list | `Persistence/TrendCollectionNames.cs` | Update before adding LiteDB collection. |
| Event domain | `Events/EventAggregate.cs` | Event lifecycle fields and status strings. |
| Matching algorithm | `Events/EventMatcher.cs` | Active/stale recall, stable anchors, LLM decision handling. |
| Scoring/push rules | `Events/EventScoringService.cs` | Eligibility, progress stages, push dedup. |
| Adapter contracts | `Events/EventContracts.cs`, `News/`, `Enrichment/` | Implement in Infrastructure. |

## CONVENTIONS
- Core has no project references; keep third-party adapters out.
- Constants are static string classes instead of enums when values persist to LiteDB.
- IDs and dedup keys are deterministic where possible; repositories may add run-time uniqueness.
- Business thresholds come from `AppConfig.Analysis` and `AppConfig.System`.
- User-facing validation errors are Chinese and specific.
- Prefer adding new contracts/models here, then implement adapters in Infrastructure.

## ANTI-PATTERNS
- Do not reference LiteDB, YamlDotNet, HttpClient adapters, OpenAI clients, or push SDKs from Core.
- Do not duplicate collection/status names as literals outside the existing constants.
- Do not weaken `AppConfigValidator` to make examples pass; fix config/defaults instead.
- Do not move event matching/scoring into Infrastructure just because it uses LLM contracts.

## VALIDATION
```bash
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
