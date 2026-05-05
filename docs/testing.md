# 测试与回归说明

M6 引入 xUnit 测试项目，覆盖核心规则、适配器解析、LiteDB 持久化、WebExtract 写回和回归样本。

## 命令

推荐完整验证顺序：

```bash
dotnet restore TrendReporter2.sln --configfile NuGet.Config
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet build TrendReporter2.sln --configuration Release --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet test TrendReporter2.sln --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```

若只想快速跑测试：

```bash
dotnet test tests/TrendReporter2.Tests/TrendReporter2.Tests.csproj
```

## 覆盖范围

- `EnrichmentPolicy`：强制信源、完整 hover、弱标题和可识别主体。
- `EventBlacklistPolicy`：大小写无关关键词命中和未命中。
- `EventCandidateService`：候选召回排序、特征和数量限制。
- `EventScoringService`：热度、趋势、资格、首次推送、重复推送、推送去重和黑名单阻断。
- `DigestJob`：摘要推送日志、状态幂等和重复时段跳过。
- `NewsNowClient`：`success/cache` 响应解析、坏条目跳过、fallback id。
- `LiteDbInitializer` / `ContentIngestService`：临时 LiteDB 初始化、重复初始化、内容幂等更新和快照写入。
- `WebExtractEnrichmentClient` / `EnrichmentService`：先解析响应体再判断富化成败、响应解析、成功富化写回。
- `UnipushPusher`：请求 URL、`Push-Key` 请求头和 JSON payload。

所有外部服务测试都使用 fake `HttpMessageHandler`、stub client 或临时 LiteDB 路径，不依赖 `config.yaml`、真实密钥或运行期 `data/`。

## 回归样本

固定样本位于 `tests/TrendReporter2.Tests/Fixtures/regression-corpus.json`，当前使用脱敏的真实风格新闻标题/摘要，覆盖：

- 可合并事件。
- 不应合并的相关但不同事件。
- stale 事件后续进展复活。
- 黑名单过滤。
- 推送去重样本。

新增回归样本时，应同时补充期望字段，并确保 `RegressionCorpusTests` 读取并验证该样本。
