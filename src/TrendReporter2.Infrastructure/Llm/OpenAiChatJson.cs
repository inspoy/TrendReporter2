using Newtonsoft.Json.Linq;

namespace TrendReporter2.Infrastructure.Llm;

internal static class OpenAiChatJson
{
    public static JObject ParseAssistantContent(string content)
        => JObject.Parse(UnwrapMarkdownJsonFence(content));

    private static string UnwrapMarkdownJsonFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        var fenceHeader = trimmed[..firstLineEnd].Trim();
        if (!string.Equals(fenceHeader, "```", StringComparison.Ordinal) &&
            !string.Equals(fenceHeader, "```json", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var body = trimmed[(firstLineEnd + 1)..].Trim();
        if (!body.EndsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return body[..^3].Trim();
    }
}
