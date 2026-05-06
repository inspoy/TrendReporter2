# INFRASTRUCTURE PROJECT KNOWLEDGE BASE

## OVERVIEW
Adapter project for YAML config loading, LiteDB persistence, NewsNow HTTP fetch, web extraction, OpenAI-compatible LLM calls, Unipush, and DI registration. Depends on Core only.

## STRUCTURE
```text
TrendReporter2.Infrastructure/
├── DependencyInjection.cs
├── Configuration/  # YAML loader
├── Enrichment/     # web extract client + run implementation
├── Llm/            # OpenAI-compatible cluster/judge clients
├── News/           # NewsNow client
├── Persistence/    # LiteDB connection, indexes, repositories, app state
└── Push/           # Unipush adapter
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Register implementation | `DependencyInjection.cs` | Add singleton mappings behind Core contracts. |
| YAML loading | `Configuration/YamlAppConfigLoader.cs` | CamelCase, ignores unknown properties, rewrites legacy key. |
| LiteDB schema | `Persistence/LiteDbInitializer.cs` | Explicit indexes for every collection, including `app_state`. |
| Content persistence | `Persistence/ContentIngestService.cs` | Upsert content, insert snapshots, dedup keys. |
| Event persistence | `Persistence/LiteDbEventRepository.cs` | Event, score, push-log, digest candidate queries. |
| App state persistence | `Persistence/LiteDbAppStateRepository.cs` | Implements `IAppStateRepository` for digest idempotency state. |
| LLM calls | `Llm/ClusterLlmClient.cs`, `Llm/JudgeLlmClient.cs` | OpenAI chat completions, JSON object responses. |
| News fetch | `News/NewsNowClient.cs` | Accepts `success` and `cache` statuses. |

## CONVENTIONS
- Implement Core interfaces here; do not add reverse project references.
- DI extension registers core services and persistence adapters as singletons; typed HTTP clients are registered in App `Program.cs`.
- HTTP adapters log warnings and return safe domain results where contracts allow degradation.
- LiteDB code opens short-lived connections via `LiteDbConnectionFactory.Open()`.
- YAML loader validates through Core `AppConfigValidator` immediately after deserialization.
- `LiteDbInitializer` creates `app_state` indexes on unique `Key` and `UpdatedAt`.

## ANTI-PATTERNS
- Do not hard-code collection names outside `TrendCollectionNames`.
- Do not create alternate DI registration sites for Infrastructure services.
- Do not leak adapter-specific types into Core contracts.
- Do not log full LLM or API responses without truncation; current clients normalize/truncate snippets.
- Do not commit or rely on runtime `data/` files.
