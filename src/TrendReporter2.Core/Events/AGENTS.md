# EVENTS KNOWLEDGE BASE

## OVERVIEW
Highest-complexity Core area. Converts ingested content into event aggregates, recalls candidates, invokes cluster/judge LLM contracts, scores importance, tracks progress, and creates push messages.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Event aggregate fields | `EventAggregate.cs` | Status, aliases, entities, milestones, push history. |
| Repository/LLM contracts | `EventContracts.cs` | `IEventRepository`, cluster/judge clients, pusher. |
| Candidate recall | `EventCandidateService.cs` | Similarity heuristics before LLM matching. |
| Merge/create flow | `EventMatcher.cs` | Active/stale recall, LLM decisions, reactivation. |
| Scoring and push | `EventScoringService.cs` | Base score, judge adjustment, progress, push logs. |
| Score models | `EventScoringModels.cs` | Trigger reasons and progress stage constants. |

## CONVENTIONS
- Cluster decisions are string constants: `same_event`, `follow_up`, `related_but_distinct`, `unrelated`.
- Active events use normal merge threshold; stale/reactivated events use stricter stale threshold.
- LLM is optional: unconfigured cluster client returns create-new behavior.
- Scoring may invoke judge LLM only for eligible or near-eligible events.
- Push dedup keys include event/run/reason context; persistence enforces uniqueness.
- LLM parallelism is bounded by `System.MaxParallelLlm`.

## ANTI-PATTERNS
- Do not persist new event statuses, push types, trigger reasons, or progress stages without constants.
- Do not bypass `IEventRepository`; Persistence owns LiteDB queries.
- Do not treat LLM failures as fatal to the fetch run; current clients degrade to neutral/create-new results.
- Do not remove cancellation checks in matching/scoring loops.
- Do not push without inserting/updating `PushLog` through the repository contract.

## NOTES
- `EventMatcher` and `EventScoringService` are algorithm-heavy; read the full file before changing behavior.
- Changes here usually need manual scenario validation because there is no test project yet.
