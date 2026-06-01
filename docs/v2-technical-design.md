# TrendReporter2 V2 技术设计稿

## 1. 文档目标

本文档用于将 [v2-design.md](v2-design.md) 中的 V2 方案落到可编码的工程设计，覆盖以下内容：

- PostgreSQL 主库、Npgsql、Dapper 和 SQL migration 的实现边界
- 当前 App、Core、Infrastructure 三层结构在 V2 中的调整方式
- source registry、source capability、ranked news、flash feed 和 topic 的抽象
- 内容、事件、评分、tag、report、embedding、merge history 的表结构与约束
- pgvector 候选召回、规则 fallback、二次归并的执行流程
- LLM usage、成本估算、阶段耗时、运行状态的可观测性设计
- 静态 HTML 报告、CLI、调度、幂等、失败降级和测试策略

本文档面向 V2，优先保证：

- PostgreSQL 一次性成为主路径，不保留 LiteDB provider
- 现有 V1 主链路可在新持久化层上继续工作
- 新概念先以小范围可验证方式落地，避免 V2 起步时变成大而全重写

## 2. 设计原则

### 2.1 PostgreSQL first

V2 从全新数据开始，不迁移历史 LiteDB 数据，不做双写，不提供 LiteDB 兼容 provider。所有 V2 持久化开发都以 PostgreSQL 为目标。

### 2.2 保留现有分层边界

依赖方向保持不变：

```text
TrendReporter2.App -> TrendReporter2.Core
TrendReporter2.App -> TrendReporter2.Infrastructure
TrendReporter2.Infrastructure -> TrendReporter2.Core
TrendReporter2.Core -> 无项目依赖
```

Core 继续承载业务规则，包括事件归并、评分、黑名单、摘要候选、tag 策略和向量召回结果合并。Infrastructure 只负责 PostgreSQL、HTTP、YAML、LLM、推送和报告文件写入等外部适配。

### 2.3 显式 SQL 优先

V2 主路径使用 `NpgsqlDataSource + Dapper + SQL migrations`。不引入 EF Core 作为主要持久化方案。原因是当前项目更需要可读 SQL、稳定索引和可解释查询，而不是 change tracking。

### 2.4 事件仍是产品核心

多 source、tag、embedding 和报告页面都服务于事件级趋势发现。TrendReporter2 仍不是新闻阅读器，也不是通用 dashboard。

### 2.5 召回增强，不替代规则

pgvector 用于增强候选召回。最终是否归并仍由硬过滤、规则特征和 Cluster LLM 决定。embedding 不可用或向量查询失败时，规则召回必须能继续运行。

### 2.6 初期输出静态报告

V2 初期生成静态 HTML 报告，不把动态 Dashboard、登录、多用户、Grafana 面板作为早期交付要求。PostgreSQL schema 为后续查询和监控留出空间即可。

## 3. 技术栈与运行方式

### 3.1 技术栈

| 类别 | V2 选择 | 说明 |
| --- | --- | --- |
| 运行时 | `.NET 8` | 沿用当前项目 |
| 后台服务 | `Generic Host + BackgroundService` | 沿用抓取和摘要调度模型 |
| 数据库 | `PostgreSQL` | V2 主库 |
| 数据库驱动 | `Npgsql` | 通过 `NpgsqlDataSource` 统一创建连接 |
| SQL 映射 | `Dapper` | 仓储层显式 SQL |
| 向量扩展 | `pgvector` | SQL migration 中执行 `CREATE EXTENSION IF NOT EXISTS vector` |
| 配置 | `YamlDotNet` | 沿用当前 YAML 加载方式 |
| JSON | `Newtonsoft.Json` | 沿用当前项目 |
| HTTP | `HttpClientFactory` | NewsNow、DailyHotApi、WebExtract、LLM、Unipush |
| 测试 | `xUnit` | 沿用现有测试项目和回归样本 |

### 3.2 运行方式

V2 仍是单进程后台程序：

1. `Program.cs` 解析 CLI 参数。
2. 加载 `AppConfig` 并执行 `AppConfigValidator`。
3. 通过 Infrastructure 注册 `NpgsqlDataSource`、SQL migration runner、仓储和外部 adapter。
4. `validate` 只校验配置和连接所需字段，不启动后台任务。
5. `fetch-once` 执行一轮 `FetchJob`。
6. `digest-once` 执行一轮 `DigestJob`。
7. 无命令时启动 `FetchSchedulerService` 和 `DigestSchedulerService`。

V2 移除 V1 的 `data-view` 命令。PostgreSQL 数据查看应通过 `psql`、SQL 客户端或后续只读报告解决，不在 App 中保留 LiteDB 调试路径。

## 4. 总体架构

### 4.1 模块划分

| 项目 | V2 责任 |
| --- | --- |
| `TrendReporter2.App` | `Program.cs`、CLI、Host、`FetchJob`、`DigestJob`、调度器、后续可选 `ReportJob` |
| `TrendReporter2.Core` | 配置模型、领域模型、仓储接口、source 抽象、事件规则、评分、tag 规则、报告 read model 契约 |
| `TrendReporter2.Infrastructure` | PostgreSQL 连接、SQL migrations、Dapper 仓储、NewsNow、DailyHotApi、WebExtract、LLM、Unipush、HTML 文件写入 |
| `TrendReporter2.Tests` | 单元测试、PostgreSQL 集成测试、回归样本 |

### 4.2 主链路

```text
FetchSchedulerService
  -> FetchJob
    -> ISourceRegistry
    -> IContentSourceClient
    -> IContentIngestService
    -> EnrichmentService
    -> IEventCandidateService
       -> RuleEventCandidateService
       -> VectorEventCandidateService
    -> EventMatcher
    -> EventScoringService
    -> PushDispatcher
    -> RunTelemetryRecorder
```

摘要和报告链路：

```text
DigestSchedulerService
  -> DigestJob
    -> DigestQueryService
    -> ReportReadModelQuery
    -> StaticHtmlReportRenderer
    -> PushDispatcher
    -> AppStateRepository
```

二次归并链路：

```text
SecondaryMergeJob
  -> EventPairCandidateService
  -> SecondaryMergePolicy
  -> ClusterLlmClient
  -> EventMergeRepository
  -> EventScoringService
```

`SecondaryMergeJob` 可先作为 `fetch-once` 后的内部阶段实现，也可后续独立成 CLI。V2 初期不要求新调度器。

## 5. 项目与模块变化

### 5.1 App 项目

| 当前模块 | V2 调整 |
| --- | --- |
| `Program.cs` | 移除 `data-view` 解析和执行分支，保留 `validate`、`fetch-once`、`digest-once`、后台模式 |
| `FetchJob` | 从 source registry 获取启用来源，按 capability 处理 ranked 和 flash 信号，记录 stage telemetry 和 LLM usage 汇总 |
| `DigestJob` | 继续使用 `app_state` 和 `push_log` 做摘要幂等，可附带静态报告路径 |
| `FetchSchedulerService` | 保持启动即抓取、周期抓取、防重入 |
| `DigestSchedulerService` | 保持分钟级检查、按 `pushTime` 触发、按时区处理 |
| `DataView` | V2 删除目录或停止注册，命令不再暴露 |

### 5.2 Core 项目

V2 Core 应新增或扩展以下契约：

| 模块 | 建议类型 | 责任 |
| --- | --- | --- |
| `Sources` | `SourceDefinition` | 描述 provider、external id、display name、content kind、capability、weight |
| `Sources` | `ISourceRegistry` | 返回启用 source，按 provider 和 kind 分组 |
| `Sources` | `IContentSourceClient` | 抓取外部内容并标准化为 `FetchedContentItem` |
| `Enrichment` | `EnrichmentResult` | 正文富化结果，包含 summary、正文元数据和 WebExtract 返回的 tags；`Tags` 映射自 WebExtract JSON 顶层 `insights` 字符串数组 |
| `Persistence` | `IContentRepository` | content item、snapshot、embedding 相关读写接口 |
| `Events` | `IEventRepository` | event、event item、score、push log、digest candidate、merge history |
| `Events` | `IEventCandidateService` | 规则召回和向量召回的组合接口 |
| `Events` | `ISecondaryMergeService` | 二次归并策略入口 |
| `Tags` | `ITagService` | 对未富化、富化跳过、富化失败或 WebExtract 未返回 tags 的内容生成 fallback tag，并统一去重、关联 content/event tag |
| `Reports` | `IReportReadModelQuery` | 查询静态报告所需的 read model |
| `Observability` | `IRunTelemetryRecorder` | fetch run、stage、source、LLM usage 记录 |

业务规则仍在 Core：

- source capability 判断
- ranked 和 flash scoring signal 的合并规则
- 候选召回结果合并和排序
- WebExtract tags 优先、`ITagService` fallback 的 tag 生成规则
- 二次归并硬过滤
- 摘要候选过滤

### 5.3 Infrastructure 项目

V2 Infrastructure 应新增或替换以下实现：

| 模块 | 建议类型 | 责任 |
| --- | --- | --- |
| `Persistence` | `PostgresDataSourceFactory` | 创建 app-wide `NpgsqlDataSource` |
| `Persistence` | `SqlMigrationRunner` | 读取并执行 SQL migrations |
| `Persistence` | `PostgresContentRepository` | Dapper 实现内容仓储 |
| `Persistence` | `PostgresEventRepository` | Dapper 实现事件仓储 |
| `Persistence` | `PostgresAppStateRepository` | Dapper 实现 app state |
| `Persistence` | `PostgresRunTelemetryRecorder` | 写入 run、stage、source、LLM usage |
| `Sources` | `NewsNowClient` | 继续支持 `GET /api/s?id=source` |
| `Sources` | `DailyHotApiClient` | 支持 ranked 和 flash 来源 |
| `Enrichment` | `WebExtractClient` | 继续作为正文和摘要增强 adapter，`EnrichmentResult.Tags` 读取 WebExtract JSON 顶层 `insights` 字符串数组 |
| `Llm` | `ClusterLlmClient`、`JudgeLlmClient`、`WriterLlmClient`、`EmbeddingClient` | 统一记录 usage |
| `Reports` | `StaticHtmlReportRenderer` | 从 read model 渲染 HTML 并写入本地目录 |

## 6. 配置设计

### 6.1 数据库配置

V2 `database` 配置改为：

```yaml
database:
  provider: "postgres"
  connectionString: "Host=localhost;Port=5432;Database=trendreporter;Username=trendreporter;Password=..."
  migrateOnStartup: true
```

校验规则：

| 字段 | 规则 |
| --- | --- |
| `database.provider` | 必须为 `postgres` |
| `database.connectionString` | 非空 |
| `database.migrateOnStartup` | 默认为 `true` |

V2 不保留 `database.path`，也不接受 `litedb` provider。

### 6.2 Source 配置

建议新增统一 sources 配置，逐步替代只面向 NewsNow 的结构：

```yaml
sources:
  newsNow:
    baseUrl: "http://localhost:3000"
    items:
      - id: "newsnow:china:ifeng"
        externalId: "ifeng"
        category: "china"
        displayName: "凤凰网"
        contentKind: "ranked_news"
        enabled: true
        weight: 1.0
  dailyHotApi:
    baseUrl: "http://localhost:6688"
    items:
      - id: "dailyhot:weibo"
        externalId: "weibo"
        category: "social"
        displayName: "微博热搜"
        contentKind: "ranked_news"
        enabled: true
        weight: 1.0
```

兼容策略：当前实现只保留统一 `sources` 配置，不再支持旧版 NewsNow 专用配置。

### 6.3 LLM 和 embedding 配置

在现有 `llm.cluster`、`llm.judge`、`llm.writer` 基础上增加：

```yaml
llm:
  embedding:
    baseUrl: ""
    apiKey: ""
    model: ""
    dimensions: 1536
    maxTokens: 2048
    pricing:
      cacheRead: 0
      input: 0
      output: 0
```

重试次数固定为代码常量，例如 `3`，不放入配置。所有 cluster、judge、writer、tagging、embedding 调用都写入 `llm_usage`。

### 6.4 Report 配置

```yaml
report:
  enabled: true
  outputDirectory: "./data/reports"
  publicBaseUrl: ""
  includeInDigestPush: true
```

`publicBaseUrl` 为空时，摘要推送只包含本地文件路径或不包含链接，由 `includeInDigestPush` 控制。

## 7. PostgreSQL 连接与 Migration 设计

### 7.1 `NpgsqlDataSource` 生命周期

Infrastructure 在 DI 中注册单例 `NpgsqlDataSource`：

```csharp
services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var config = sp.GetRequiredService<AppConfig>();
    return NpgsqlDataSource.Create(config.Database.ConnectionString);
});
```

仓储通过 `NpgsqlDataSource.OpenConnectionAsync(ct)` 获取连接，不自行解析 connection string。连接池由 Npgsql 管理。

### 7.2 Migration 表

新增 `schema_migration` 表：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `version` | `text` | migration 文件版本，主键 |
| `name` | `text` | 名称 |
| `checksum` | `text` | 文件 hash |
| `applied_at` | `timestamptz` | 执行时间 |

SQL 文件建议位于：

```text
src/TrendReporter2.Infrastructure/Persistence/Migrations/
  0001_init.sql
  0002_observability.sql
  0003_sources.sql
```

执行规则：

1. 启动时如 `migrateOnStartup = true`，先执行 migrations。
2. 每个 migration 在事务中执行。
3. 已执行版本 checksum 不一致时直接失败。
4. 第一版 migration 必须包含 `CREATE EXTENSION IF NOT EXISTS vector;`，用于提前验证本地和部署环境支持 pgvector。若目标环境暂时不能启用扩展，必须在 V2M0 明确记录为阻塞项，而不是在 V2M5 才发现。
5. pgvector HNSW 索引只在 embedding dimensions 固定后创建。

## 8. Schema 设计

### 8.1 命名约定

| 约定 | 说明 |
| --- | --- |
| 表名 | snake_case，沿用 V1 collection 名称 |
| 主键 | `uuid`，由应用生成或数据库 `gen_random_uuid()` 生成 |
| 时间 | `timestamptz` |
| JSON | `jsonb` |
| 状态 | `text` + check constraint |
| 金额 | `numeric(18,8)` |

### 8.2 `source`

| 字段 | 类型 | 约束 | 说明 |
| --- | --- | --- | --- |
| `id` | `text` | pk | 稳定 source id，如 `newsnow:china:ifeng` |
| `provider` | `text` | not null | `newsnow`、`daily_hot_api` |
| `external_id` | `text` | not null | provider 内部 id |
| `category` | `text` | not null | `china`、`social`、`tech` |
| `display_name` | `text` | not null | 中文展示名 |
| `content_kind` | `text` | not null | `ranked_news`、`flash_feed`、`topic` |
| `enabled` | `boolean` | not null | 是否启用 |
| `weight` | `double precision` | not null | 来源权重 |
| `created_at` | `timestamptz` | not null | 创建时间 |
| `updated_at` | `timestamptz` | not null | 更新时间 |

索引和约束：

- unique `(provider, external_id, content_kind)`
- index `(enabled, content_kind)`
- check `content_kind in ('ranked_news', 'flash_feed', 'topic')`

### 8.3 `fetch_run`、`fetch_run_source`、`fetch_run_stage`

`fetch_run`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | run id |
| `started_at` | `timestamptz` | 开始时间 |
| `finished_at` | `timestamptz` | 结束时间 |
| `status` | `text` | `running`、`succeeded`、`partial`、`failed` |
| `source_count` | `integer` | 来源总数 |
| `success_source_count` | `integer` | 成功来源数 |
| `failure_source_count` | `integer` | 失败来源数 |
| `fetched_item_count` | `integer` | 抓取条目数 |
| `enriched_item_count` | `integer` | 富化成功条目数 |
| `matched_event_count` | `integer` | 命中事件条目数 |
| `pushed_event_count` | `integer` | 推送事件数 |
| `estimated_llm_cost` | `numeric(18,8)` | 本轮 LLM 成本 |
| `error_summary` | `text` | 错误摘要 |

`fetch_run_source`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `run_id` | `uuid` | fetch run |
| `source_id` | `text` | source |
| `status` | `text` | `succeeded`、`failed`、`skipped` |
| `duration_ms` | `integer` | 耗时 |
| `item_count` | `integer` | 条目数 |
| `error` | `text` | 错误 |

主键 `(run_id, source_id)`。

`fetch_run_stage`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | 主键 |
| `run_id` | `uuid` | fetch run |
| `stage` | `text` | `fetch`、`ingest`、`enrich`、`match`、`score`、`push`、`report` |
| `started_at` | `timestamptz` | 开始 |
| `finished_at` | `timestamptz` | 结束 |
| `duration_ms` | `integer` | 耗时 |
| `status` | `text` | 状态 |
| `error` | `text` | 错误 |

### 8.4 `content_item`

| 字段 | 类型 | 约束 | 说明 |
| --- | --- | --- | --- |
| `id` | `uuid` | pk | content id |
| `source_id` | `text` | fk source | 来源 |
| `dedup_key` | `text` | not null | 稳定去重键 |
| `source_item_id` | `text` | nullable | 外部条目 id |
| `content_kind` | `text` | not null | 内容类型 |
| `title` | `text` | not null | 标题 |
| `url` | `text` | nullable | 原文链接 |
| `mobile_url` | `text` | nullable | 移动链接 |
| `published_at` | `timestamptz` | nullable | 发布时间 |
| `hover_text` | `text` | nullable | hover 或 source 摘要 |
| `summary` | `text` | nullable | 当前摘要 |
| `summary_source` | `text` | nullable | `title_only`、`hover_text`、`web_extract`、`writer` |
| `need_enrichment` | `boolean` | not null | 是否需要富化 |
| `enrichment_status` | `text` | not null | `none`、`pending`、`succeeded`、`failed`、`skipped` |
| `enrichment_tried_at` | `timestamptz` | nullable | 最近尝试时间 |
| `raw_payload` | `jsonb` | not null | 原始 JSON |
| `first_seen_at` | `timestamptz` | not null | 首次出现 |
| `last_seen_at` | `timestamptz` | not null | 最近出现 |
| `created_at` | `timestamptz` | not null | 创建 |
| `updated_at` | `timestamptz` | not null | 更新 |

索引和约束：

- unique `(source_id, dedup_key)`
- index `(content_kind, last_seen_at desc)`
- index `(need_enrichment, enrichment_status)`
- index `(published_at desc)`

### 8.5 `content_snapshot`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | snapshot id |
| `run_id` | `uuid` | fetch run |
| `content_item_id` | `uuid` | content item |
| `source_id` | `text` | source |
| `captured_at` | `timestamptz` | 抓取时间 |
| `visual_order` | `integer` | 页面或列表顺序 |
| `rank` | `integer` | 排名，无排名来源为空 |
| `source_list_size` | `integer` | 列表长度，无排名来源为空 |
| `normalized_rank_score` | `double precision` | ranked signal |
| `freshness_score` | `double precision` | flash signal |
| `raw_payload` | `jsonb` | source 当轮字段 |

索引和约束：

- unique `(run_id, content_item_id)`
- index `(content_item_id, captured_at desc)`
- index `(source_id, captured_at desc)`

ranked source 写入 `rank` 和 `normalized_rank_score`。flash source 不伪造 rank，只写 `freshness_score`。

### 8.6 `event`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | event id |
| `event_type` | `text` | `news_event`、`topic` |
| `canonical_title` | `text` | 标准标题 |
| `summary` | `text` | 摘要 |
| `status` | `text` | `active`、`stale`、`merged` |
| `merged_into_event_id` | `uuid` | 被二次归并后的目标事件 |
| `current_stage` | `text` | `initial`、`expanding`、`escalating`、`follow_up`、`cooling` |
| `progress_summary` | `text` | 进程摘要 |
| `first_seen_at` | `timestamptz` | 首次出现 |
| `last_seen_at` | `timestamptz` | 最近出现 |
| `last_activated_at` | `timestamptz` | 最近复活 |
| `last_pushed_at` | `timestamptz` | 最近推送 |
| `push_count` | `integer` | 推送次数 |
| `last_push_score` | `double precision` | 上次推送分数 |
| `last_push_rank_score` | `double precision` | 上次推送 ranked 分 |
| `last_push_source_count` | `integer` | 上次推送来源数 |
| `is_blacklisted` | `boolean` | 是否黑名单 |
| `blacklist_reason` | `text` | 黑名单原因 |
| `created_at` | `timestamptz` | 创建 |
| `updated_at` | `timestamptz` | 更新 |

索引：

- index `(status, last_seen_at desc)`
- index `(event_type, status)`
- index `(is_blacklisted)`
- index `(merged_into_event_id)`

### 8.7 `event_item`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | relation id |
| `event_id` | `uuid` | event |
| `content_item_id` | `uuid` | content item |
| `confidence` | `double precision` | 归并置信度 |
| `matched_at` | `timestamptz` | 归并时间 |
| `match_reason` | `text` | 归因说明 |
| `is_active` | `boolean` | 当前 evidence 是否有效 |
| `created_by_merge_id` | `uuid` | 二次归并迁移来源，可为空 |

索引和约束：

- unique `(event_id, content_item_id)`
- index `(content_item_id)`
- index `(event_id, is_active)`

二次归并不硬删除 evidence。迁移到目标事件时，原事件 evidence 可置为 `is_active = false`，新关系记录 `created_by_merge_id`。

### 8.8 `event_score_snapshot`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | score id |
| `event_id` | `uuid` | event |
| `run_id` | `uuid` | fetch run |
| `calculated_at` | `timestamptz` | 评分时间 |
| `coverage_score` | `double precision` | 覆盖分 |
| `rank_score` | `double precision` | ranked signal |
| `flash_score` | `double precision` | flash signal |
| `freshness_score` | `double precision` | 新鲜度 |
| `trend_score` | `double precision` | 趋势 |
| `persistence_score` | `double precision` | 持续性 |
| `llm_boost_score` | `double precision` | LLM 加权 |
| `reactivation_bonus` | `double precision` | 复活加分 |
| `total_score` | `double precision` | 总分 |
| `unique_source_count` | `integer` | 独立来源数 |
| `ranked_source_count` | `integer` | ranked 来源数 |
| `flash_source_count` | `integer` | flash 来源数 |
| `heat_value` | `double precision` | 热度 |
| `trigger_reasons` | `jsonb` | 触发原因 |
| `current_stage` | `text` | 阶段 |

索引：

- index `(event_id, calculated_at desc)`
- index `(run_id)`
- index `(total_score desc)`

### 8.9 `push_log` 和 `app_state`

`push_log` 沿用 V1 语义：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | push id |
| `event_id` | `uuid` | 即时推送关联事件，可为空 |
| `push_type` | `text` | `instant`、`digest` |
| `pushed_at` | `timestamptz` | 推送时间 |
| `title` | `text` | 标题 |
| `payload` | `jsonb` | 请求体 |
| `dedup_key` | `text` | 幂等键 |
| `success` | `boolean` | 是否成功 |
| `error` | `text` | 错误 |

约束：unique `(dedup_key)`。

`app_state`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `key` | `text` | 主键 |
| `value` | `text` | 序列化值 |
| `updated_at` | `timestamptz` | 更新时间 |

### 8.10 `tag`、`event_tag`、`content_item_tag`

V2M4 的 tag 获取路径：

1. 内容执行 WebExtract 富化且成功时，直接使用 `WebExtractClient.EnrichmentResult.Tags` 作为该内容的初始 tags；`Tags` 来自 WebExtract JSON 顶层 `insights` 字段，类型为字符串数组，层级与 `title`、`summary` 同级。
2. 内容未执行富化、富化被跳过、富化失败或 WebExtract 成功但未返回 tags 时，调用 `ITagService` 基于标题、hover text、summary、source category 和已有事件上下文生成 fallback tags。
3. `ITagService` 负责 tag 规范化、数量控制、去重和置信度归一化；它不重复调用已成功富化内容的 WebExtract tag 逻辑。
4. `content_item_tag` 记录内容级 tag 来源，`event_tag` 由事件关联内容的 tags 和事件级规则汇总得到。

`tag`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | tag id |
| `name` | `text` | 稳定名 |
| `display_name` | `text` | 中文展示名 |
| `category` | `text` | `topic`、`entity`、`domain`、`risk` |
| `created_at` | `timestamptz` | 创建时间 |

`event_tag`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `event_id` | `uuid` | event |
| `tag_id` | `uuid` | tag |
| `confidence` | `double precision` | 置信度 |
| `source` | `text` | `web_extract`、`rule`、`llm`、`manual` |
| `created_at` | `timestamptz` | 创建时间 |

`content_item_tag`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `content_item_id` | `uuid` | content item |
| `tag_id` | `uuid` | tag |
| `confidence` | `double precision` | 置信度 |
| `source` | `text` | `web_extract`、`rule`、`llm`、`manual` |
| `created_at` | `timestamptz` | 创建时间 |

V2 初期 tag 只用于展示和搜索，不驱动 push subscription，不改变即时推送资格。

### 8.11 `llm_usage`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | usage id |
| `run_id` | `uuid` | fetch run，可为空 |
| `stage` | `text` | `cluster`、`judge`、`writer`、`tagging`、`embedding` |
| `model` | `text` | 模型名 |
| `request_id` | `text` | 外部请求 id |
| `content_item_id` | `uuid` | 相关内容，可为空 |
| `event_id` | `uuid` | 相关事件，可为空 |
| `input_tokens` | `integer` | 输入 token |
| `output_tokens` | `integer` | 输出 token |
| `cache_read_tokens` | `integer` | 缓存读取 token |
| `estimated_cost` | `numeric(18,8)` | 估算成本 |
| `duration_ms` | `integer` | 耗时 |
| `success` | `boolean` | 是否成功 |
| `retry_count` | `integer` | 重试次数 |
| `error` | `text` | 错误 |
| `created_at` | `timestamptz` | 创建时间 |

索引：

- index `(run_id, stage)`
- index `(created_at desc)`
- index `(model, created_at desc)`

### 8.12 `content_embedding` 和 `event_embedding`

`event_embedding`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `event_id` | `uuid` | 主键 |
| `embedding_model` | `text` | 模型 |
| `embedding_version` | `text` | 版本 |
| `dimensions` | `integer` | 维度 |
| `source_text_hash` | `text` | 生成文本 hash |
| `embedding` | `vector(n)` | 向量 |
| `created_at` | `timestamptz` | 创建 |
| `updated_at` | `timestamptz` | 更新 |

`content_embedding` 同理，以 `content_item_id` 为主键。

HNSW cosine index 示例：

```sql
CREATE INDEX event_embedding_hnsw_cosine_idx
ON event_embedding
USING hnsw (embedding vector_cosine_ops);
```

只有在 `dimensions` 固定且表字段定义为 `vector(1536)` 这类定长向量后才创建索引。

### 8.13 `event_merge_history`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | merge id |
| `source_event_id` | `uuid` | 被合并事件 |
| `target_event_id` | `uuid` | 目标事件 |
| `confidence` | `double precision` | 置信度 |
| `reason` | `text` | 合并原因 |
| `decided_by` | `text` | `rule`、`llm`、`manual` |
| `evidence_snapshot` | `jsonb` | 合并前证据摘要 |
| `created_at` | `timestamptz` | 合并时间 |

约束：

- `source_event_id <> target_event_id`
- unique `(source_event_id, target_event_id)`

### 8.14 `report_snapshot`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | `uuid` | report id |
| `report_type` | `text` | `digest_html`、`daily`、`manual` |
| `slot_time` | `timestamptz` | 摘要时段 |
| `generated_at` | `timestamptz` | 生成时间 |
| `file_path` | `text` | 本地文件路径 |
| `public_url` | `text` | 可访问链接，可为空 |
| `event_count` | `integer` | 事件数 |
| `payload_json` | `jsonb` | 报告结构化内容 |

## 9. Source 抽象

### 9.1 Source capability

V2 source capability 至少包含三类：

| content kind | 说明 | 典型来源 | 评分信号 |
| --- | --- | --- | --- |
| `ranked_news` | 有排名的新闻或热榜 | NewsNow 榜单、DailyHotApi 热榜 | rank、normalized rank、source coverage |
| `flash_feed` | 无排名快讯或时间线 | 快讯、RSS、突发新闻 feed | freshness、多源短窗命中、重复出现 |
| `topic` | 社交或社区话题 | 微博、知乎、V2EX | V2 初期只入库和展示，评分后置 |

### 9.2 `IContentSourceClient`

建议接口：

```csharp
public interface IContentSourceClient
{
    string Provider { get; }

    Task<IReadOnlyList<FetchedContentItem>> FetchAsync(
        SourceDefinition source,
        CancellationToken cancellationToken);
}
```

`FetchedContentItem` 必须包含：

- `SourceId`
- `SourceItemId`
- `DedupKey`
- `ContentKind`
- `Title`
- `Url`
- `PublishedAt`
- `Rank`
- `SourceListSize`
- `HoverText`
- `RawPayload`

### 9.3 NewsNow

NewsNow ranked source 沿用 `GET /api/s?id=source`。映射规则：

- 原 `source` 字符串映射为 `source.external_id`
- 返回顺序映射为 `rank` 和 `visual_order`
- `HoverText` 继续用于摘要和富化判定
- 原始响应写入 `raw_payload`

### 9.4 DailyHotApi

DailyHotApi 初期支持两类来源：

- 有榜单顺序的接口映射为 `ranked_news`
- 只有发布时间或更新时间的接口映射为 `flash_feed`

DailyHotApi adapter 不应把 flash feed 强行补 rank。

## 10. 流程变化

### 10.1 FetchJob

V2 `FetchJob` 建议流程：

1. 创建 `fetch_run`，状态为 `running`。
2. 从 `ISourceRegistry` 读取启用 source。
3. 按 provider 找到对应 `IContentSourceClient`。
4. 有限并发抓取 source，写入 `fetch_run_source`。
5. 将抓取结果传给 `IContentIngestService`。
6. 对 `NeedEnrichment` 内容按预算调用 WebExtract；成功时保存 `EnrichmentResult.Tags`。
7. 对未富化、富化跳过、富化失败或 WebExtract 未返回 tags 的内容调用 `ITagService` 生成 fallback tags。
8. 统一 upsert tag、content_item_tag，并在事件归并后维护 event_tag。
9. 对内容执行规则召回和向量召回，合并候选。
10. 调用 `EventMatcher` 新建、更新或复活事件。
11. 执行 `EventScoringService`，分别计算 ranked 和 flash 信号。
12. 执行 push 判定并写入 `push_log`。
13. 可选生成静态报告。
14. 更新 `fetch_run` 统计和状态。

### 10.2 DigestJob

V2 `DigestJob` 保留 V1 幂等逻辑：

- `app_state` 记录 `digest:{yyyy-MM-dd}:{HH:mm}` 是否已执行
- `push_log.dedup_key` 使用同一摘要键
- 查询 `event_score_snapshot` 和 event 状态生成候选
- 过滤黑名单和 merged source event
- 可生成静态 HTML 报告并在摘要推送中附带链接

### 10.3 移除 data-view

V2 的 CLI 不再包含：

```text
data-view <collection> --limit N --json
```

移除原因：

- LiteDB 已不再是 V2 存储
- PostgreSQL 有成熟查询工具
- App 不应维护一套临时只读数据库浏览器

## 11. 评分变化

### 11.1 ranked signal

ranked source 继续使用源内归一化：

```text
normalizedRankScore = 1 - (rank - 1) / max(sourceListSize - 1, 1)
rankHeat = Σ(normalizedRankScore * sourceWeight)
```

### 11.2 flash signal

flash source 使用独立信号：

```text
freshnessScore = exp(-ageMinutes / freshnessHalfLifeMinutes)
flashHeat = Σ(freshnessScore * sourceWeight)
```

触发原因建议：

- `flash_multi_source`
- `flash_repeated`
- `flash_follow_up`
- `flash_trusted_source`

### 11.3 综合评分

V2 事件总分建议：

```text
TotalScore = 100 * (
  0.30 * coverageScore +
  0.20 * rankScore +
  0.15 * flashScore +
  0.15 * trendScore +
  0.10 * persistenceScore +
  0.10 * llmBoostScore
) + reactivationBonus
```

具体权重可在实现中沿用现有 `EventScoringService` 结构并小步调整。关键要求是 ranked 和 flash 分项在模型和表字段中分开保存。

## 12. LLM Usage 与成本

### 12.1 记录范围

以下调用必须写入 `llm_usage`：

- `cluster`
- `judge`
- `writer`
- `tagging`
- `embedding`

### 12.2 成本估算

成本计算：

```text
estimatedCost =
  inputTokens / 1_000_000 * pricing.input +
  outputTokens / 1_000_000 * pricing.output +
  cacheReadTokens / 1_000_000 * pricing.cacheRead
```

OpenAI compatible response 没有 `usage` 时，对应 token 字段置空或 0，但日志要能说明 usage 缺失，不要伪造 token。

### 12.3 重试记录

LLM adapter 固定最多重试 3 次。`retry_count` 记录实际额外尝试次数，最终失败时 `success = false`，`error` 保存简短错误。

LLM 失败不应导致整轮 fetch 失败，除非未来某阶段被明确标记为强依赖。

## 13. 可观测性

### 13.1 四层记录

| 层级 | 表 | 说明 |
| --- | --- | --- |
| run | `fetch_run` | 总状态、总耗时、总成本 |
| source | `fetch_run_source` | 每源成功率、条目数、错误 |
| stage | `fetch_run_stage` | fetch、ingest、enrich、match、score、push、report 耗时 |
| llm | `llm_usage` | token、成本、重试、失败率 |

### 13.2 日志字段

结构化日志应稳定包含：

- `runId`
- `sourceId`
- `contentItemId`
- `eventId`
- `stage`
- `durationMs`
- `model`
- `estimatedCost`

日志文案继续以中文为主。

## 14. 静态 HTML 报告

### 14.1 目标

静态报告用于把摘要结果变成可读页面，便于浏览事件、证据和来源链接。它不是动态 Dashboard。

### 14.2 Read model

`IReportReadModelQuery` 输出结构化 payload，包含：

- 报告生成时间
- 报告窗口
- 事件列表
- 每个事件的标题、摘要、阶段、进程摘要
- score、heat、source count、trigger reasons
- tag 列表
- 相关新闻列表
- 每条新闻的 source、发布时间、标题、原文链接

### 14.3 渲染流程

1. `DigestJob` 或 `ReportJob` 查询 read model。
2. `StaticHtmlReportRenderer` 渲染 HTML。
3. 文件写入 `report.outputDirectory`。
4. `report_snapshot` 记录 `file_path`、`public_url` 和 payload。
5. 摘要推送可附带报告路径或链接。

HTML 模板应保持静态资源简单，避免引入前端构建系统。

## 15. pgvector 召回

### 15.1 召回定位

pgvector 只增强候选发现，不直接决定事件归并。

### 15.2 生成文本

event embedding 建议使用：

```text
canonical_title + summary + representative_titles + key_terms + tags
```

content embedding 建议使用：

```text
title + hover_text + summary
```

写入时记录 `embedding_model`、`embedding_version` 和 `source_text_hash`，文本变化或模型变化时重算。

### 15.3 候选合并

`CompositeEventCandidateService` 负责：

1. 执行规则召回。
2. 若 embedding 可用，执行 vector recall。
3. 按 event id 去重。
4. 合并规则分和向量相似度。
5. 应用硬过滤。
6. 返回 `candidateLimit` 个候选。

vector 查询失败时记录 warning，并只返回规则召回结果。

## 16. 二次归并

### 16.1 前提

二次归并在 pgvector 召回稳定后实施。它用于修复 V1 和 V2 在线归并偏保守导致的事件拆分。

### 16.2 流程

1. 选择 active 或近期 stale 事件。
2. 用 event embedding 找相似事件对。
3. 使用硬过滤排除核心实体、时间、地点、数字明显冲突的候选。
4. 对高置信候选调用 Cluster LLM 判断。
5. 写入 `event_merge_history`。
6. 将 source event 标记为 `merged`，设置 `merged_into_event_id`。
7. 将 active evidence 迁移或复制到 target event，并保留 lineage。
8. 重新计算 target event 的 summary、tag 和 score。

### 16.3 误合并保护

- 不删除 `content_item`
- 不硬删除 `event`
- 不硬删除原始 `event_item`
- 保留 `event_merge_history.evidence_snapshot`
- 摘要和推送过滤 `status = merged` 的 source event

## 17. CLI 变化

V2 CLI 保留：

```text
validate [--config path]
fetch-once [--config path]
digest-once [--config path]
```

V2 移除：

```text
data-view
```

可选新增，不作为 V2 初期要求：

```text
migrate [--config path]
report-once [--config path]
secondary-merge-once [--config path]
```

`migrate` 如果实现，只执行 migration，不启动 Host。

## 18. 幂等、事务与失败处理

### 18.1 幂等键

| 场景 | 幂等方式 |
| --- | --- |
| content item | unique `(source_id, dedup_key)` |
| content snapshot | unique `(run_id, content_item_id)` |
| event item | unique `(event_id, content_item_id)` |
| push | unique `push_log.dedup_key` |
| digest | `app_state` + `push_log.dedup_key` |
| migration | `schema_migration.version` + checksum |
| merge | unique `(source_event_id, target_event_id)` |

### 18.2 事务边界

建议事务边界：

- 单个 source ingest 使用一个事务
- 单个 content item 的事件归并使用一个事务
- 单个 event score snapshot 写入使用一个事务
- 单次二次归并使用一个事务
- migration 每个文件一个事务

不建议把整轮 `FetchJob` 包在一个大事务中。单源失败或单条归并失败不应回滚整轮已成功数据。

### 18.3 失败降级

| 失败点 | 行为 |
| --- | --- |
| 单个 source 抓取失败 | 写 `fetch_run_source`，其他 source 继续 |
| WebExtract 失败 | 标记富化失败，使用标题和 hover 继续，并调用 `ITagService` 生成 fallback tags |
| WebExtract 成功但未返回 tags | 保留富化结果，调用 `ITagService` 补充 fallback tags |
| Cluster LLM 失败 | 偏保守创建新事件或跳过归并 |
| Judge LLM 失败 | `llm_boost_score = 0` |
| Embedding 失败 | 不写向量，规则召回继续 |
| pgvector 查询失败 | 记录 warning，规则召回继续 |
| 推送失败 | 写失败 `push_log`，不重复无限重试 |
| 报告生成失败 | 摘要推送可继续，记录 stage error |

## 19. 测试策略

### 19.1 单元测试

继续覆盖：

- WebExtract 富化判定
- 规则候选召回
- EventMatcher 阈值和复活逻辑
- EventScoringService
- 黑名单
- 重复推送
- DigestJob 幂等

新增覆盖：

- source capability 判断
- ranked 和 flash scoring 分项
- LLM usage 成本计算
- WebExtract 顶层 `insights` 字符串数组映射为 `EnrichmentResult.Tags` 并入库去重
- 未富化、富化跳过、富化失败时的 `ITagService` fallback tag
- vector recall 和规则召回合并
- secondary merge hard filters
- report read model 排序和过滤

### 19.2 PostgreSQL 集成测试

建议使用可选 integration profile，最终 CI 应覆盖主路径：

- migration 可重复执行
- `CREATE EXTENSION IF NOT EXISTS vector` 可执行
- content upsert 和 snapshot 写入
- event、event_item、score 写入
- push log dedup
- app state upsert
- fetch run stage/source telemetry
- llm usage 写入和按 run 查询
- report snapshot 写入

### 19.3 回归样本

扩展现有 regression corpus：

- ranked news merge
- ranked news no merge
- stale reactivation
- flash multi source merge
- flash repeated follow up
- topic noise no merge
- blacklist
- push dedup
- vector recall improvement
- secondary merge
- tag generation

## 20. Rollout 与实施顺序

建议顺序：

1. V2 foundation：配置、CLI、PostgreSQL 连接、migration runner，移除 data-view。
2. PostgreSQL persistence：核心表和 Dapper 仓储替换 LiteDB。
3. Observability：`fetch_run_source`、`fetch_run_stage`、`llm_usage`。
4. Source abstraction：source registry、DailyHotApi、flash feed。
5. Tag 和静态报告。
6. pgvector recall。
7. secondary merge。
8. 可选 Grafana 或 Dashboard。

每一步都应保持 `validate`、`fetch-once`、`digest-once` 可运行，不引入 LiteDB fallback。

## 21. 风险与开放问题

### 21.1 风险

1. PostgreSQL schema 一次设计过大，拖慢主链路迁移。
2. source 抽象过度，导致 NewsNow 已有路径回退。
3. ranked 和 flash 信号混合不当，造成误推送。
4. LLM usage 记录过细，影响 adapter 简洁性。
5. embedding 候选过多，导致 Cluster LLM 成本上升。
6. 二次归并误合并重要事件。
7. 静态报告需求膨胀成动态 Web 项目。

### 21.2 缓解策略

1. V2M1 只迁核心主链路表，tag、embedding、merge 可通过后续 migration 增加。
2. Source capability 先只实现 `ranked_news` 和 `flash_feed`，topic 先入库展示。
3. `event_score_snapshot` 中保留 `rank_score` 和 `flash_score`，便于调参。
4. LLM usage 通过统一 wrapper 记录，避免每个 client 复制逻辑。
5. vector recall 设置 candidate limit，并与规则召回合并去重。
6. 二次归并保留 lineage，不做硬删除。
7. 报告只生成静态 HTML，动态 Dashboard 后置。

### 21.3 开放问题

1. 本地 PostgreSQL 是否提供 Docker Compose 模板。
2. migration runner 自研到什么程度，是否后续换成成熟工具。
3. DailyHotApi 使用公共实例还是自部署实例。
4. flash source 默认时间窗口和半衰期。
5. Source weight 是否在 V2M3 就参与评分。
6. tag taxonomy 是否需要种子配置。
7. embedding 模型和维度最终选择。
8. 静态报告的访问方式是本地文件、Nginx 静态目录，还是对象存储。

## 22. 结论

V2 的技术路线是以 PostgreSQL 为第一阶段基础设施，直接在关系型 schema 中承载 source、tag、LLM usage、report、embedding 和 merge history。这样可以避免在 LiteDB 上重复实现新模型，也为 pgvector、可观测性、静态报告和后续 Grafana 查询打好基础。

实现时不应重写已有业务资产。`FetchJob`、`DigestJob`、调度器、Core 事件规则、WebExtract、Cluster/Judge LLM 和 Unipush 都应在新持久化层上逐步迁移和扩展。V2 的关键不是做更多界面，而是让数据平台、来源抽象、召回质量和输出可读性进入可持续演进状态。
