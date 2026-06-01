# INFRASTRUCTURE PROJECT KNOWLEDGE BASE

## OVERVIEW
Adapter project for YAML config loading, PostgreSQL persistence, NewsNow HTTP fetch, web extraction, OpenAI-compatible LLM calls, Unipush, and DI registration. Depends on Core only.

## STRUCTURE
```text
TrendReporter2.Infrastructure/
├── DependencyInjection.cs
├── Configuration/  # YAML loader
├── Enrichment/     # web extract client + run implementation
├── Llm/            # OpenAI-compatible cluster/judge clients
├── News/           # NewsNow client
├── Persistence/    # PostgreSQL migrations and repositories
└── Push/           # Unipush adapter
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Register implementation | `DependencyInjection.cs` | Add singleton mappings behind Core contracts. |
| YAML loading | `Configuration/YamlAppConfigLoader.cs` | CamelCase, ignores unknown properties, rewrites legacy key. |
| SQL migrations | `Persistence/Migrations/` and `Persistence/SqlMigrationRunner.cs` | PostgreSQL schema setup and migration execution. |
| Content persistence | `Persistence/PostgresContentRepository.cs` | Content ingest and snapshot writes. |
| Event persistence | `Persistence/PostgresEventRepository.cs` | Event mappings, scoring inputs, digest candidates, and push logs. |
| App/fetch state persistence | `Persistence/PostgresFetchRunRepository.cs` | Fetch run and app state repositories. |
| LLM calls | `Llm/ClusterLlmClient.cs`, `Llm/JudgeLlmClient.cs` | OpenAI chat completions, JSON object responses. |
| News fetch | `News/NewsNowClient.cs` | Accepts `success` and `cache` statuses. |

## CONVENTIONS
- Implement Core interfaces here; do not add reverse project references.
- DI extension registers the PostgreSQL data source, migration runner, core services, and PostgreSQL repositories; typed HTTP clients are registered in App `Program.cs`.
- HTTP adapters log warnings and return safe domain results where contracts allow degradation.
- YAML loader validates through Core `AppConfigValidator` immediately after deserialization.

## ANTI-PATTERNS
- Do not add PostgreSQL query paths without matching migrations or indexes when they affect runtime scans.
- Do not create alternate DI registration sites for Infrastructure services.
- Do not leak adapter-specific types into Core contracts.
- Do not log full LLM or API responses without truncation; current clients normalize/truncate snippets.
- Do not commit or rely on runtime `data/` files.
