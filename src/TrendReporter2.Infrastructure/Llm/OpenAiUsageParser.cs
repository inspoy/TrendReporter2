using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Observability;

namespace TrendReporter2.Infrastructure.Llm;

internal static class OpenAiUsageParser
{
    public static LlmUsageTokens Parse(JObject root)
    {
        var usage = root["usage"];
        if (usage is null)
        {
            return new LlmUsageTokens(null, null, null);
        }

        return new LlmUsageTokens(
            usage.Value<int?>("prompt_tokens") ?? usage.Value<int?>("input_tokens"),
            usage.Value<int?>("completion_tokens") ?? usage.Value<int?>("output_tokens"),
            usage["prompt_tokens_details"]?.Value<int?>("cached_tokens") ??
                usage["input_token_details"]?.Value<int?>("cache_read") ??
                usage.Value<int?>("prompt_cache_hit_tokens"));
    }
}

internal sealed record OpenAiChatParseResult<T>(
    T Result,
    bool Success,
    string? Error,
    string? RequestId,
    LlmUsageTokens Tokens);
