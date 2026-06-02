# 测试与回归说明

项目使用 xUnit 覆盖核心规则、适配器解析、PostgreSQL migration/仓储、WebExtract 写回和回归样本。默认测试配置是离线 profile：不读取本地 `config.yaml`，不要求真实 PostgreSQL、NewsNow、DailyHotApi、WebExtract、LLM、embedding API、API key 或网络访问。

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

若只想验证 V2M8 回归语料 contract：

```bash
dotnet test tests/TrendReporter2.Tests/TrendReporter2.Tests.csproj --filter FullyQualifiedName~RegressionCorpusTests
```

## 测试 profile

默认 profile 是离线单元/回归测试：所有外部边界都必须使用 fake `HttpMessageHandler`、stub client、fixture metadata 或内存仓储，不允许真实 HTTP、真实 LLM/embedding 调用、真实 NewsNow/DailyHotApi/WebExtract 调用、真实推送、真实密钥、`config.yaml` 或运行期 `data/`。

PostgreSQL 仓储和 migration 属于可选集成 profile。仅当环境变量 `TRENDREPORTER2_POSTGRES_TEST_CONNECTION` 存在时，相关测试才连接真实 PostgreSQL，并在临时 schema 中执行迁移和清理；未设置该变量时集成测试直接返回，不影响默认离线测试结果。

## 覆盖范围

- `EnrichmentPolicy`：强制信源、完整 hover、弱标题和可识别主体。
- `EventBlacklistPolicy`：大小写无关关键词命中和未命中。
- `EventCandidateService`：候选召回排序、特征和数量限制。
- `EventScoringService`：热度、趋势、资格、首次推送、重复推送、推送去重和黑名单阻断。
- `DigestJob`：摘要推送日志、状态幂等和重复时段跳过。
- `NewsNowClient`：`success/cache` 响应解析、坏条目跳过、fallback id。
- `SqlMigrationRunner` / PostgreSQL migration：migration 排序、checksum 校验、幂等执行和核心 schema 创建。
- `PostgresContentRepository` / `PostgresEventRepository` / `PostgresAppStateRepository` / `PostgresFetchRunRepository`：内容幂等更新、快照写入、event item 去重、push log 去重、摘要候选、app state upsert 和 fetch run 状态更新。
- `WebExtractEnrichmentClient` / `EnrichmentService`：先解析响应体再判断富化成败、响应解析、成功富化写回。
- `UnipushPusher`：请求 URL、`Push-Key` 请求头和 JSON payload。

外部 HTTP 服务测试使用 fake `HttpMessageHandler` 或 stub client，不依赖 `config.yaml`、真实密钥或运行期 `data/`。

## 回归样本

固定样本位于 `tests/TrendReporter2.Tests/Fixtures/regression-corpus.json`，使用脱敏的真实风格新闻标题/摘要。V2M8 起，语料使用显式 V2 contract，而不是旧版 flat sample：

```json
{
  "id": "merge-ai-model",
  "kind": "merge",
  "summary": "Same-event AI model rollout should merge into an active candidate.",
  "offline": true,
  "inputs": { "incomingTitle": "..." },
  "fakes": { "backing": "in-memory", "externalServices": "none" },
  "expectations": { "mergedEvents": 1 }
}
```

字段约定：

- `id`：稳定、唯一、非空，用于定位回归样本。
- `kind`：样本类型。`RegressionCorpusTests` 要求至少包含 `merge`、`no-merge`、`reactivation`、`blacklist`、`push-dedup`、`flash-scoring`、`vector-fallback`、`secondary-merge-hard-filter`、`tag-generation`、`digest-filtering`。
- `summary`：样本意图说明，必须非空。
- `offline`：必须为 `true`，表示默认测试不接触外部服务。
- `inputs`：测试输入，例如 `incomingTitle`。
- `fakes`：fake 或 fixture 来源说明，`externalServices` 必须为 `none`。
- `expectations`：确定性期望；可包含 matcher 计数、黑名单关键词、push dedup key、trigger reasons、vector fallback metadata、hard-filter metadata、tag 列表或 digest exclusion flags。trigger reason 和 tag category 应优先使用 Core 中已有常量值，例如 `flash_multi_source`、`flash_repeated`、`topic`、`entity`、`domain`、`risk`。

当前语料覆盖：

- 可合并事件。
- 不应合并的相关但不同事件。
- stale 事件后续进展复活。
- 黑名单过滤。
- 推送去重样本。
- flash scoring trigger reasons。
- vector recall 失败后的规则 fallback metadata。
- secondary merge hard filter metadata。
- tag generation 的稳定 tag 名称/类别。
- digest filtering 的 merged/blacklisted 排除标记。

新增回归样本时，应补充 `expectations`，保持 `offline: true` 和 `fakes.externalServices: "none"`，并确保 `RegressionCorpusTests` 能读取和验证该样本。除非显式新增可选集成 profile，否则回归语料测试不得依赖真实 PostgreSQL、外部 API、密钥、网络或 `config.yaml`。
