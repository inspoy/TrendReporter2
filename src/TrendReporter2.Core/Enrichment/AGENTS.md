# ENRICHMENT KNOWLEDGE BASE

## OVERVIEW
Core enrichment contracts and policy. Decides which weak news/content items need extra context before event recall and defines status/summary constants used by persistence.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Need-enrichment rules | `EnrichmentPolicy.cs` | Source whitelist, title weakness, hover completeness. |
| Constants | `EnrichmentConstants.cs` | `SummarySources`, `EnrichmentStatuses`. |
| Client contract | `IEnrichmentClient.cs` | External adapter returns `EnrichmentResult?`. |
| Run service contract | `IEnrichmentService.cs` | Per-fetch-run enrichment summary. |
| Result model | `EnrichmentResult.cs` | Summary/title/url/raw payload from adapter. |
| Config | `../Configuration/AppConfig.cs` | `EnrichmentConfig` thresholds and WebExtract URL. |

## CONVENTIONS
- Policy works for both `NewsItem` before ingest and `ContentItem` after persistence.
- Enabled sources always enrich; otherwise complete hover text can skip enrichment.
- Title weakness uses Unicode-aware text length and CJK/entity-like subject detection.
- Summary source/status strings persist to LiteDB; use constants rather than literals.
- `OnlyWhenRecallWeak` exists in config but is not currently wired into recall flow.

## ANTI-PATTERNS
- Do not reintroduce Tavily-specific names into Core; current abstraction is generic enrichment/WebExtract.
- Do not put HTTP parsing or LiteDB writes in Core enrichment contracts/policy.
- Do not add new persisted statuses or summary sources without constants and persistence review.

## NOTES
- `RetryCooldownHours`, `MaxRequestsPerRun`, and `MaxParallelEnrichment` are enforced in Infrastructure, not policy.
- `DisabledSources` exists in `config.example.yaml` but is not modeled in current `EnrichmentConfig`; verify schema before documenting it as active.
- Enrichment should improve recall quality, not become a hard dependency for event matching.
- Core policy behavior has xUnit coverage under `tests/TrendReporter2.Tests`.

## VALIDATION
```bash
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
