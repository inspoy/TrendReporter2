# APP PROJECT KNOWLEDGE BASE

## OVERVIEW
Executable .NET 8 console app. Owns `Program.cs`, custom CLI modes, Generic Host startup, fetch and digest scheduling, one-shot jobs, and the read-only LiteDB data viewer.

## STRUCTURE
```text
TrendReporter2.App/
├── Program.cs        # top-level entry point, DI, and manual CLI parser
├── Scheduling/       # FetchJob, DigestJob, BackgroundService schedulers
└── DataView/         # read-only CLI inspection of known LiteDB collections
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Add CLI mode | `Program.cs` | Extend `CliOptions.Parse`; keep modes mutually exclusive. |
| Host service wiring | `Program.cs` | Config loads before `Host.CreateApplicationBuilder`. |
| Fetch pipeline | `Scheduling/FetchJob.cs` | Main run sequence and run summary logging. |
| One-shot digest | `Scheduling/DigestJob.cs` | Builds scheduled digest messages, writes push logs, marks `app_state`. |
| Periodic fetch | `Scheduling/FetchSchedulerService.cs` | Runs immediately, then by `analysis.fetchInterval`; non-reentrant. |
| Digest schedule | `Scheduling/DigestSchedulerService.cs` | Checks `analysis.push.pushTime` every minute in configured timezone. |
| Debug DB view | `DataView/` | Reads known collections without starting host services. |

## CONVENTIONS
- `Program.cs` accepts default background mode plus `validate`, `fetch-once`, `digest-once`, and `data-view`.
- Unknown args fail fast with Chinese usage text; do not silently ignore options.
- `--config` defaults to `config.yaml` and is converted to a full path.
- Hosted services are registered only for background mode.
- `FetchJob` tolerates enrichment/matching/scoring failures with warnings so one weak stage does not fail the whole fetch run.
- `DigestJob` applies `EventBlacklistPolicy`, uses `push_log` dedup keys, and records processed slots through `IAppStateRepository`.
- `FetchSchedulerService` and `DigestSchedulerService` use zero-wait `SemaphoreSlim` locks to skip overlapping runs.

## ANTI-PATTERNS
- Do not start `FetchSchedulerService` or `DigestSchedulerService` in `validate`, `fetch-once`, `digest-once`, or `data-view` modes.
- Do not add parser packages for current CLI changes.
- Do not put business rules in `Program.cs`; add to Core or a service behind a Core contract.
- Do not make `data-view` initialize collections, create indexes, fetch news, enrich, push, schedule, or mutate data.

## VALIDATION
```bash
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
