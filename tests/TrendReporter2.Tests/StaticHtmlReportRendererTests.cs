using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Reports;
using TrendReporter2.Infrastructure.Reports;

namespace TrendReporter2.Tests;

public sealed class StaticHtmlReportRendererTests
{
    [Fact]
    public async Task RenderAsync_EscapesHtmlAndOnlyLinksHttpUrls()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "TrendReporter2.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var renderer = new StaticHtmlReportRenderer(new AppConfig { Report = new ReportConfig { OutputDirectory = outputDirectory, PublicBaseUrl = "https://reports.example/base" } });
            var payload = new ReportPayload
            {
                GeneratedAt = DateTimeOffset.Parse("2026-06-01T15:00:00Z"),
                WindowStart = DateTimeOffset.Parse("2026-06-01T09:00:00Z"),
                WindowEnd = DateTimeOffset.Parse("2026-06-01T15:00:00Z"),
                Events =
                [
                    new ReportEventItem
                    {
                        EventId = "event-1",
                        Title = "<script>alert(1)</script>",
                        Summary = "摘要 <b>bold</b>",
                        TotalScore = 88,
                        HeatValue = 2.7,
                        UniqueSourceCount = 3,
                        TriggerReasons = ["coverage_rank"],
                        Tags = ["AI"],
                        ContentItems =
                        [
                            new ReportContentItem { ContentItemId = "ci-1", Source = "安全源", Title = "恶意链接", Url = "javascript:alert(1)" },
                            new ReportContentItem { ContentItemId = "ci-2", Source = "可信源", Title = "正常链接", Url = "https://example.com/news?a=1&b=2" }
                        ]
                    }
                ]
            };

            var rendered = await renderer.RenderAsync(payload, CancellationToken.None);
            var html = await File.ReadAllTextAsync(rendered.FilePath);

            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
            Assert.DoesNotContain("href=\"javascript:alert(1)\"", html);
            Assert.Contains("恶意链接", html);
            Assert.Contains("href=\"https://example.com/news?a=1&amp;b=2\"", html);
            Assert.Contains("触发原因：coverage_rank", html);
            Assert.Equal("https://reports.example/base/digest-20260601-150000.html", rendered.PublicUrl);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
