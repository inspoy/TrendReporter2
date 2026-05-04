# ENRICHMENT ADAPTER KNOWLEDGE BASE

## OVERVIEW
Infrastructure implementation for run-level enrichment and WebExtract HTTP calls. This path is cost-controlled, concurrency-limited, and non-fatal to the main fetch run.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Run orchestration | `EnrichmentService.cs` | Candidate load, request budget, cooldown, save/fallback. |
| HTTP adapter | `WebExtractEnrichmentClient.cs` | POST `{baseUrl}/fetch` with `{ url }`. |
| Core policy | `../../TrendReporter2.Core/Enrichment/EnrichmentPolicy.cs` | Decides `NeedEnrichment`. |
| Refactor rationale | `../../../prompts/重构计划1.md` | Tavily cost drove generic WebExtract abstraction. |

## CONVENTIONS
- Candidates are current-run content items needing enrichment, not already succeeded, and outside retry cooldown.
- `MaxRequestsPerRun` caps attempted external calls; `MaxParallelEnrichment` bounds concurrency.
- Empty `enrichment.webExtractUrl` skips candidates and marks fallback status.
- WebExtract URL is normalized to include scheme and append `/fetch`.
- WebExtract response supports `summary`, `insights`, top-level or `data` fields; invalid JSON becomes raw summary text.
- Failed/empty responses return `null`; the service applies title/hover fallback and marks failed/skipped.
- Logs are Chinese and truncate response snippets.

## ANTI-PATTERNS
- Do not make enrichment failure fail the entire fetch run.
- Do not remove request budget, cooldown, or concurrency controls.
- Do not assume Tavily naming or API shape; current service is generic WebExtract.
- Do not let unconfigured WebExtract leave candidates permanently pending.
- Do not log full extracted pages or raw large responses.

## VALIDATION
```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
