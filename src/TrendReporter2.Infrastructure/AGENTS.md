# INFRASTRUCTURE PROJECT KNOWLEDGE BASE

## OVERVIEW
Adapter project for YAML config loading, PostgreSQL foundation, transitional persistence adapters, NewsNow HTTP fetch, web extraction, OpenAI-compatible LLM calls, Unipush, and DI registration. Depends on Core only.

## STRUCTURE
```text
TrendReporter2.Infrastructure/
├── DependencyInjection.cs
├── Configuration/  # YAML loader
├── Enrichment/     # web extract client + run implementation
├── Llm/            # OpenAI-compatible cluster/judge clients
├── News/           # NewsNow client
├── Persistence/    # SQL migrations plus transitional repository adapters
└── Push/           # Unipush adapter
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Register implementation | `DependencyInjection.cs` | Add singleton mappings behind Core contracts. |
| YAML loading | `Configuration/YamlAppConfigLoader.cs` | CamelCase, ignores unknown properties, rewrites legacy key. |
| SQL migrations | `Persistence/Migrations/` and `Persistence/SqlMigrationRunner.cs` | PostgreSQL schema setup and migration execution. |
| Content persistence | `Persistence/ContentIngestService.cs` | Transitional adapter until V2M1 PostgreSQL repositories replace LiteDB writes. |
| Event persistence | `Persistence/LiteDbEventRepository.cs` | Transitional adapter until V2M1 PostgreSQL repositories replace LiteDB writes. |
| App state persistence | `Persistence/LiteDbAppStateRepository.cs` | Transitional adapter until V2M1 PostgreSQL repositories replace LiteDB writes. |
| LLM calls | `Llm/ClusterLlmClient.cs`, `Llm/JudgeLlmClient.cs` | OpenAI chat completions, JSON object responses. |
| News fetch | `News/NewsNowClient.cs` | Accepts `success` and `cache` statuses. |

## CONVENTIONS
- Implement Core interfaces here; do not add reverse project references.
- DI extension registers the PostgreSQL data source, migration runner, core services, and transitional persistence adapters; typed HTTP clients are registered in App `Program.cs`.
- HTTP adapters log warnings and return safe domain results where contracts allow degradation.
- YAML loader validates through Core `AppConfigValidator` immediately after deserialization.

## ANTI-PATTERNS
- Do not hard-code collection names outside `TrendCollectionNames`.
- Do not create alternate DI registration sites for Infrastructure services.
- Do not leak adapter-specific types into Core contracts.
- Do not log full LLM or API responses without truncation; current clients normalize/truncate snippets.
- Do not commit or rely on runtime `data/` files.
