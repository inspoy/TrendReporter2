# PROJECT KNOWLEDGE BASE

**Updated:** 2026-05-06 CST
**Branch:** master

## OVERVIEW
TrendReporter2 is a .NET 8 personal opinion-trend analyzer. It fetches ranked news from NewsNow, stores PostgreSQL-backed history, enriches weak items, merges items into event aggregates, scores event importance, pushes notable events, and sends scheduled digests.

## STRUCTURE
```text
TrendReporter2/
├── TrendReporter2.sln
├── NuGet.Config                  # nuget.org only; used by restore/CI
├── config.example.yaml           # committed template; copy to ignored config.yaml
├── docs/                         # Chinese design, milestones, C# layout notes, testing notes
├── prompts/                      # product/task prompt artifacts, not runtime code
├── tools/                         # standalone local diagnostics/helpers
├── tests/
│   └── TrendReporter2.Tests/     # xUnit tests and regression corpus
├── data/                         # ignored local runtime data
└── src/
    ├── TrendReporter2.App/       # executable, CLI modes, scheduling
    ├── TrendReporter2.Core/      # config, contracts, domain models, core rules
    └── TrendReporter2.Infrastructure/ # PostgreSQL, YAML, HTTP, LLM, push adapters
```

Dependency direction is fixed:
```text
App -> Core
App -> Infrastructure
Infrastructure -> Core
Core -> no project references
Tests -> App/Core/Infrastructure
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Entry point / CLI | `src/TrendReporter2.App/Program.cs` | Top-level statements; modes are custom-parsed. |
| Background fetch flow | `src/TrendReporter2.App/Scheduling/FetchJob.cs` | fetch -> ingest -> enrich -> match -> score/push. |
| Scheduled digest flow | `src/TrendReporter2.App/Scheduling/DigestJob.cs`, `DigestSchedulerService.cs` | Uses `pushTime`, `app_state`, and `push_log` for scheduled digest idempotency. |
| CLI parsing | `src/TrendReporter2.App/Program.cs` | Manual parser for background, validate, fetch-once, digest-once. |
| Config schema | `src/TrendReporter2.Core/Configuration/AppConfig.cs` | Strong typed YAML model and defaults. |
| Config validation | `src/TrendReporter2.Core/Configuration/AppConfigValidator.cs` | Required fields, ratios, times, timezone. |
| Persistence names | `src/TrendReporter2.Core/Persistence/TrendCollectionNames.cs` | Logical names retained for repository/table mapping, including `app_state`. |
| Event matching/scoring | `src/TrendReporter2.Core/Events/` | Matching, scoring, blacklisting, digest candidate rules, app state contract. |
| Enrichment policy/adapters | `src/TrendReporter2.Core/Enrichment/`, `src/TrendReporter2.Infrastructure/Enrichment/` | Weak-title policy, WebExtract calls, cooldowns, budgets. |
| PostgreSQL migrations / persistence | `src/TrendReporter2.Infrastructure/Persistence/` | SQL migrations, legacy adapters, IDs, dedup keys, repository queries, app state persistence. |
| HTTP NewsNow adapter | `src/TrendReporter2.Infrastructure/News/NewsNowClient.cs` | Calls `GET /api/s?id=source`. |
| NewsNow enrichment diagnostic | `tools/newsnow_fetch_test/` | Python venv helper; reads `sources.txt` and `.env`, tests HoverText/WebExtract/title length for configurable items per source. |
| LLM adapters | `src/TrendReporter2.Infrastructure/Llm/` | OpenAI-compatible chat completions; JSON-only responses. |
| DI boundary | `src/TrendReporter2.Infrastructure/DependencyInjection.cs` | Infrastructure implementations behind Core interfaces. |
| Tests | `tests/TrendReporter2.Tests/` | xUnit policy, persistence, adapter, digest, scoring, and regression corpus tests. |

## CODE MAP
| Symbol | Type | Location | Role |
|--------|------|----------|------|
| `CliOptions` | record | `App/Program.cs` | Manual CLI parser for background, validate, fetch-once, digest-once. |
| `FetchJob` | service | `App/Scheduling/FetchJob.cs` | End-to-end fetch run orchestration. |
| `DigestJob` | service | `App/Scheduling/DigestJob.cs` | Scheduled digest candidate filtering, message creation, push logging, state marking. |
| `DigestSchedulerService` | hosted service | `App/Scheduling/DigestSchedulerService.cs` | Minute poller for configured digest times in the configured timezone. |
| `AppConfig` | model | `Core/Configuration/AppConfig.cs` | YAML config object graph. |
| `EventMatcher` | service | `Core/Events/EventMatcher.cs` | Recall, LLM match, create/merge/reactivate events. |
| `EventScoringService` | service | `Core/Events/EventScoringService.cs` | Score, judge, blacklist, progress, push eligibility. |
| `EventBlacklistPolicy` | policy | `Core/Events/EventBlacklistPolicy.cs` | Applies configured blacklist keywords to events. |
| `IEventRepository` | contract | `Core/Events/EventContracts.cs` | Event, score, push-log, digest candidate persistence boundary. |
| `IAppStateRepository` | contract | `Core/Events/EventContracts.cs` | App state persistence boundary for digest idempotency. |
| `LiteDbEventRepository` | repository | `Infrastructure/Persistence/LiteDbEventRepository.cs` | Transitional LiteDB adapter for event, score, and push-log persistence until M1 PostgreSQL repositories replace it. |
| `LiteDbAppStateRepository` | repository | `Infrastructure/Persistence/LiteDbAppStateRepository.cs` | Transitional LiteDB adapter for `app_state` get/upsert until M1 PostgreSQL repositories replace it. |
| `ClusterLlmClient` | adapter | `Infrastructure/Llm/ClusterLlmClient.cs` | Event-cluster LLM decision adapter. |
| `JudgeLlmClient` | adapter | `Infrastructure/Llm/JudgeLlmClient.cs` | Event importance judge LLM adapter. |

## CONVENTIONS
- File-scoped namespaces; implementations usually `sealed`; small result shapes often `record`.
- Async APIs use `Async` suffix and accept `CancellationToken` at boundaries.
- Core can contain real business logic, not just interfaces; do not move matching, scoring, digest candidate rules, or blacklist policy into Infrastructure.
- Infrastructure owns concrete PostgreSQL, HTTP, YAML, LLM, and push implementations and registers them via `AddTrendReporterInfrastructure`.
- Collection names, statuses, decisions, push types, enrichment statuses, trigger reasons, and app state keys are constants or deterministic strings owned by Core/App boundaries.
- `config.yaml` is local and ignored; keep committed examples in `config.example.yaml`.
- YAML uses camelCase binding, but `YamlAppConfigLoader` rewrites legacy `web_extract_url` to `webExtractUrl`.
- Concurrency is config-backed (`MaxParallelFetch`, `MaxParallelEnrichment`, `MaxParallelLlm`) with `SemaphoreSlim`.

## ANTI-PATTERNS (THIS PROJECT)
- Do not commit real API keys, push secrets, local `config.yaml`, or runtime `data/` files.
- Do not introduce persistence collection/table names outside the Core-owned constants or migration files.
- Do not put PostgreSQL, LiteDB, HTTP, YAML, or LLM SDK dependencies into Core.
- Do not start hosted services, fetch, enrich, push, initialize collections, or mutate DB from debug-only paths.
- Do not add `System.CommandLine` for the current CLI; `Program.cs` uses a manual parser.
- Do not claim tests are missing; the solution includes `tests/TrendReporter2.Tests`.

## UNIQUE STYLES
- Logs and user-facing errors are mostly Chinese.
- Build docs intentionally disable shared compilation/parallelism for stability.
- `Program.cs` loads config before host construction, then either runs one-shot modes or starts Generic Host.
- `tools/newsnow_fetch_test/` is standalone Python: use venv + `requirements.txt`; keep local `.env` and `.venv/` ignored.
- M0-M6 are complete; scheduled digest is implemented by `DigestJob` and `DigestSchedulerService`.

## COMMANDS
```bash
dotnet restore TrendReporter2.sln --configfile NuGet.Config
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet build TrendReporter2.sln --configuration Release --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- fetch-once
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- digest-once
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
cd tools/newsnow_fetch_test && python3 -m venv .venv && source .venv/bin/activate && pip install -r requirements.txt && python newsnow_fetch_test.py
```

## NOTES
- CI runs `dotnet test`; the solution includes the xUnit project at `tests/TrendReporter2.Tests`.
- Validation baseline is build, tests, and `validate --config config.example.yaml`.
- Root `.gitignore` excludes `.sisyphus`, `config.yaml`, `bin/`, `obj/`, `data/`, and tool-local `.env` / `.venv` paths.
- `docs/tech_stack.md` is partly stale: it still mentions older milestone names like Tavily, but the current code uses WebExtract naming.
