# PROJECT KNOWLEDGE BASE

**Updated:** 2026-06-02 CST
**Branch:** master

## OVERVIEW
TrendReporter2 is a .NET 8 personal opinion-trend analyzer. It fetches ranked news from NewsNow, stores PostgreSQL-backed history, enriches weak items, merges items into event aggregates, scores event importance, pushes notable events, and sends scheduled digests. V2M0-V2M5 are complete: V2M0/V2M1 migrated the main path to PostgreSQL, V2M2 added run/stage/LLM telemetry, V2M3 introduced the source registry with NewsNow and DailyHotApi (ranked + flash), V2M4 added tag/event_tag and static HTML reports, and V2M5 added pgvector candidate recall (content_embedding/event_embedding, EmbeddingClient, vector recall merged with rule recall via `CompositeEventCandidateService`; rule recall is the fallback).

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
    │   └── Embeddings/           # embedding contracts, text builder, EmbeddingService
    └── TrendReporter2.Infrastructure/ # PostgreSQL, YAML, HTTP, LLM, push adapters
        └── Llm/                  # Cluster/Judge/Tagging/Embedding OpenAI-compatible clients
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
| Event matching/scoring | `src/TrendReporter2.Core/Events/` | Matching, scoring, blacklisting, digest candidate rules, app state contract. |
| Event candidate recall (rule + vector) | `src/TrendReporter2.Core/Events/EventCandidateService.cs`, `VectorEventCandidateService.cs` | `EventCandidateService` does rule recall; `CompositeEventCandidateService` merges rule + vector recall, with rule recall as the fallback when vector recall fails. |
| Enrichment policy/adapters | `src/TrendReporter2.Core/Enrichment/`, `src/TrendReporter2.Infrastructure/Enrichment/` | Weak-title policy, WebExtract calls, cooldowns, budgets. |
| Embedding contracts, text builder, run service | `src/TrendReporter2.Core/Embeddings/EmbeddingContracts.cs` | `IEmbeddingClient`, `IEmbeddingRepository`, `IEmbeddingService`, `EmbeddingTextBuilder`; content/event text composition and SHA-256 source-text hash for change detection. |
| Embedding adapter | `src/TrendReporter2.Infrastructure/Llm/EmbeddingClient.cs` | OpenAI-compatible `/v1/embeddings` client; retries up to 3 times, records `llm_usage.stage = embedding`, failure does not fail the fetch run. |
| Embedding repository | `src/TrendReporter2.Infrastructure/Persistence/PostgresEmbeddingRepository.cs` | Upsert `content_embedding`/`event_embedding`, hash-based skip, pgvector cosine similarity recall against recent and archive events. |
| PostgreSQL migrations / persistence | `src/TrendReporter2.Infrastructure/Persistence/` | SQL migrations, IDs, dedup keys, repository queries, app state persistence. `0007_embeddings.sql` adds `content_embedding`/`event_embedding` plus HNSW cosine index on `event_embedding`. |
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
| `IEventCandidateService` | contract | `Core/Events/EventContracts.cs` | Candidate recall boundary; `CompositeEventCandidateService` is the registered implementation. |
| `EventCandidateService` | service | `Core/Events/EventCandidateService.cs` | Rule-based candidate recall used by the composite service and as the vector-failure fallback. |
| `VectorEventCandidateService` | service | `Core/Events/VectorEventCandidateService.cs` | pgvector cosine-similarity recall; no-ops when `llm.embedding` is not configured. |
| `CompositeEventCandidateService` | service | `Core/Events/VectorEventCandidateService.cs` | Merges rule and vector candidates, dedupes, sorts, hard-filters, and caps at `analysis.event.candidateLimit`. |
| `PostgresEventRepository` | repository | `Infrastructure/Persistence/PostgresEventRepository.cs` | PostgreSQL event, score, push-log, and digest candidate persistence. |
| `PostgresFetchRunRepository` | repository | `Infrastructure/Persistence/PostgresFetchRunRepository.cs` | PostgreSQL fetch-run persistence and app-state repository implementation. |
| `IEmbeddingClient` | contract | `Core/Embeddings/EmbeddingContracts.cs` | Embedding API boundary; `IsConfigured` short-circuits when `llm.embedding` is missing. |
| `IEmbeddingRepository` | contract | `Core/Embeddings/EmbeddingContracts.cs` | Content/event embedding persistence and pgvector recall boundary. |
| `IEmbeddingService` | contract | `Core/Embeddings/EmbeddingContracts.cs` | Run-scoped content and event embedding generation boundary. |
| `EmbeddingService` | service | `Core/Embeddings/EmbeddingContracts.cs` | Generates per-run content/event embeddings, bounded by `System.MaxParallelLlm` and `llm.embedding.maxRequestsPerRun`. |
| `EmbeddingTextBuilder` | helper | `Core/Embeddings/EmbeddingContracts.cs` | Builds content/event embedding text and SHA-256 source-text hash for change detection. |
| `PostgresEmbeddingRepository` | repository | `Infrastructure/Persistence/PostgresEmbeddingRepository.cs` | Upsert `content_embedding`/`event_embedding`, hash-based skip, pgvector cosine similarity recall against recent and archive events. |
| `EmbeddingClient` | adapter | `Infrastructure/Llm/EmbeddingClient.cs` | OpenAI-compatible `/v1/embeddings` client; retries up to 3 times, records `llm_usage.stage = embedding`. |
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
- Embedding dimensions are currently fixed at 1536 by `AppConfigValidator`; mismatched vectors fail the embedding upsert path. pgvector `vector_cosine_ops` is the only HNSW operator currently used (on `event_embedding`).
- Embedding generation, vector recall, and the embedding LLM client must not fail the fetch run: failures degrade to empty results / rule-only recall / neutral outcomes.
- `CompositeEventCandidateService` always invokes rule recall first and catches vector-recall exceptions, then dedupes by `Event.Id`, picks the max score, unions matched features, and caps at `analysis.event.candidateLimit`.

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
- V1M0-V1M6 are complete; scheduled digest is implemented by `DigestJob` and `DigestSchedulerService`.
- V2M0-V2M5 are complete: V2M0/V2M1 PostgreSQL main path, V2M2 telemetry, V2M3 source registry, V2M4 tags and static reports, V2M5 pgvector candidate recall.

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
- Several nested `AGENTS.md` files under `src/TrendReporter2.Core/`, `src/TrendReporter2.Core/Events/`, `src/TrendReporter2.Infrastructure/`, `src/TrendReporter2.Infrastructure/Llm/`, and `src/TrendReporter2.Infrastructure/Persistence/` are out of date for V2M5 and do not yet mention embedding/vector recall entries.
