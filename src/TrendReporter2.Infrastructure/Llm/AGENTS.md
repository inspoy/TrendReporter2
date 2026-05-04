# LLM ADAPTER KNOWLEDGE BASE

## OVERVIEW
OpenAI-compatible chat-completions adapters for event clustering and importance judging. These are Infrastructure implementations of Core LLM contracts; Core owns request/result models and decision constants.

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Cluster matching | `OpenAiClusterLlmClient.cs` | Determines whether a content item belongs to recalled candidates. |
| Judge/scoring adjustment | `OpenAiJudgeLlmClient.cs` | Adjusts event importance and labels. |
| Contracts/models | `../../TrendReporter2.Core/Events/EventContracts.cs` | `IClusterLlmClient`, `IJudgeLlmClient`, decisions. |
| Config | `../../TrendReporter2.Core/Configuration/AppConfig.cs` | `llm.cluster`, `llm.judge`, `llm.writer`. |

## CONVENTIONS
- Endpoint is `{baseUrl.TrimEnd('/')}/v1/chat/completions`.
- `ApiKey` is optional; when present it is sent as Bearer auth.
- Payloads request `response_format = { type = "json_object" }`.
- `MaxTokens` is clamped to at least 1.
- Responses are parsed with Newtonsoft `JObject` from the assistant content string.
- Invalid HTTP/JSON/decision responses degrade to create-new or neutral results, not thrown fatal errors.
- Logs truncate normalized response snippets before writing warnings.

## ANTI-PATTERNS
- Do not add SDK-specific dependencies unless the Core contracts change first.
- Do not let invalid or unconfigured LLM calls fail the whole fetch run.
- Do not accept decisions outside Core constants.
- Do not log raw full API responses, prompts containing secrets, or API keys.
- Do not move prompt strings into Core unless they become domain contracts.

## VALIDATION
Use config validation for shape. Real LLM behavior needs configured endpoints; unconfigured clients should safely degrade.
```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```
