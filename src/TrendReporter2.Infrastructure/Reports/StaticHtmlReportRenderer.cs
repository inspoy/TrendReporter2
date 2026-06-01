using System.Net;
using System.Text;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Reports;

namespace TrendReporter2.Infrastructure.Reports;

public sealed class StaticHtmlReportRenderer : IStaticHtmlReportRenderer
{
    private readonly AppConfig _config;

    public StaticHtmlReportRenderer(AppConfig config)
    {
        _config = config;
    }

    public async Task<RenderedReport> RenderAsync(ReportPayload payload, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(_config.Report.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var fileName = $"digest-{payload.WindowEnd:yyyyMMdd-HHmmss}.html";
        var filePath = Path.Combine(outputDirectory, fileName);
        await File.WriteAllTextAsync(filePath, BuildHtml(payload), Encoding.UTF8, cancellationToken);

        var publicUrl = string.IsNullOrWhiteSpace(_config.Report.PublicBaseUrl)
            ? null
            : _config.Report.PublicBaseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);
        return new RenderedReport(filePath, publicUrl);
    }

    private static string BuildHtml(ReportPayload payload)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<title>舆情摘要报告</title>");
        builder.AppendLine("<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;margin:0;background:#f6f7f9;color:#1f2937}.wrap{max-width:1080px;margin:0 auto;padding:32px}h1{margin:0 0 8px}.meta{color:#667085;margin-bottom:24px}.event{background:#fff;border:1px solid #e5e7eb;border-radius:14px;padding:20px;margin:16px 0;box-shadow:0 1px 2px rgba(16,24,40,.04)}.score{color:#475467}.tags{margin:12px 0}.tag{display:inline-block;background:#eef2ff;color:#3730a3;border-radius:999px;padding:3px 10px;margin:0 6px 6px 0;font-size:13px}.news{margin:12px 0 0;padding-left:20px}.news li{margin:8px 0}.source{color:#667085;font-size:13px}</style></head><body><main class=\"wrap\">");
        builder.AppendLine($"<h1>舆情摘要报告</h1><div class=\"meta\">生成时间：{Html(payload.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}；窗口：{Html(payload.WindowStart.ToString("yyyy-MM-dd HH:mm"))} - {Html(payload.WindowEnd.ToString("yyyy-MM-dd HH:mm"))}；事件数：{payload.Events.Count}</div>");

        foreach (var item in payload.Events)
        {
            builder.AppendLine("<article class=\"event\">");
            builder.AppendLine($"<h2>{Html(item.Title)}</h2>");
            builder.AppendLine($"<p>{Html(item.Summary)}</p>");
            builder.AppendLine($"<p class=\"score\">阶段：{Html(item.Stage ?? "Initial")}；总分 {item.TotalScore:F1}；Heat {item.HeatValue:F2}；来源数 {item.UniqueSourceCount}</p>");
            if (item.TriggerReasons.Count > 0)
            {
                builder.AppendLine($"<p>触发原因：{Html(string.Join(", ", item.TriggerReasons))}</p>");
            }

            if (!string.IsNullOrWhiteSpace(item.ProgressSummary))
            {
                builder.AppendLine($"<p>进程：{Html(item.ProgressSummary)}</p>");
            }

            if (item.Tags.Count > 0)
            {
                builder.AppendLine("<div class=\"tags\">" + string.Join("", item.Tags.Select(tag => $"<span class=\"tag\">{Html(tag)}</span>")) + "</div>");
            }

            if (item.ContentItems.Count > 0)
            {
                builder.AppendLine("<ol class=\"news\">");
                foreach (var content in item.ContentItems)
                {
                    var title = Html(content.Title);
                    var source = Html(content.Source);
                    var time = content.PublishedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未知时间";
                    var link = SafeHttpUrl(content.Url);
                    var titleHtml = link is null
                        ? title
                        : $"<a href=\"{Html(link)}\" target=\"_blank\" rel=\"noopener noreferrer\">{title}</a>";
                    builder.AppendLine($"<li>{titleHtml}<div class=\"source\">{source} · {Html(time)}</div></li>");
                }

                builder.AppendLine("</ol>");
            }

            builder.AppendLine("</article>");
        }

        builder.AppendLine("</main></body></html>");
        return builder.ToString();
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string? SafeHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }
}
