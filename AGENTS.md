# PROJECT KNOWLEDGE BASE

**Generated:** 2026-05-04 14:40:40 CST
**Commit:** 1f54037
**Branch:** master

## OVERVIEW
TrendReporter2 is a .NET 8 personal opinion-trend analyzer. It fetches ranked news from NewsNow, stores LiteDB history, enriches weak items, merges items into event aggregates, scores event importance, and can push notable events; scheduled summaries are still placeholder work.

## STRUCTURE
```text
TrendReporter2/
├── TrendReporter2.sln
├── NuGet.Config                  # nuget.org only; used by restore/CI
├── config.example.yaml           # committed template; copy to ignored config.yaml
├── docs/                         # Chinese design, milestones, C# layout notes
├── prompts/                      # product/task prompt artifacts, not runtime code
├── data/                         # ignored runtime LiteDB data
└── src/
    ├── TrendReporter2.App/       # executable, CLI modes, scheduling, data-view
    ├── TrendReporter2.Core/      # config, contracts, domain models, core rules
    └── TrendReporter2.Infrastructure/ # LiteDB, YAML, HTTP, LLM, push adapters
```

Dependency direction is fixed:
```text
App -> Core
App -> Infrastructure
Infrastructure -> Core
Core -> no project references
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Entry point / CLI | `src/TrendReporter2.App/Program.cs` | Top-level statements; modes are custom-parsed. |
| Background fetch flow | `src/TrendReporter2.App/Scheduling/FetchJob.cs` | fetch -> ingest -> enrich -> match -> score/push. |
| CLI DB inspection | `src/TrendReporter2.App/DataView/` | Read-only `data-view`; bypasses host services. |
| Config schema | `src/TrendReporter2.Core/Configuration/AppConfig.cs` | Strong typed YAML model and defaults. |
| Config validation | `src/TrendReporter2.Core/Configuration/AppConfigValidator.cs` | Required fields, ratios, times, timezone. |
| Collection names | `src/TrendReporter2.Core/Persistence/TrendCollectionNames.cs` | Only source for valid LiteDB collections. |
| Event matching/scoring | `src/TrendReporter2.Core/Events/` | Highest-complexity business logic. |
| Enrichment policy/adapters | `src/TrendReporter2.Core/Enrichment/`, `src/TrendReporter2.Infrastructure/Enrichment/` | Weak-title policy, WebExtract calls, cooldowns, budgets. |
| LiteDB schema/repositories | `src/TrendReporter2.Infrastructure/Persistence/` | Indexes, IDs, dedup keys, repository queries. |
| HTTP NewsNow adapter | `src/TrendReporter2.Infrastructure/News/NewsNowClient.cs` | Calls `GET /api/s?id=source`. |
| LLM adapters | `src/TrendReporter2.Infrastructure/Llm/` | OpenAI-compatible chat completions; JSON-only responses. |
| DI boundary | `src/TrendReporter2.Infrastructure/DependencyInjection.cs` | Infrastructure implementations behind Core interfaces. |

## CODE MAP
| Symbol | Type | Location | Role |
|--------|------|----------|------|
| `CliOptions` | record | `App/Program.cs` | Manual CLI parser for background, validate, fetch-once, data-view. |
| `FetchJob` | service | `App/Scheduling/FetchJob.cs` | End-to-end run orchestration. |
| `DataViewReader` | service | `App/DataView/DataViewReader.cs` | Generic read-only LiteDB collection reader. |
| `AppConfig` | model | `Core/Configuration/AppConfig.cs` | YAML config object graph. |
| `EventMatcher` | service | `Core/Events/EventMatcher.cs` | Recall, LLM match, create/merge/reactivate events. |
| `EventScoringService` | service | `Core/Events/EventScoringService.cs` | Score, judge, progress, push eligibility. |
| `IEventRepository` | contract | `Core/Events/EventContracts.cs` | Event persistence boundary. |
| `LiteDbInitializer` | service | `Infrastructure/Persistence/LiteDbInitializer.cs` | Creates collections and explicit indexes. |
| `LiteDbEventRepository` | repository | `Infrastructure/Persistence/LiteDbEventRepository.cs` | Event, score, push-log persistence. |
| `OpenAiClusterLlmClient` | adapter | `Infrastructure/Llm/OpenAiClusterLlmClient.cs` | Event-cluster LLM decision adapter. |

## CONVENTIONS
- File-scoped namespaces; implementations usually `sealed`; small result shapes often `record`.
- Async APIs use `Async` suffix and accept `CancellationToken` at boundaries.
- Core can contain real business logic, not just interfaces; do not move matching/scoring into Infrastructure.
- Infrastructure owns concrete LiteDB/HTTP/YAML/LLM/push implementations and registers them via `AddTrendReporterInfrastructure`.
- Collection names, statuses, decisions, push types, and enrichment statuses are string constants in Core.
- `config.yaml` is local and ignored; keep committed examples in `config.example.yaml`.
- YAML uses camelCase binding, but `YamlAppConfigLoader` rewrites legacy `web_extract_url` to `webExtractUrl`.
- Concurrency is config-backed (`MaxParallelFetch`, `MaxParallelEnrichment`, `MaxParallelLlm`) with `SemaphoreSlim`.

## ANTI-PATTERNS (THIS PROJECT)
- Do not commit real API keys, push secrets, local `config.yaml`, or runtime `data/` files.
- Do not introduce LiteDB collection names outside `TrendCollectionNames.All`.
- Do not put LiteDB, HTTP, YAML, or LLM SDK dependencies into Core.
- Do not start hosted services, fetch, enrich, push, initialize collections, or mutate DB from `data-view`.
- Do not add `System.CommandLine` for the current CLI; `Program.cs` uses a manual parser.
- Do not assume a formal test project exists; none is present yet.

## UNIQUE STYLES
- Logs and user-facing errors are mostly Chinese.
- Build docs intentionally disable shared compilation/parallelism for stability.
- `Program.cs` loads config before host construction, then either runs one-shot modes or starts Generic Host.
- Data-view is an admin/debug path in App that directly uses `LiteDbConnectionFactory`.
- M5/M6 are still in progress; `EmptyDigestJob` remains a placeholder.

## COMMANDS
```bash
dotnet restore TrendReporter2.sln --configfile NuGet.Config
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- fetch-once
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- data-view content_item --limit 20
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
```

## NOTES
- CI runs `dotnet test`, but the solution currently has no dedicated test project.
- Validation baseline is build plus `validate --config config.example.yaml`.
- Root `.gitignore` excludes `.sisyphus`, `config.yaml`, `bin/`, `obj/`, and `data/`.
- `docs/tech_stack.md` is partly stale: it still mentions older milestone names like Tavily, but the current code uses WebExtract naming.
