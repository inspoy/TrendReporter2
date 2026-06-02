# TrendReporter2 V2 里程碑与任务清单

## 1. 目标

本文档基于 [v2-design.md](v2-design.md) 和 [v2-technical-design.md](v2-technical-design.md)，将 V2 开发拆分为可执行的里程碑和任务清单。

拆分原则：

- PostgreSQL 先行，不做 LiteDB 迁移、双写或兼容 provider
- 先保持 V1 主链路在新数据库上跑通，再扩展 source、report、vector 和二次归并
- 每个里程碑都应产出可验证结果
- 每个任务都尽量对应明确代码模块
- Dashboard、Grafana、tag 订阅推送后置，不作为 V2 早期要求

## 2. 总体节奏

建议将 V2 拆为 9 个里程碑：

| 进度 | 里程碑 | 名称 | 目标 |
| --- | --- | --- | --- |
| 已完成 | `V2M0` | V2 基础准备 | 配置、CLI、PostgreSQL 连接、migration runner、移除 data-view |
| 已完成 | `V2M1` | PostgreSQL 持久化主链路 | 用 Dapper 仓储替换 LiteDB，打通 fetch、match、score、push、digest |
| 已完成 | `V2M2` | 可观测性与 LLM usage | 补齐 run/source/stage telemetry 和 LLM token、成本、错误记录 |
| 已完成 | `V2M3` | Source 抽象与 DailyHotApi/flash | 建立 source registry，支持 ranked news 和 flash feed |
| 已完成 | `V2M4` | Tag 与静态报告 | 实现 tag/event_tag，生成静态 HTML 摘要报告 |
| 已完成 | `V2M5` | pgvector 候选召回 | 接入 embedding 表、向量索引和 vector recall，规则召回保底 |
| 已完成 | `V2M6` | 二次归并 | 合并拆分过细的事件，保留 lineage 和 evidence |
| 已完成 | `V2M7` | Dashboard/Grafana 可选增强 | 基于 PostgreSQL 提供运行健康和历史查询视图 |
| 已完成 | `V2M8` | 稳定化、文档与回归 | 补齐测试、运行说明、回归样本和调参记录 |

## 3. 里程碑详情

### 3.1 `V2M0` V2 基础准备

### 目标

建立 V2 的基础工程入口：PostgreSQL 配置、`NpgsqlDataSource`、SQL migration runner、CLI 调整和基础验证。此阶段不迁移历史 LiteDB 数据。

### 交付物

- `database.provider = postgres` 和 `database.connectionString` 配置模型
- PostgreSQL 连接注册
- SQL migration runner
- 第一版 schema migration 框架
- `Program.cs` 移除 `data-view` 命令
- V2 配置校验规则

### 任务清单

#### `V2M0-T1` 更新配置模型

- 修改 `src/TrendReporter2.Core/Configuration/AppConfig.cs`
- 将 `DatabaseConfig` 调整为：
  - `Provider`
  - `ConnectionString`
  - `MigrateOnStartup`
- 移除或停止使用 `database.path`
- 保持 YAML camelCase 绑定

#### `V2M0-T2` 更新配置校验

- 修改 `AppConfigValidator`
- 要求 `database.provider` 必须等于 `postgres`
- 要求 `database.connectionString` 非空
- 保留 `analysis.fetchInterval`、`pushTime`、`system.timeZone` 等现有校验
- 明确拒绝 `litedb` provider

#### `V2M0-T3` 引入 PostgreSQL 依赖

- 在 Infrastructure 引入 `Npgsql`
- 引入 `Dapper`
- 不引入 EF Core 作为主路径
- 确认 `NuGet.Config` 仍只使用 nuget.org

#### `V2M0-T4` 注册 `NpgsqlDataSource`

- 在 `DependencyInjection.cs` 中注册 app-wide `NpgsqlDataSource`
- 所有 PostgreSQL 仓储都从 data source 打开连接
- 不让仓储直接读取 connection string

#### `V2M0-T5` 实现 SQL migration runner

- 创建 `SqlMigrationRunner`
- 创建 `schema_migration` 表
- 按文件名顺序执行 SQL
- 记录 version、name、checksum、applied_at
- 已执行 migration checksum 不一致时失败

#### `V2M0-T6` 创建初始 migration 目录

- 在 Infrastructure 添加 `Persistence/Migrations`
- 创建 `0001_init.sql`
- 在 migration 中执行 `CREATE EXTENSION IF NOT EXISTS vector`
- 提前验证目标 PostgreSQL 环境支持 pgvector，避免到 V2M5 才暴露扩展不可用
- 先创建 `schema_migration` 之外的最小核心表占位或完整 V2M1 schema

#### `V2M0-T7` 调整 `Program.cs` CLI

- 保留：
  - `validate`
  - `fetch-once`
  - `digest-once`
  - 后台模式
- 移除：
  - `data-view`
- 未知命令输出中文帮助和错误

#### `V2M0-T8` 移除 V2 的 data-view 注册

- 停止注册 `DataViewReader`
- 停止从 CLI 访问 LiteDB collection 名称
- 保留文件删除可在后续清理任务中完成，但 V2 命令不再暴露

#### `V2M0-T9` 更新配置示例

- 将 V2 使用的示例配置改为 `database.provider` 和 `database.connectionString`
- 不在本任务中提交真实 connection string
- 不把 `config.yaml` 加入版本控制

### 验收标准

- `validate --config config.example.yaml` 可校验 PostgreSQL 配置形态
- provider 不是 `postgres` 时明确报错
- migration runner 可创建 `schema_migration`
- `data-view` 命令不再出现在 V2 CLI 帮助中
- 代码中没有新的 LiteDB provider 或双写路径

### 3.2 `V2M1` PostgreSQL 持久化主链路

### 目标

用 PostgreSQL/Dapper 仓储替换 LiteDB 仓储，使 V1 主链路在新数据库上跑通：抓取、入库、富化、归并、评分、推送和摘要。

### 交付物

- 核心 PostgreSQL 表
- Dapper 仓储实现
- `fetch-once` 写入 PostgreSQL
- `digest-once` 使用 PostgreSQL 读取候选并保持幂等
- LiteDB 初始化和仓储不再注册到主路径

### 任务清单

#### `V2M1-T1` 设计核心表 migration

- 在 `0001_init.sql` 中创建：
  - `source`
  - `content_item`
  - `content_snapshot`
  - `event`
  - `event_item`
  - `event_score_snapshot`
  - `push_log`
  - `fetch_run`
  - `app_state`
- 添加必要主键、外键、unique 约束和索引
- 表名沿用 V1 collection 名称

#### `V2M1-T2` 实现 `PostgresContentRepository`

- 替换 content item upsert
- 写入 content snapshot
- 支持按富化状态查询内容
- 支持更新 summary、summary source、enrichment status
- 使用 Dapper 显式 SQL

#### `V2M1-T3` 实现 `PostgresEventRepository`

- 新建和更新 event
- 查询 active 和 stale 候选事件
- 写入 event item
- 写入 event score snapshot
- 查询摘要候选
- 写入 push log
- 保证 dedup key unique 冲突可被识别

#### `V2M1-T4` 实现 `PostgresAppStateRepository`

- 支持 get by key
- 支持 upsert key/value
- 用于 `DigestJob` 摘要幂等

#### `V2M1-T5` 实现 `PostgresFetchRunRepository`

- 创建 running fetch run
- 完成时更新状态和统计
- 记录 started_at、finished_at、source count、item count、error summary
- 保持 V1 `FetchJob` 统计语义

#### `V2M1-T6` 替换 DI 注册

- 在 `AddTrendReporterInfrastructure` 中注册 PostgreSQL 仓储
- 移除 LiteDB initializer 主路径注册
- 不保留 `ILiteDbConnectionFactory` 作为 V2 默认依赖

#### `V2M1-T7` 适配 `FetchJob`

- 保持 fetch -> ingest -> enrich -> match -> score/push 流程
- 写入 PostgreSQL content 和 snapshot
- 事件归并和评分从 PostgreSQL 读取数据
- 单 source 失败不阻塞整轮

#### `V2M1-T8` 适配 `DigestJob`

- 从 PostgreSQL 查询 digest candidates
- 使用 `app_state` 控制同一摘要时刻幂等
- 使用 `push_log.dedup_key` 防重复推送
- 保持现有 `pushTime` 时区语义

#### `V2M1-T9` 移植持久化测试

- 将 LiteDB repository 测试改为 PostgreSQL integration 测试
- 覆盖 upsert、snapshot、event item unique、push log dedup、app state upsert
- 如果 CI 暂无 PostgreSQL，先用 integration profile 标记，但主路径测试必须存在

### 验收标准

- `fetch-once` 可把 content、snapshot、event、score、push log 写入 PostgreSQL
- `digest-once` 可从 PostgreSQL 查询候选并写入 `app_state` 和 `push_log`
- 重复抓取不会重复创建相同 content item
- 重复摘要不会重复发送同一时段
- 主路径不依赖 LiteDB 文件

### 3.3 `V2M2` 可观测性与 LLM usage

### 目标

补齐每轮运行的可观测性，记录 run/source/stage 统计，以及 cluster、judge、tagging、embedding 的 LLM token、成本、耗时、重试和错误。

### 交付物

- `fetch_run_source`
- `fetch_run_stage`
- `llm_usage`
- LLM usage recorder
- FetchJob 完成时的 LLM 成本汇总日志

### 任务清单

#### `V2M2-T1` 添加 observability migration

- 创建 `fetch_run_source`
- 创建 `fetch_run_stage`
- 创建 `llm_usage`
- 添加 run_id、stage、model、created_at 等索引

#### `V2M2-T2` 定义 telemetry 契约

- 在 Core 新增 `IRunTelemetryRecorder`
- 定义记录 source 结果的方法
- 定义记录 stage 开始和结束的方法
- 定义记录 LLM usage 的方法

#### `V2M2-T3` 实现 PostgreSQL telemetry recorder

- 用 Dapper 写入 `fetch_run_source`
- 用 Dapper 写入 `fetch_run_stage`
- 用 Dapper 写入 `llm_usage`
- 支持同一 run 下按 stage 查询汇总

#### `V2M2-T4` 在 `FetchJob` 中记录 stage

- 记录 `fetch`
- 记录 `ingest`
- 记录 `enrich`
- 记录 `match`
- 记录 `score`
- 记录 `push`
- 后续 report 阶段接入时记录 `report`

#### `V2M2-T5` 在 source 抓取中记录 per source 结果

- 成功 source 记录 item_count 和 duration_ms
- 失败 source 记录 error
- skipped source 记录原因

#### `V2M2-T6` 包装 LLM client usage

- Cluster LLM 调用记录 `stage = cluster`
- Judge LLM 调用记录 `stage = judge`
- Tagging LLM 调用记录 `stage = tagging`
- 后续 embedding 使用同一 LLM usage recorder
- token 缺失时不伪造 usage

#### `V2M2-T7` 实现成本估算

- 读取 `llm.*.pricing`
- 按每百万 token 计算成本
- 记录 input、output、cache read token
- `fetch_run.estimated_llm_cost` 汇总本轮成本

#### `V2M2-T8` 固定 LLM 重试策略

- 在 LLM adapter 中固定最多 3 次重试
- 记录 retry_count
- 记录最终错误
- 失败后沿用现有降级策略

### 验收标准

- 每轮 fetch 都有 source 和 stage 记录
- 每次 LLM 调用都能在 `llm_usage` 中追踪成功或失败
- FetchJob 结束日志包含本轮 LLM 调用次数和估算成本
- LLM 失败不会让整轮 fetch 直接失败

### 3.4 `V2M3` Source 抽象与 DailyHotApi/flash

### 目标

从只围绕 NewsNow 榜单的模型，演进到 source registry 和 capability 模型，支持 ranked news、flash feed 和 topic 的初始入库语义。

### 交付物

- `SourceDefinition`
- `ISourceRegistry`
- `IContentSourceClient`
- `NewsNowClient` 适配新接口
- `DailyHotApiClient`
- flash source 入库和评分信号

### 任务清单

#### `V2M3-T1` 定义 source 领域模型

- 新增 `SourceDefinition`
- 新增 `ContentKind` 常量或枚举
- 字段包含 provider、external_id、category、display_name、content_kind、enabled、weight

#### `V2M3-T2` 实现 `ISourceRegistry`

- 从配置读取 source 列表
- 可选将旧 `newsNow.sources` 映射为 ranked source
- 按 enabled 过滤
- 按 provider 分组

#### `V2M3-T3` 抽象 `IContentSourceClient`

- 定义 `Provider`
- 定义 `FetchAsync(SourceDefinition, CancellationToken)`
- 返回统一 `FetchedContentItem`

#### `V2M3-T4` 改造 `NewsNowClient`

- 使用 `SourceDefinition.ExternalId` 请求 `GET /api/s?id=source`
- 返回 `ranked_news`
- 写入 `rank`、`source_list_size`、`normalized_rank_score`
- 保留 HoverText 和 raw payload

#### `V2M3-T5` 实现 `DailyHotApiClient`

- 支持配置 `baseUrl`
- 支持 ranked endpoint
- 支持 flash endpoint
- 将返回项映射为 `FetchedContentItem`
- 单 endpoint 失败不阻塞其他 source

#### `V2M3-T6` 同步 source 表

- 启动或 fetch 前将配置 source upsert 到 `source` 表
- 禁用的 source 保留记录但不抓取
- 保证 `(provider, external_id, content_kind)` 唯一

#### `V2M3-T7` 支持 flash snapshot

- flash source 的 `rank` 为空
- 不写虚假 `normalized_rank_score`
- 计算 `freshness_score`
- 用 published_at 和 captured_at 支持新鲜度判断

#### `V2M3-T8` 调整评分服务

- 在 `EventScoringService` 中分开计算 ranked signal 和 flash signal
- 增加 trigger reasons：
  - `flash_multi_source`
  - `flash_repeated`
  - `flash_follow_up`
  - `flash_trusted_source`
- 写入 `rank_score` 和 `flash_score`

#### `V2M3-T9` 增加 source capability 测试

- ranked source 仍按排名评分
- flash source 不依赖 rank
- topic source 初期只入库或展示，不进入强推送策略

### 验收标准

- NewsNow ranked source 行为不回退
- DailyHotApi ranked source 可入库并参与评分
- flash source 无 rank 时仍可参与事件发现
- ranked signal 和 flash signal 在 score snapshot 中分开保存
- topic 不成为 V2 早期强推送要求

### 3.5 `V2M4` Tag 与静态报告

### 目标

实现 tag/event_tag 的初始能力，并从 read model 生成静态 HTML 报告。tag 优先来自成功 WebExtract 富化返回的 `EnrichmentResult.Tags`；缺少 WebExtract tags 的内容可由 `llm.tagging` 补充。tag 初期只用于展示和搜索，不驱动 push subscription。

### 交付物

- `tag`
- `event_tag`
- `content_item_tag`
- WebExtract tags 接入
- WebExtract tags 优先、`llm.tagging` 缺失补全规则
- report read model
- 静态 HTML 报告文件
- `report_snapshot`

### 任务清单

#### `V2M4-T1` 添加 tag 和 report migration

- 创建 `tag`
- 创建 `event_tag`
- 创建 `content_item_tag`
- 创建 `report_snapshot`
- 添加 tag name unique 约束

#### `V2M4-T2` 定义 tag 领域模型

- 新增 `Tag`
- 新增 `EventTag`
- 新增 `ContentItemTag`
- 新增 `TagCategory`
- 新增 `TagSource`

#### `V2M4-T3` 扩展 WebExtract 富化结果

- 将 `EnrichmentResult` 增加 `Tags` 数组
- `Tags` 从 WebExtract JSON 顶层 `insights` 字段读取，类型为字符串数组，层级与 `title`、`summary` 同级
- `WebExtractClient` 成功富化新闻内容时返回若干 tags
- 将 WebExtract tags 标记为 `TagSource = web_extract`
- WebExtract 未返回 tags 时不视为富化失败，但需要 fallback 补全

#### `V2M4-T4` 实现 WebExtract tags 规范化

- WebExtract 成功返回 tags 时直接使用这些 tags
- 将 WebExtract tags 规范化为稳定名、展示名、分类、来源和置信度
- 控制 tag 数量，避免低质量 tag 膨胀
- 不再从 source category、标题、hover text、summary 或事件关键词生成规则 fallback tags

#### `V2M4-T5` 接入 LLM tagging 缺失补全

- 仅对未富化、富化跳过、富化失败或 WebExtract 未返回 tags 的内容调用 `llm.tagging`
- 记录 `llm_usage.stage = tagging`
- 返回 tag 名、分类、置信度
- LLM 失败时保留 WebExtract tags 或保持无标签

#### `V2M4-T6` 实现 tag 仓储

- upsert tag
- upsert content_item_tag
- upsert event_tag
- 查询事件 tag 列表
- 支持按 tag 查询事件，为后续搜索预留

#### `V2M4-T7` 定义 report read model

- 新增 `ReportEventItem`
- 新增 `ReportContentItem`
- 新增 `ReportPayload`
- 包含事件标题、摘要、阶段、score、heat、tag 和新闻链接

#### `V2M4-T8` 实现 `IReportReadModelQuery`

- 查询摘要窗口内的高价值事件
- 排除黑名单事件
- 排除 `status = merged` 的 source event
- 读取 event_tag 和相关新闻

#### `V2M4-T9` 实现 `StaticHtmlReportRenderer`

- 生成单个 HTML 文件
- 使用简单内联样式或静态模板
- 不引入前端构建系统
- 文件写入 `report.outputDirectory`

#### `V2M4-T10` 集成 tag 写入到 fetch/enrich 流程

- 富化成功时直接持久化 `EnrichmentResult.Tags`
- 未富化、富化跳过、富化失败或未返回 tags 时在 tagging 阶段调用 `llm.tagging`
- 写入 `content_item_tag`
- 事件归并后汇总并写入 `event_tag`

#### `V2M4-T11` 集成报告到 `DigestJob`

- 摘要生成时创建报告
- 写入 `report_snapshot`
- 可选把报告路径或 URL 放入摘要推送

#### `V2M4-T12` 增加 tag 和报告测试

- WebExtract tags 可写入并展示
- WebExtract 顶层 `insights` 字符串数组可映射为 `EnrichmentResult.Tags`
- 未富化、富化跳过和富化失败内容在缺少 WebExtract tags 时会进入 `llm.tagging` 补全
- WebExtract 成功但未返回 tags 时会进入 `llm.tagging` 补全；未配置或失败时保持无标签
- read model 排序正确
- 黑名单不出现在报告中
- tag 展示正确
- 原文链接输出正确

### 验收标准

- 每个高价值事件可以关联 tag
- 成功富化的内容优先使用 WebExtract 返回的 tags
- 缺少 WebExtract tags 的内容会在 tagging 阶段尝试通过 LLM 获得标签；未配置或失败时保持无标签
- 摘要报告可生成并用浏览器打开
- 报告包含事件、评分、tag、相关新闻和原文链接
- tag 不影响 V2 初期即时推送资格
- 没有动态 Dashboard、登录或多用户要求

### 3.6 `V2M5` pgvector 候选召回

### 目标

引入 embedding 表和 pgvector 查询，用向量召回增强候选事件发现。规则召回仍是 fallback，最终归并仍由规则和 Cluster LLM 判定。

### 交付物

- `content_embedding`
- `event_embedding`
- pgvector HNSW cosine index
- `EmbeddingClient`
- `VectorEventCandidateService`
- 规则召回和向量召回合并逻辑

### 任务清单

#### `V2M5-T1` 添加 embedding migration

- 创建 `content_embedding`
- 创建 `event_embedding`
- 字段包含 model、version、dimensions、source_text_hash、embedding
- 在 dimensions 固定后创建 HNSW cosine index

#### `V2M5-T2` 配置 embedding 模型

- 增加 `llm.embedding`
- 校验 model 和 dimensions
- pricing 用于成本估算

#### `V2M5-T3` 实现 `EmbeddingClient`

- 调用 OpenAI compatible embedding API
- 返回固定维度向量
- 记录 `llm_usage.stage = embedding`
- 失败时不阻塞 fetch 主流程

#### `V2M5-T4` 实现 embedding 仓储

- upsert content embedding
- upsert event embedding
- 根据 source text hash 判断是否需要重算
- 查询相似 event embedding

#### `V2M5-T5` 生成 content embedding

- 使用 title、hover_text、summary 组成文本
- 对新增或摘要变化的 content 生成向量
- 控制单轮 embedding 预算

#### `V2M5-T6` 生成 event embedding

- 使用 canonical title、summary、代表标题、tag 组成文本
- 事件更新后按 hash 判断是否重算
- 避免同一事件每轮无意义重算

#### `V2M5-T7` 实现 vector recall

- 根据 content embedding 查询相似 event
- 返回 event id、similarity、reason
- 设置 candidate limit
- 查询失败时降级为规则召回

#### `V2M5-T8` 合并召回结果

- 新增 `CompositeEventCandidateService`
- 合并 rule candidates 和 vector candidates
- 去重、排序、硬过滤
- 限制传给 Cluster LLM 的候选数量

#### `V2M5-T9` 增加召回回归测试

- 对比规则召回和 vector recall 命中情况
- 验证 vector failure fallback
- 验证候选数量不会爆炸

### 验收标准

- embedding 表可写入固定维度向量
- pgvector HNSW cosine index 可创建
- vector recall 可以补充规则召回漏掉的候选
- vector 查询失败时 fetch 主流程继续运行
- Cluster LLM 调用量不会因为候选增加而失控

### 3.7 `V2M6` 二次归并

### 目标

在 pgvector 召回稳定后，实现二次归并任务，修复在线归并偏保守导致的事件拆分，同时保留 lineage 和 evidence。

### 交付物

- `event_merge_history`
- `SecondaryMergeService`
- 相似事件对发现
- 二次归并硬过滤
- merge 后 evidence 和 score 更新

### 任务清单

#### `V2M6-T1` 添加 merge history migration

- 创建 `0008_secondary_merge.sql` migration 文件
- 新增 `event_merge_history` 表，字段为 `id`(uuid pk)、`source_event_id`(uuid)、`target_event_id`(uuid)、`confidence`(double precision)、`reason`(text)、`decided_by`(text)、`evidence_snapshot`(jsonb)、`created_at`(timestamptz)
- 在 `event_merge_history` 添加 check 约束 `source_event_id <> target_event_id`，unique 约束 `(source_event_id, target_event_id)`
- 在 `event` 表增加 `merged_into_event_id` 字段（uuid，可为空），加 check `merged_into_event_id <> id`
- 在 `event` 表增加 `status` check 约束，新增 `merged` 枚举值：`status in ('Active', 'Stale', 'Merged')`
- 在 `event_item` 表增加 `is_active` 字段（boolean，默认 true），加 index `(event_id, is_active)`
- 在 `event_item` 表增加 `created_by_merge_id` 字段（uuid，可为空），记录该 evidence 是否由二次归并迁移而来
- 在 `event` 表增加 index `(status)` 和 `(merged_into_event_id)`
- 在 `event_merge_history` 表增加 index `(source_event_id)` 和 `(target_event_id)`
- 对现有数据回填 `event_item.is_active = true`

#### `V2M6-T2` 定义二次归并领域模型和契约

- 在 Core 新增 `IEventMergeRepository` 接口，定义写入 `EventMergeHistory`、查询已处理合并对、按事件 id 迁移 evidence 等方法
- 在 Core 新增 `ISecondaryMergeService` 接口，定义 `MergeRunAsync(runId, now, ct)` 返回二次归并结果统计
- 在 Core 新增 `EventMergeCandidate` 模型，包含 source event、target event、similarity score、matched reasons
- 在 Core 新增 `EventMergeDecision` 模型，包含 decision（`same_event`、`related_but_distinct`、`unrelated`）、confidence、reason
- 在 Core 新增 `EventMergeHistory` 模型，字段对齐 migration 中的表结构，包含 evidence_snapshot 的 JSON 序列化
- 在 Core 新增 `SecondaryMergeRunResult` record，记录候选对数、硬过滤排除数、LLM 判定数、实际合并数
- 在 `EventAggregate` 增加 `MergedIntoEventId` 属性（可为空），与 migration 中 `event.merged_into_event_id` 对齐
- 在 `EventStatus` 常量类增加 `public const string Merged = "Merged";`
- 在 `EventItem` 增加 `IsActive` 和 `CreatedByMergeId` 属性
- 在 `IEventRepository` 增加二次归并所需方法：加载待归并候选事件、批量更新 event item is_active、批量迁移 event item 到 target event、批量更新 event merged 状态

#### `V2M6-T3` 发现相似事件对

- 实现 `SecondaryMergeService`，在 Core 中承载二次归并主流程
- 从 `IEventRepository` 加载 active 和近期 stale 事件作为候选池
- 排除 `status = 'Merged'` 的事件和 `is_blacklisted = true` 的事件（黑名单不决定合并，但排除以减少候选噪音）
- 对候选池中的每个事件，通过 event embedding 查询余弦相似事件列表
- embedding 不可用或不返回结果时跳过该事件，不阻塞其他事件的查询
- 对每个相似事件对，检查是否已在 `event_merge_history` 中处理过（任一方向 `source_event_id -> target_event_id`），已处理的对直接排除
- 对相似度低于配置阈值（如 `analysis.event.mergeSimilarityThreshold`，默认 0.7）的对直接排除
- 去重：若 A-B 和 B-A 都出现，只保留一个方向（选择相似度更高的方向，或按 event id 排序取第一个）
- 返回 `EventMergeCandidate` 列表，按 similarity 降序排列，受 `analysis.event.mergeCandidateLimit` 限制

#### `V2M6-T4` 实现硬过滤

- 在 `SecondaryMergeService` 中实现 `ApplyHardFilters(EventMergeCandidate)` 方法
- 核心实体冲突过滤：提取 source 和 target event 的 `Entities` 列表，若存在明显互斥的实体（如人名、机构名）且无交集，则拒绝合并
- 时间冲突过滤：若两个事件的 `first_seen_at` 和 `last_seen_at` 时间窗完全不重叠，且 `event_type` 不同，则拒绝合并
- 地点冲突过滤：提取 `Places` 列表，若地点明确不同（如不同国家/城市）且无交集，则拒绝合并
- 事件类型不兼容过滤：若 source 是 `NewsEvent` 而 target 是 `Topic`（或反之），拒绝合并
- 关键数字冲突过滤：若 `KeyTerms` 中存在明显冲突的数字（如死亡人数、金额），拒绝合并
- 硬过滤拒绝的事件记录原因到日志（中文），不写入 merge history
- 硬过滤通过的事件对标记 `decided_by = 'rule'`，但将 `confidence` 设为低于 LLM 判定值（如 0.6），交由后续 LLM 判定确认

#### `V2M6-T5` 接入 LLM merge 判定

- 在 `IEventMergeRepository` 或 Core 中定义 `SecondaryMergeLlmRequest` 和 `SecondaryMergeLlmResponse` 模型
- 在 `ISecondaryMergeService` 中接入 `IClusterLlmClient`（复用现有 cluster LLM），对通过硬过滤的高相似候选进行判定
- 构建 LLM 请求上下文，包含 source event 和 target event 的 canonical_title、summary、representative titles、tags、entities、key terms、first_seen_at、last_seen_at、覆盖 source 数
- 要求 LLM 返回 decision（`same_event`、`related_but_distinct`、`unrelated`）、confidence（0到1）、reason（中文说明）
- 决策映射：`same_event` → 执行合并；`related_but_distinct` 和 `unrelated` → 不合并
- `same_event` 但 confidence 低于 `mergeLlmlConfidenceThreshold`（如 0.6）时，不执行合并，仅记录 reason 到日志
- 每次 LLM 调用写入 `llm_usage`，`stage = 'cluster'`，关联 source event id 和 target event id
- LLM 调用失败或未配置时，不执行该对合并，记录原因，不阻塞其他候选对
- 判定结果写入 `EventMergeDecision` 模型，包含完整 decision、confidence、reason

#### `V2M6-T6` 执行 merge

- `SecondaryMergeService` 收到 `SameEvent` 决策后，使用 `MergeEventsAsync(sourceEventId, targetEventId, decision, ct)` 方法
- 第一个事务中完成以下操作，任一步骤失败则回滚整个事务：
  - 写入 `event_merge_history`，包含 source_event_id、target_event_id、confidence、reason、decided_by（`llm`）、evidence_snapshot（序列化 source event 当前的 event_item 列表和 score 快照摘要为 jsonb）
  - 更新 source event：`status = EventStatus.Merged`，`merged_into_event_id = targetEventId`
  - 迁移 source event 的 active evidence（`is_active = true` 的 event_item）到 target event：在 target event 下创建新的 event_item 记录，`content_item_id`、`confidence`、`match_reason` 保持不变，`created_by_merge_id` 设为 `event_merge_history.id`，`is_active = true`
  - 将 source event 的原 event_item 标记为 `is_active = false`
  - 如果同一 content_item_id 在 target event 中已存在（unique 冲突），跳过该 item，只标记 source 侧 `is_active = false`
- 事务提交后更新 `SecondaryMergeRunResult` 中的合并统计数
- 不对原始 `content_item` 做任何删除或修改
- 导出 `event_merge_history.evidence_snapshot` 便于事后排查和可能的拆分

#### `V2M6-T7` 重算 target event

- 在 merge 事务完成后，对 target event 执行重算
- 更新 target event 的 `canonical_title`：若 source event 的标题更长或更有代表性，考虑替换；否则保留
- 更新 target event 的 `summary`：合并 source event 的 summary 信息，确保不丢失关键进展
- 合并 source event 的 `Aliases`、`Entities`、`Places`、`KeyTerms`、`RepresentativeTitles` 到 target event，去重并限制数量（沿用 `EventMatcher` 中的 AliasLimit、EntityLimit、KeyTermLimit 等常量）
- 更新 target event 的 `last_seen_at`：取 source 和 target 的较晚值
- 更新 target event 的 `first_seen_at`：取 source 和 target 的较早值
- 调用 `EmbeddingService` 重新生成 target event 的 event embedding（因为 summary、title、tags 可能变化），source_text_hash 变化后触发重算
- 合并 source event 和 target event 的 tags：汇总所有 event_tag，去重，取最高置信度
- 调用 `EventScoringService` 对 target event 重新评分，基于合并后的 evidence 重新计算 coverage、rank、flash、trend、persistence 等各项分数
- 写入新的 `event_score_snapshot`，并在新 snapshot 的 trigger_reasons 中增加 `secondary_merge`
- 确保 `DigestJob` 和静态报告查询中过滤 `status = 'Merged'` 的事件，避免 merged source event 在摘要和报告中重复展示

#### `V2M6-T8` 在 `FetchJob` 中集成二次归并

- 在 `FetchJob` 的主流程中，在线归并和评分完成后、推送之前插入二次归并阶段
- 记录 `fetch_run_stage`，`stage = 'secondary_merge'`
- 调用 `ISecondaryMergeService.MergeRunAsync(runId, now, ct)`
- 二次归并失败（embedding 不可用、LLM 失败等）不应阻塞整轮 fetch，记录 warning 日志和 stage error 后继续后续流程
- 将二次归并结果统计写入日志：候选对数、硬过滤排除数、LLM 判定数、实际合并数

#### `V2M6-T9` 实现 PostgreSQL merge 仓储

- 在 Infrastructure 新增 `PostgresEventMergeRepository`，实现 `IEventMergeRepository`
- 实现 `InsertMergeHistoryAsync`：Dapper 写入 `event_merge_history`
- 实现 `HasBeenProcessedAsync(sourceEventId, targetEventId)`：查询任一方向是否存在 merge history 记录
- 实现 `MigrateEventItemsAsync(sourceEventId, targetEventId, mergeHistoryId)`：在事务中迁移 active event_item，处理 unique 冲突
- 实现 `DeactivateEventItemsAsync(eventId)`：批量将 event_item 的 `is_active` 置为 false
- 在 `PostgresEventRepository` 中增加二次归并所需的新方法：
  - `LoadMergeCandidateEventsAsync(now, historyHours, staleDays)`：加载 active 和近期 stale 的非 merged 事件及其 event_item、快照等评分输入所需数据
  - `BatchUpdateEventItemActiveStateAsync`：批量更新 event_item 的 is_active 状态
  - `BatchMigrateEventItemsAsync`：批量迁移 event_item 到 target event
  - `BatchSetEventMergedStatusAsync`：批量更新 event 的 merged_into_event_id 和 status
- 在 `AddTrendReporterInfrastructure` 中注册 `PostgresEventMergeRepository` 和 `ISecondaryMergeService`

#### `V2M6-T10` 增加二次归并测试

- 单元测试 `SecondaryMergeService` 的硬过滤逻辑：验证核心实体冲突、时间不重叠、地点冲突、event type 不兼容能正确拒绝
- 单元测试 merge 后 target event 的 summary、tags、score 重算逻辑
- 回归样本增加：明显重复事件合并场景、明显不同事件不合并场景、merge history 保留原因和 evidence_snapshot、merged source event 不出现在摘要候选
- 集成测试 `PostgresEventMergeRepository` 的 merge history 写入、已处理对查询、event_item 迁移和去激活
- 验证原始 `content_item` 在整个 merge 流程后不丢失
- 验证 merge 事务失败时（如 unique 冲突）正确回滚，不产生半成品状态

### 验收标准

- 系统能通过 event embedding 发现相似的 active 和近期 stale 事件对
- 硬过滤能正确排除核心实体、时间、地点、关键数字或 event type 明显冲突的事件对
- LLM 判定为 `same_event` 的对可成功合并，写入 `event_merge_history` 并迁移 active evidence
- 误合并风险通过硬过滤和 LLM reason 可追踪；`evidence_snapshot` 保留合并前证据摘要便于事后排查
- source event 标记为 `status = 'Merged'`，`merged_into_event_id` 记录目标事件
- 原始 `content_item`、source event 和 event item lineage 不丢失
- target event 合并后 summary、tags、embedding 和 score 正确更新
- 摘要和报告不重复展示 `status = 'Merged'` 的 source event
- embedding 不可用或 LLM 失败时不阻塞整轮 fetch，日志有明确 warning
- merge 事务失败时正确回滚，不产生半成品状态

### 3.8 `V2M7` Dashboard/Grafana 可选增强

### 目标

在 V2 主链路稳定后，基于 PostgreSQL 现有 telemetry 数据（`fetch_run`、`fetch_run_source`、`fetch_run_stage`、`llm_usage`）以及事件/评分/推送/标签表，通过 SQL view 和可选 Grafana 提供运行健康与历史趋势查看能力。View 通过 migration runner 管理，不引入额外进程或状态表。

此里程碑不是 V2 早期要求，也不属于 V2 完成定义。

### 交付物

- `0009_monitoring_views.sql` migration 文件，在 `metrics` schema 下创建只读视图
- `docs/grafana.md` Grafana 接入与告警建议
- 不新增 `.cs` 文件、不引入动态 Web Dashboard

### 任务清单

#### `V2M7-T1` 创建监控视图 migration 与命名规范

- 创建 `src/TrendReporter2.Infrastructure/Persistence/Migrations/0009_monitoring_views.sql`
- 执行 `CREATE SCHEMA IF NOT EXISTS metrics` 并将所有视图放在该 schema 下
- 视图命名规范：`metrics.run_*`（运行健康）、`metrics.llm_*`（LLM 成本）、`metrics.event_*`（事件与内容）
- 每个视图创建前执行 `DROP VIEW IF EXISTS metrics.<name> CASCADE`，后续可直接 `CREATE OR REPLACE VIEW` 迭代
- 不修改任何业务表的写入路径

#### `V2M7-T2` 实现运行健康视图

基于 `fetch_run`、`fetch_run_source`、`fetch_run_stage` 创建以下视图：

- `metrics.run_success_rate`：按 `date(finished_at)` 聚合 run 总数、成功数、partial 数、失败数、成功率（`succeeded` + `partial` 视为可用），以及当日 `estimated_llm_cost` 汇总。窗口取最近 30 天 `finished_at`。
- `metrics.run_source_failure_rate`：按 `source_id` + `date(created_at)` 聚合每源抓取次数、成功次数、失败次数、失败率。关联 `source` 表获取 `provider`、`display_name`、`content_kind`。
- `metrics.run_stage_duration`：按 `stage` + `date(started_at)` 聚合 `avg(duration_ms)`、`percentile_cont(0.5)`（P50）、`percentile_cont(0.95)`（P95）、`count(*)`。窗口取最近 30 天，排除 `status = 'skipped'` 的记录。

#### `V2M7-T3` 实现 LLM 成本视图

基于 `llm_usage` 创建以下视图：

- `metrics.llm_daily_cost`：按 `stage` + `model` + `date(created_at)` 聚合 `sum(input_tokens)`、`sum(output_tokens)`、`sum(cache_read_tokens)`、`sum(estimated_cost)`、调用次数 `count(*)`、`avg(duration_ms)`。
- `metrics.llm_cost_trend_7d`：按 `date(created_at)` 聚合最近 7 天的每日总 `estimated_cost` 和总调用次数，用于成本趋势折线图。
- `metrics.llm_stage_cost_pct`：按 `stage` 聚合全部历史 `estimated_cost` 的累计值和占比（`cluster`、`judge`、`tagging`、`embedding`），用于饼图或堆叠柱状图。

#### `V2M7-T4` 实现事件与内容视图

基于 `event`、`event_score_snapshot`、`push_log`、`tag`、`event_tag` 创建以下视图：

- `metrics.event_daily_counts`：按 `date(first_seen_at)` 聚合每日新建事件数；按 `date(last_pushed_at)` 聚合每日推送事件数（去重 `event_id`）；按 `date(pushed_at)` 从 `push_log` 聚合每日推送次数（`push_type = 'Instant'` vs `'Digest'`）。排除 `event.status = 'Merged'` 的事件。
- `metrics.event_score_distribution`：找到最近一个 `fetch_run` 的 `event_score_snapshot` 记录，按分数分段统计事件数：`0-30`、`30-60`、`60-80`、`80-100`（`total_score` 字段）。排除 `event.is_blacklisted = true` 和 `event.status = 'Merged'` 的事件。
- `metrics.event_tag_distribution`：按 `tag.category` + `tag.name` 聚合关联事件数（去重 `event_tag.event_id`），排除 `event.status = 'Merged'` 的事件。用于 tag 云或条形图展示。

#### `V2M7-T5` 实现综合看板视图

- `metrics.latest_run_summary`：查询最新一条 `fetch_run`（按 `started_at desc`），返回 `status`、`source_count`、`success_source_count`、`failure_source_count`、`fetched_item_count`、`matched_event_count`、`pushed_event_count`、`estimated_llm_cost`、`extract(epoch from (finished_at - started_at)) / 60` 作为 `duration_minutes`、`started_at`、`finished_at`。关联 `fetch_run_stage` 获取各 stage 的 `duration_ms` 并列展示（如 `fetch_duration_ms`、`match_duration_ms` 等）。
- `metrics.health_snapshot_7d`：滚动 7 天窗口聚合：运行成功率（`succeeded + partial` / 总数）、日均 LLM 成本、日均事件数、日均推送数、平均 `fetch_run` 总耗时（分钟）。
- 这两个视图面向单行"一切正常"型仪表板，方便 Grafana stat/table 面板直接消费。

#### `V2M7-T6` 编写 Grafana 接入文档

在 `docs/grafana.md` 中输出：

- **数据源配置**：PostgreSQL 连接串示例、`metrics` schema 设为默认 search path
- **推荐面板**（每个面板附 SQL 查询和 Grafana 面板类型）：
  - Run Success Rate — 时间序列（`metrics.run_success_rate`），阈值线 80%，红/绿着色
  - Per-Source Failure Rate — 按 source 分面的时间序列（`metrics.run_source_failure_rate`）
  - LLM Daily Cost — 折线图（`metrics.llm_cost_trend_7d`）
  - LLM Cost by Stage — 堆叠柱状图或饼图（`metrics.llm_stage_cost_pct`）
  - Stage Duration P50/P95 — 按 stage 分面的时间序列（`metrics.run_stage_duration`）
  - Event Score Distribution — 仪表盘或柱状图（`metrics.event_score_distribution`）
  - Daily Events / Pushes — 双轴时间序列（`metrics.event_daily_counts`）
  - Latest Run Summary — 表格/stat 面板（`metrics.latest_run_summary`）
- **告警建议**：
  - 连续 3 次 `fetch_run` 成功率 <80% 时告警
  - 每日 LLM 成本超过前 7 天平均值的 2 倍时告警
  - 单 source 连续 3 次抓取全部失败时告警

### 验收标准

- `0009_monitoring_views.sql` 可通过 migration runner 执行且幂等（重复执行不报错）
- 所有 `metrics.*` 视图在空数据库（无 run 数据）上查询不报错，返回空结果或合理默认值
- 至少 `metrics.latest_run_summary` 和 `metrics.run_success_rate` 在有数据时可返回正确结果
- `docs/grafana.md` 包含数据源配置、面板 SQL 和告警阈值建议
- 不新增 `.cs` 文件、不引入动态 Web Dashboard、不引入额外状态表
- Dashboard 仍是可选增强，不影响 V2 主链路完成定义

### 3.9 `V2M8` 稳定化、文档与回归

### 目标

在 V2M0 到 V2M7 的主要能力已经落地后，集中完成稳定化收口：明确测试分层，补齐核心单元测试和 PostgreSQL 集成测试，建立 V2 regression corpus，更新运行、配置、测试文档，并沉淀调参记录与已知限制。

此里程碑不新增业务能力。它的目标是让现有 V2 主链路可重复验证、可离线回归、可在新环境按文档部署，并让后续维护者能快速判断一次改动是否破坏了 PostgreSQL 主路径、LLM 降级、向量召回、二次归并、摘要或报告过滤语义。

### 交付物

- 测试分层和运行约定，区分离线单元测试、需要 PostgreSQL 的集成测试和 corpus 回归测试
- 覆盖 source、scoring、tag、report、vector recall、secondary merge、digest filtering、fallback 行为的核心单元测试
- 可通过环境变量启用的 PostgreSQL 集成测试，覆盖 migration runner 和关键仓储语义
- V2 regression corpus 及样本格式说明，样本不依赖真实 NewsNow、DailyHotApi、WebExtract 或外部 LLM
- `docs/running.md`、`docs/testing.md`、配置说明和 `config.example.yaml` 的一致性检查结果
- 调参记录、默认阈值说明、已知限制和排错入口

### 非目标

- 不新增业务功能，不改变事件发现、评分、归并、推送、摘要或报告的产品语义
- 不新增前端、动态 Dashboard、自建 Web 应用、登录、多用户或权限系统
- 不做 LiteDB 历史迁移、LiteDB/PostgreSQL 双写或 provider 兼容层
- CI 和 regression tests 不要求真实外部 LLM/API，不要求真实 NewsNow、DailyHotApi、WebExtract，也不要求任何密钥
- Grafana 不是必选部署项；V2M7 的视图和 Grafana 文档可被测试或说明引用，但 V2M8 不把 Grafana 运行作为完成条件

### 任务清单

#### `V2M8-T1` 建立测试分层和运行约定

- 在测试文档中定义三层测试：默认离线测试、PostgreSQL integration profile、V2 regression corpus
- 明确默认 `dotnet test` 不依赖真实外部 HTTP 服务、外部 LLM、NewsNow、DailyHotApi 或密钥
- 约定 PostgreSQL 集成测试只在 `TRENDREPORTER2_POSTGRES_TEST_CONNECTION` 等显式环境变量存在时启用
- 约定 corpus 回归使用固定 fixture、fake source client、fake LLM client 和 fake embedding client
- 说明本地完整验证命令、CI 默认验证命令和需要数据库时的扩展命令
- 标明哪些失败属于环境未启用，哪些失败属于真实回归

#### `V2M8-T2` 补齐核心单元测试

- 覆盖 source registry 和 source capability：NewsNow ranked、DailyHotApi ranked、flash source 和禁用 source 的行为
- 覆盖 ranked scoring、flash scoring、source weight、freshness、persistence、trend signal 和 push threshold
- 覆盖 blacklist policy、merged event 过滤、摘要候选过滤和静态报告 read model 过滤
- 覆盖 WebExtract tags 优先、`llm.tagging` 缺失补全、tag 规范化和 tag 数量限制
- 覆盖 LLM usage 成本计算、token 缺失处理、重试记录和失败降级
- 覆盖 embedding text builder、source text hash、vector recall merge、候选去重、candidate limit 和 vector failure fallback
- 覆盖 secondary merge 硬过滤、LLM 失败不合并、merge result 统计和 merged source event 不重复展示
- 所有单元测试使用 fake clock、fake repository、fake client 或内存 fixture，不读取本地 `config.yaml`

#### `V2M8-T3` 补齐 PostgreSQL 集成测试

- 覆盖 migration runner 首次执行、重复执行、checksum 不一致失败和空数据库初始化
- 覆盖全部 V2 migration 文件可以按顺序执行，包括 embedding、secondary merge 和 monitoring views
- 覆盖 content upsert、snapshot 写入、source sync、event item unique、push log dedup、app state upsert
- 覆盖 fetch run、fetch run source、fetch run stage、llm usage、report snapshot、tag/event_tag 写入
- 覆盖 embedding repository 的 hash skip、固定维度校验、event embedding upsert 和 vector recall 查询；pgvector 不可用时测试应明确跳过或给出环境原因
- 覆盖 merge history 写入、已处理事件对判断、event item 迁移、source event 去激活和事务回滚
- 覆盖 metrics schema 视图在空库和少量样本数据下可查询，不要求 Grafana 实例
- 集成测试必须通过环境变量显式启用，CI 可以选择不开启数据库 profile，但不能误报为通过真实数据库验证

#### `V2M8-T4` 建立 V2 regression corpus

- 在测试 fixture 中整理 V2 样本集，覆盖 ranked news merge、ranked news no merge、stale reactivation、flash multi source merge、flash repeated follow up、topic noise no merge
- 增加 blacklist、push dedup、digest idempotency、tag generation、static report filtering、vector recall improvement 和 secondary merge 样本
- 增加 LLM 未配置、LLM 返回异常、embedding 未配置、embedding 查询失败、WebExtract 未返回 tags 的降级样本
- 每个样本使用固定输入和确定性 fake client，不能访问真实外部服务
- 样本命名包含场景、输入类型和期望结果，便于单独定位失败原因
- corpus 可以被默认离线测试运行，不要求 PostgreSQL；需要数据库验证的样本只验证仓储边界，不复用外部服务

#### `V2M8-T5` 定义 corpus 样本格式和断言

- 定义样本字段：scenario id、description、sources、content items、existing events、config overrides、fake LLM responses、fake embeddings、expected assertions
- 断言至少支持 created event count、matched event id、not matched reason、score range、trigger reasons、push eligibility、digest inclusion、report inclusion、tags、merge decision 和 fallback path
- 对分数类断言使用明确区间或稳定阈值，避免只检查非空结果
- 对归并类断言同时检查正例和反例，避免只验证成功合并
- 对降级类断言检查主流程继续运行、错误被记录、结果回落到规则路径或空结果
- 对过滤类断言检查 `status = 'Merged'` 和 `is_blacklisted = true` 的事件不会进入 digest 和 report
- 文档说明如何新增样本、如何运行单个 scenario、如何解释失败 diff

#### `V2M8-T6` 更新运行文档

- 更新 PostgreSQL 准备方式，包含 pgvector 扩展要求、数据库用户权限、连接串占位写法和本地初始化建议
- 更新 migration 执行方式，说明 `migrateOnStartup`、validate、fetch once、digest once 和后台模式的差异
- 更新常用命令，覆盖 restore、build、Release build、test、validate、fetch once、digest once 和后台运行
- 说明 source registry 配置、NewsNow、DailyHotApi ranked、DailyHotApi flash 的本地替身或 fake 测试方式
- 说明静态报告输出目录、report snapshot、digest 输出和排查入口
- 增加常见错误处理：数据库不可达、pgvector 未安装、migration checksum 不一致、LLM 未配置、外部 source 不可达、配置校验失败

#### `V2M8-T7` 更新配置文档

- 对齐 `config.example.yaml`、`AppConfig` 和配置说明，确保字段名、默认值、必填项和弃用项一致
- 说明 `database.provider = postgres`、`database.connectionString`、`database.migrateOnStartup` 和不支持 LiteDB provider 的原因
- 说明 `sources` 下 NewsNow、DailyHotApi ranked、DailyHotApi flash 的最小配置和禁用方式
- 说明 `analysis` 中 ranked、flash、candidate limit、vector threshold、secondary merge threshold、digest 和 report 相关配置
- 说明 `llm.cluster`、`llm.judge`、`llm.tagging`、`llm.embedding` 均可未配置并降级，文档不得暗示 CI 必须提供密钥
- 说明 `enrichment`、`filters`、`pushers`、`report`、`system` 的关键字段和安全注意事项
- 检查示例配置只包含占位值，不包含真实 API key、推送密钥、生产连接串或个人路径

#### `V2M8-T8` 更新测试文档和 CI profile

- 在 `docs/testing.md` 中列出默认离线测试命令和预期覆盖范围
- 说明 PostgreSQL 集成测试启用变量、数据库准备命令、跳过条件和失败排查方式
- 说明 migration runner tests、repository tests、regression corpus tests 的职责边界
- 给出 CI 默认 profile：restore、build、Release build、offline unit tests、validate config example
- 给出可选 CI profile：带 PostgreSQL service 的 integration tests，仍不要求真实外部 LLM/API 或密钥
- 说明 fake HTTP、fake LLM、fake embedding 的使用约定，避免测试意外访问网络
- 记录哪些测试是稳定性 closeout 的门禁，哪些是本地扩展验证

#### `V2M8-T9` 整理调参记录和已知限制

- 记录 ranked 权重、flash 时间窗口、source weight、rank normalization、freshness decay、persistence window 的当前取值和调整依据
- 记录 vector similarity threshold、candidate limit、embedding request budget、source text hash 策略和已观察到的召回边界
- 记录 secondary merge similarity threshold、LLM confidence threshold、硬过滤规则和误合并风险
- 记录 LLM 成本观察维度：cluster、judge、tagging、embedding 的调用量、token、估算成本和失败率
- 记录 digest/report 过滤规则，尤其是 blacklisted 和 merged event 的处理
- 整理已知限制：外部 source 质量波动、WebExtract tags 缺失、embedding 模型维度固定、pgvector 环境要求、Grafana 只是可选观察面
- 为后续 V3 或维护任务留下清晰入口，但不把这些后续事项纳入 V2M8 范围

### 验收标准

- `dotnet restore TrendReporter2.sln --configfile NuGet.Config` 成功
- `dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal` 成功
- `dotnet build TrendReporter2.sln --configuration Release --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal` 成功
- 默认 `dotnet test` 或约定的 offline test profile 可在无 PostgreSQL、无真实 NewsNow、无真实 DailyHotApi、无真实 WebExtract、无真实外部 LLM/API、无密钥的环境中运行并通过
- PostgreSQL integration profile 可通过显式环境变量启用；未启用时测试报告能清楚说明跳过原因，不把跳过伪装成真实数据库通过
- migration runner tests 覆盖首次执行、重复执行、checksum 不一致失败、全部 migration 顺序执行，以及 metrics views 在空库可查询
- V2 regression corpus 覆盖 ranked、flash、topic noise、blacklist、push dedup、digest idempotency、tag、report、vector recall、secondary merge 和 stale reactivation
- failure fallback tests 覆盖 source 抓取失败、WebExtract 缺失 tags、tagging LLM 失败、embedding 未配置、vector 查询失败、cluster/judge LLM 失败和 secondary merge LLM 失败，且主流程按设计继续或降级
- 摘要和静态报告测试明确验证 `status = 'Merged'` 的 source event 与 `is_blacklisted = true` 的事件不会出现在 digest/report 输出中
- PostgreSQL 仓储测试覆盖 content upsert、snapshot、event item unique、push log dedup、app state、telemetry、llm usage、tag/event_tag、report snapshot、embedding 和 merge history 的关键写入语义
- `docs/running.md`、`docs/testing.md`、配置说明和本里程碑内容相互一致，足够支持新环境部署、测试启用、常见故障排查和后续维护
- `config.example.yaml` 与当前 `AppConfig`、`AppConfigValidator` 和文档字段一致，只包含占位值，不包含真实 API key、推送密钥、生产数据库连接串或本地私有路径
- V2M8 完成后，不新增动态 Dashboard、前端、LiteDB 迁移、双写、provider compatibility 或 tag subscription push 范围

## 4. 推荐开发顺序

按依赖关系和当前完成状态，建议顺序保持如下：

1. `V2M0` V2 基础准备
2. `V2M1` PostgreSQL 持久化主链路
3. `V2M2` 可观测性与 LLM usage
4. `V2M3` Source 抽象与 DailyHotApi/flash
5. `V2M4` Tag 与静态报告
6. `V2M5` pgvector 候选召回
7. `V2M6` 二次归并
8. `V2M7` Dashboard/Grafana 可选增强
9. `V2M8` 稳定化、文档与回归

原因：

- `V2M0` 和 `V2M1` 决定 V2 主数据库路径，越早完成越少返工
- `V2M2` 的 telemetry 和 LLM usage 会影响后续 source、tag、embedding 的成本观察
- `V2M3` 需要 PostgreSQL source registry 和 scoring schema 支持
- `V2M4` 的报告依赖稳定 read model 和 tag 表
- `V2M5` 的 pgvector 依赖 PostgreSQL 和 LLM usage
- `V2M6` 的二次归并依赖向量召回质量
- `V2M7` 是可选增强，不阻塞 V2 主线；当前状态中它已在 V2M8 前完成，因此顺序应保留在 V2M8 之前
- `V2M8` 的测试、文档和回归意识应贯穿各阶段，但集中收口应放在主能力和可选观察视图之后，作为 V2 的最终稳定化 closeout

## 5. 可并行任务

虽然整体建议串行推进，但以下任务可以并行：

| 并行组 | 可并行任务 |
| --- | --- |
| `P1` | `V2M0-T1` 配置模型、`V2M0-T5` migration runner、`V2M0-T7` CLI 调整 |
| `P2` | `V2M1-T2` content 仓储、`V2M1-T3` event 仓储、`V2M1-T4` app state 仓储 |
| `P3` | `V2M2-T2` telemetry 契约、`V2M2-T6` LLM usage wrapper、`V2M2-T7` 成本估算 |
| `P4` | `V2M3-T1` source 模型、`V2M3-T4` NewsNow 改造、`V2M3-T5` DailyHotApi adapter |
| `P5` | `V2M4-T3` WebExtract tags、`V2M4-T4` WebExtract tag 规范化、`V2M4-T5` LLM tagging 补全、`V2M4-T7` report read model、`V2M4-T9` HTML renderer |
| `P6` | `V2M5-T3` EmbeddingClient、`V2M5-T4` embedding 仓储、`V2M5-T8` 召回合并 |
| `P7` | `V2M6-T3` 相似事件对发现、`V2M6-T4` 硬过滤、`V2M6-T10` 二次归并测试 |
| `P8` | `V2M8-T1` 测试分层、`V2M8-T2` 单元测试、`V2M8-T4` regression corpus、`V2M8-T6` 运行文档 |

## 6. 最小可运行版本

如果想尽快得到一个可用的 V2，可以先做一个 `MVP` 子集：

- 完成 `V2M0`
- 完成 `V2M1`
- 在 `V2M2` 中至少完成 `fetch_run_source`、`fetch_run_stage` 和 cluster/judge 的 `llm_usage`
- 在 `V2M3` 中先只把 NewsNow 改造成 source registry 下的 `ranked_news`
- 暂缓 DailyHotApi flash、tag、report、pgvector、二次归并和 Dashboard

这个 MVP 的目标是：V1 主链路完整跑在 PostgreSQL 上，并且可以观察每轮运行耗时和 LLM 成本。

MVP 不包含：

- LiteDB 迁移
- LiteDB/PostgreSQL 双写
- 动态 Dashboard
- tag 驱动推送订阅
- Grafana 必选面板

## 7. 建议的第一批开发任务

如果下一步要直接进入编码，我建议先做这一批：

1. `V2M0-T1` 更新 `DatabaseConfig`
2. `V2M0-T2` 更新 `AppConfigValidator`
3. `V2M0-T3` 引入 `Npgsql` 和 `Dapper`
4. `V2M0-T4` 注册 `NpgsqlDataSource`
5. `V2M0-T5` 实现 `SqlMigrationRunner`
6. `V2M0-T6` 创建 `0001_init.sql`，包含 `CREATE EXTENSION IF NOT EXISTS vector`
7. `V2M0-T7` 移除 `Program.cs` 中的 `data-view` 命令
8. `V2M1-T1` 创建核心表 schema
9. `V2M1-T2` 实现 `PostgresContentRepository`
10. `V2M1-T5` 实现 `PostgresFetchRunRepository`

完成这 10 步后，V2 会从配置和连接层进入真实 PostgreSQL 写入阶段，后续 event、score、digest 仓储可以继续接上。

## 8. V2 完成定义

V2 可以认为“基本完成”，需要满足以下条件：

- 系统不依赖 LiteDB 文件，主路径使用 PostgreSQL
- `validate`、`fetch-once`、`digest-once` 和后台模式可运行
- `data-view` 已从 V2 CLI 移除
- NewsNow ranked source 行为不回退
- 至少一个 DailyHotApi 或同类 source 可通过 source registry 接入
- flash source 不依赖 rank 也能参与事件发现
- ranked 和 flash scoring signal 分开记录
- 每轮运行有 run/source/stage telemetry
- cluster、judge、tagging、embedding 的 LLM usage 可记录 token、成本、耗时、重试和错误
- tag/event_tag 可用于展示和搜索
- 静态 HTML 报告可从 read model 生成
- pgvector recall 可增强候选召回，失败时规则召回继续工作
- 二次归并保留 merge history、lineage 和 evidence，不硬删除原始内容
- 摘要和推送幂等仍然成立
- PostgreSQL 主路径有自动化测试或集成测试覆盖
- V2 运行、配置和测试文档可支持新环境部署

## 9. 不属于 V2 早期完成定义的内容

以下内容可以后续再做，不阻塞 V2 主线：

- 历史 LiteDB 数据迁移
- LiteDB/PostgreSQL 双写
- LiteDB provider
- 动态 Dashboard
- 登录、多用户和权限系统
- tag 驱动的即时推送订阅
- Grafana 作为必选部署项
- 自建复杂前端应用

## 10. 结论

V2 的第一优先级是把当前稳定的 V1 主链路迁到 PostgreSQL，并在这个基础上补齐可观测性和 source 抽象。完成这一步后，DailyHotApi/flash、tag、静态报告、pgvector 和二次归并都能按可验证的小步继续推进。

里程碑顺序不应反过来。先做 Dashboard、tag 订阅或复杂前端，会让 V2 早期失焦。先把 PostgreSQL 主路径、显式 SQL、运行 telemetry 和 LLM usage 做扎实，V2 后续能力才有稳定基础。
