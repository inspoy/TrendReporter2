# EVENTS KNOWLEDGE BASE

## OVERVIEW
Highest-complexity Core area. Converts ingested content into event aggregates, recalls candidates, invokes cluster/judge LLM contracts, scores importance, applies blacklist rules, tracks progress, and creates push/digest inputs.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Event aggregate fields | `EventAggregate.cs` | Status, aliases, entities, milestones, blacklist fields, push history. |
| Repository/LLM contracts | `EventContracts.cs` | `IEventRepository`, `IAppStateRepository`, cluster/judge clients, pusher. |
| App state model | `AppState.cs` | Key/value state record used by scheduled digest idempotency. |
| Blacklist rules | `EventBlacklistPolicy.cs` | Matches configured keywords against event title/summary. |
| Candidate recall | `EventCandidateService.cs` | Similarity heuristics before LLM matching. |
| Merge/create flow | `EventMatcher.cs` | Active/stale recall, LLM decisions, reactivation. |
| Scoring and push | `EventScoringService.cs` | Base score, blacklist, judge adjustment, progress, push logs. |
| Score models | `EventScoringModels.cs` | Trigger reasons and progress stage constants. |

## CONVENTIONS
- Cluster decisions are string constants: `same_event`, `follow_up`, `related_but_distinct`, `unrelated`.
- Active events use normal merge threshold; stale/reactivated events use stricter stale threshold.
- LLM is optional: unconfigured cluster client returns create-new behavior.
- Scoring applies `EventBlacklistPolicy` before push eligibility and may invoke judge LLM only for eligible or near-eligible events.
- Digest loading happens through `IEventRepository.LoadDigestCandidatesAsync`; digest flow filters blacklisted events again before pushing.
- Push dedup keys include event/run/reason context; persistence enforces uniqueness.
- LLM parallelism is bounded by per-client `llm.*.maxParallel` settings.

## ANTI-PATTERNS
- Do not persist new event statuses, push types, trigger reasons, or progress stages without constants.
- Do not bypass `IEventRepository` or `IAppStateRepository`; Persistence owns database queries.
- Do not treat LLM failures as fatal to the fetch run; current clients degrade to neutral/create-new results.
- Do not remove cancellation checks in matching/scoring loops.
- Do not push without inserting/updating `PushLog` through the repository contract.

## NOTES
- `EventMatcher`, `EventScoringService`, and `EventBlacklistPolicy` are covered by xUnit tests under `tests/TrendReporter2.Tests`.
- Changes here usually need focused unit/regression tests plus manual scenario validation.
