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
| 待开始 | `M0` | V2 基础准备 | 配置、CLI、PostgreSQL 连接、migration runner、移除 data-view |
| 待开始 | `M1` | PostgreSQL 持久化主链路 | 用 Dapper 仓储替换 LiteDB，打通 fetch、match、score、push、digest |
| 待开始 | `M2` | 可观测性与 LLM usage | 补齐 run/source/stage telemetry 和 LLM token、成本、错误记录 |
| 待开始 | `M3` | Source 抽象与 DailyHotApi/flash | 建立 source registry，支持 ranked news 和 flash feed |
| 待开始 | `M4` | Tag 与静态报告 | 实现 tag/event_tag，生成静态 HTML 摘要报告 |
| 待开始 | `M5` | pgvector 候选召回 | 接入 embedding 表、向量索引和 vector recall，规则召回保底 |
| 待开始 | `M6` | 二次归并 | 合并拆分过细的事件，保留 lineage 和 evidence |
| 待开始 | `M7` | Dashboard/Grafana 可选增强 | 基于 PostgreSQL 提供运行健康和历史查询视图 |
| 待开始 | `M8` | 稳定化、文档与回归 | 补齐测试、运行说明、回归样本和调参记录 |

## 3. 里程碑详情

### 3.1 `M0` V2 基础准备

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

#### `M0-T1` 更新配置模型

- 修改 `src/TrendReporter2.Core/Configuration/AppConfig.cs`
- 将 `DatabaseConfig` 调整为：
  - `Provider`
  - `ConnectionString`
  - `MigrateOnStartup`
- 移除或停止使用 `database.path`
- 保持 YAML camelCase 绑定

#### `M0-T2` 更新配置校验

- 修改 `AppConfigValidator`
- 要求 `database.provider` 必须等于 `postgres`
- 要求 `database.connectionString` 非空
- 保留 `analysis.fetchInterval`、`pushTime`、`system.timeZone` 等现有校验
- 明确拒绝 `litedb` provider

#### `M0-T3` 引入 PostgreSQL 依赖

- 在 Infrastructure 引入 `Npgsql`
- 引入 `Dapper`
- 不引入 EF Core 作为主路径
- 确认 `NuGet.Config` 仍只使用 nuget.org

#### `M0-T4` 注册 `NpgsqlDataSource`

- 在 `DependencyInjection.cs` 中注册 app-wide `NpgsqlDataSource`
- 所有 PostgreSQL 仓储都从 data source 打开连接
- 不让仓储直接读取 connection string

#### `M0-T5` 实现 SQL migration runner

- 创建 `SqlMigrationRunner`
- 创建 `schema_migration` 表
- 按文件名顺序执行 SQL
- 记录 version、name、checksum、applied_at
- 已执行 migration checksum 不一致时失败

#### `M0-T6` 创建初始 migration 目录

- 在 Infrastructure 添加 `Persistence/Migrations`
- 创建 `0001_init.sql`
- 在 migration 中执行 `CREATE EXTENSION IF NOT EXISTS vector`
- 提前验证目标 PostgreSQL 环境支持 pgvector，避免到 M5 才暴露扩展不可用
- 先创建 `schema_migration` 之外的最小核心表占位或完整 M1 schema

#### `M0-T7` 调整 `Program.cs` CLI

- 保留：
  - `validate`
  - `fetch-once`
  - `digest-once`
  - 后台模式
- 移除：
  - `data-view`
- 未知命令输出中文帮助和错误

#### `M0-T8` 移除 V2 的 data-view 注册

- 停止注册 `DataViewReader`
- 停止从 CLI 访问 LiteDB collection 名称
- 保留文件删除可在后续清理任务中完成，但 V2 命令不再暴露

#### `M0-T9` 更新配置示例

- 将 V2 使用的示例配置改为 `database.provider` 和 `database.connectionString`
- 不在本任务中提交真实 connection string
- 不把 `config.yaml` 加入版本控制

### 验收标准

- `validate --config config.example.yaml` 可校验 PostgreSQL 配置形态
- provider 不是 `postgres` 时明确报错
- migration runner 可创建 `schema_migration`
- `data-view` 命令不再出现在 V2 CLI 帮助中
- 代码中没有新的 LiteDB provider 或双写路径

### 3.2 `M1` PostgreSQL 持久化主链路

### 目标

用 PostgreSQL/Dapper 仓储替换 LiteDB 仓储，使 V1 主链路在新数据库上跑通：抓取、入库、富化、归并、评分、推送和摘要。

### 交付物

- 核心 PostgreSQL 表
- Dapper 仓储实现
- `fetch-once` 写入 PostgreSQL
- `digest-once` 使用 PostgreSQL 读取候选并保持幂等
- LiteDB 初始化和仓储不再注册到主路径

### 任务清单

#### `M1-T1` 设计核心表 migration

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

#### `M1-T2` 实现 `PostgresContentRepository`

- 替换 content item upsert
- 写入 content snapshot
- 支持按富化状态查询内容
- 支持更新 summary、summary source、enrichment status
- 使用 Dapper 显式 SQL

#### `M1-T3` 实现 `PostgresEventRepository`

- 新建和更新 event
- 查询 active 和 stale 候选事件
- 写入 event item
- 写入 event score snapshot
- 查询摘要候选
- 写入 push log
- 保证 dedup key unique 冲突可被识别

#### `M1-T4` 实现 `PostgresAppStateRepository`

- 支持 get by key
- 支持 upsert key/value
- 用于 `DigestJob` 摘要幂等

#### `M1-T5` 实现 `PostgresFetchRunRepository`

- 创建 running fetch run
- 完成时更新状态和统计
- 记录 started_at、finished_at、source count、item count、error summary
- 保持 V1 `FetchJob` 统计语义

#### `M1-T6` 替换 DI 注册

- 在 `AddTrendReporterInfrastructure` 中注册 PostgreSQL 仓储
- 移除 LiteDB initializer 主路径注册
- 不保留 `ILiteDbConnectionFactory` 作为 V2 默认依赖

#### `M1-T7` 适配 `FetchJob`

- 保持 fetch -> ingest -> enrich -> match -> score/push 流程
- 写入 PostgreSQL content 和 snapshot
- 事件归并和评分从 PostgreSQL 读取数据
- 单 source 失败不阻塞整轮

#### `M1-T8` 适配 `DigestJob`

- 从 PostgreSQL 查询 digest candidates
- 使用 `app_state` 控制同一摘要时刻幂等
- 使用 `push_log.dedup_key` 防重复推送
- 保持现有 `pushTime` 时区语义

#### `M1-T9` 移植持久化测试

- 将 LiteDB repository 测试改为 PostgreSQL integration 测试
- 覆盖 upsert、snapshot、event item unique、push log dedup、app state upsert
- 如果 CI 暂无 PostgreSQL，先用 integration profile 标记，但主路径测试必须存在

### 验收标准

- `fetch-once` 可把 content、snapshot、event、score、push log 写入 PostgreSQL
- `digest-once` 可从 PostgreSQL 查询候选并写入 `app_state` 和 `push_log`
- 重复抓取不会重复创建相同 content item
- 重复摘要不会重复发送同一时段
- 主路径不依赖 LiteDB 文件

### 3.3 `M2` 可观测性与 LLM usage

### 目标

补齐每轮运行的可观测性，记录 run/source/stage 统计，以及 cluster、judge、writer、tagging、embedding 的 LLM token、成本、耗时、重试和错误。

### 交付物

- `fetch_run_source`
- `fetch_run_stage`
- `llm_usage`
- LLM usage recorder
- FetchJob 完成时的 LLM 成本汇总日志

### 任务清单

#### `M2-T1` 添加 observability migration

- 创建 `fetch_run_source`
- 创建 `fetch_run_stage`
- 创建 `llm_usage`
- 添加 run_id、stage、model、created_at 等索引

#### `M2-T2` 定义 telemetry 契约

- 在 Core 新增 `IRunTelemetryRecorder`
- 定义记录 source 结果的方法
- 定义记录 stage 开始和结束的方法
- 定义记录 LLM usage 的方法

#### `M2-T3` 实现 PostgreSQL telemetry recorder

- 用 Dapper 写入 `fetch_run_source`
- 用 Dapper 写入 `fetch_run_stage`
- 用 Dapper 写入 `llm_usage`
- 支持同一 run 下按 stage 查询汇总

#### `M2-T4` 在 `FetchJob` 中记录 stage

- 记录 `fetch`
- 记录 `ingest`
- 记录 `enrich`
- 记录 `match`
- 记录 `score`
- 记录 `push`
- 后续 report 阶段接入时记录 `report`

#### `M2-T5` 在 source 抓取中记录 per source 结果

- 成功 source 记录 item_count 和 duration_ms
- 失败 source 记录 error
- skipped source 记录原因

#### `M2-T6` 包装 LLM client usage

- Cluster LLM 调用记录 `stage = cluster`
- Judge LLM 调用记录 `stage = judge`
- Writer LLM 调用记录 `stage = writer`
- 后续 tagging 和 embedding 使用同一 recorder
- token 缺失时不伪造 usage

#### `M2-T7` 实现成本估算

- 读取 `llm.*.pricing`
- 按每百万 token 计算成本
- 记录 input、output、cache read token
- `fetch_run.estimated_llm_cost` 汇总本轮成本

#### `M2-T8` 固定 LLM 重试策略

- 在 LLM adapter 中固定最多 3 次重试
- 记录 retry_count
- 记录最终错误
- 失败后沿用现有降级策略

### 验收标准

- 每轮 fetch 都有 source 和 stage 记录
- 每次 LLM 调用都能在 `llm_usage` 中追踪成功或失败
- FetchJob 结束日志包含本轮 LLM 调用次数和估算成本
- LLM 失败不会让整轮 fetch 直接失败

### 3.4 `M3` Source 抽象与 DailyHotApi/flash

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

#### `M3-T1` 定义 source 领域模型

- 新增 `SourceDefinition`
- 新增 `ContentKind` 常量或枚举
- 字段包含 provider、external_id、category、display_name、content_kind、enabled、weight

#### `M3-T2` 实现 `ISourceRegistry`

- 从配置读取 source 列表
- 可选将旧 `newsNow.sources` 映射为 ranked source
- 按 enabled 过滤
- 按 provider 分组

#### `M3-T3` 抽象 `IContentSourceClient`

- 定义 `Provider`
- 定义 `FetchAsync(SourceDefinition, CancellationToken)`
- 返回统一 `FetchedContentItem`

#### `M3-T4` 改造 `NewsNowClient`

- 使用 `SourceDefinition.ExternalId` 请求 `GET /api/s?id=source`
- 返回 `ranked_news`
- 写入 `rank`、`source_list_size`、`normalized_rank_score`
- 保留 HoverText 和 raw payload

#### `M3-T5` 实现 `DailyHotApiClient`

- 支持配置 `baseUrl`
- 支持 ranked endpoint
- 支持 flash endpoint
- 将返回项映射为 `FetchedContentItem`
- 单 endpoint 失败不阻塞其他 source

#### `M3-T6` 同步 source 表

- 启动或 fetch 前将配置 source upsert 到 `source` 表
- 禁用的 source 保留记录但不抓取
- 保证 `(provider, external_id, content_kind)` 唯一

#### `M3-T7` 支持 flash snapshot

- flash source 的 `rank` 为空
- 不写虚假 `normalized_rank_score`
- 计算 `freshness_score`
- 用 published_at 和 captured_at 支持新鲜度判断

#### `M3-T8` 调整评分服务

- 在 `EventScoringService` 中分开计算 ranked signal 和 flash signal
- 增加 trigger reasons：
  - `flash_multi_source`
  - `flash_repeated`
  - `flash_follow_up`
  - `flash_trusted_source`
- 写入 `rank_score` 和 `flash_score`

#### `M3-T9` 增加 source capability 测试

- ranked source 仍按排名评分
- flash source 不依赖 rank
- topic source 初期只入库或展示，不进入强推送策略

### 验收标准

- NewsNow ranked source 行为不回退
- DailyHotApi ranked source 可入库并参与评分
- flash source 无 rank 时仍可参与事件发现
- ranked signal 和 flash signal 在 score snapshot 中分开保存
- topic 不成为 V2 早期强推送要求

### 3.5 `M4` Tag 与静态报告

### 目标

实现 tag/event_tag 的初始能力，并从 read model 生成静态 HTML 报告。tag 初期只用于展示和搜索，不驱动 push subscription。

### 交付物

- `tag`
- `event_tag`
- tag 生成规则
- report read model
- 静态 HTML 报告文件
- `report_snapshot`

### 任务清单

#### `M4-T1` 添加 tag 和 report migration

- 创建 `tag`
- 创建 `event_tag`
- 可选创建 `content_item_tag`
- 创建 `report_snapshot`
- 添加 tag name unique 约束

#### `M4-T2` 定义 tag 领域模型

- 新增 `Tag`
- 新增 `EventTag`
- 新增 `TagCategory`
- 新增 `TagSource`

#### `M4-T3` 实现规则 tag 生成

- 从 source category 生成 domain tag
- 从事件关键词生成 topic tag
- 从实体提取 entity tag
- 控制 tag 数量，避免低质量 tag 膨胀

#### `M4-T4` 可选接入 LLM tagging

- 对高价值事件调用 tagging LLM
- 记录 `llm_usage.stage = tagging`
- 返回 tag 名、分类、置信度
- LLM 失败时保留规则 tag

#### `M4-T5` 实现 tag 仓储

- upsert tag
- upsert event_tag
- 查询事件 tag 列表
- 支持按 tag 查询事件，为后续搜索预留

#### `M4-T6` 定义 report read model

- 新增 `ReportEventItem`
- 新增 `ReportContentItem`
- 新增 `ReportPayload`
- 包含事件标题、摘要、阶段、score、heat、tag 和新闻链接

#### `M4-T7` 实现 `IReportReadModelQuery`

- 查询摘要窗口内的高价值事件
- 排除黑名单事件
- 排除 `status = merged` 的 source event
- 读取 event_tag 和相关新闻

#### `M4-T8` 实现 `StaticHtmlReportRenderer`

- 生成单个 HTML 文件
- 使用简单内联样式或静态模板
- 不引入前端构建系统
- 文件写入 `report.outputDirectory`

#### `M4-T9` 集成到 `DigestJob`

- 摘要生成时创建报告
- 写入 `report_snapshot`
- 可选把报告路径或 URL 放入摘要推送

#### `M4-T10` 增加报告测试

- read model 排序正确
- 黑名单不出现在报告中
- tag 展示正确
- 原文链接输出正确

### 验收标准

- 每个高价值事件可以关联 tag
- 摘要报告可生成并用浏览器打开
- 报告包含事件、评分、tag、相关新闻和原文链接
- tag 不影响 V2 初期即时推送资格
- 没有动态 Dashboard、登录或多用户要求

### 3.6 `M5` pgvector 候选召回

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

#### `M5-T1` 添加 embedding migration

- 创建 `content_embedding`
- 创建 `event_embedding`
- 字段包含 model、version、dimensions、source_text_hash、embedding
- 在 dimensions 固定后创建 HNSW cosine index

#### `M5-T2` 配置 embedding 模型

- 增加 `llm.embedding`
- 校验 model 和 dimensions
- pricing 用于成本估算

#### `M5-T3` 实现 `EmbeddingClient`

- 调用 OpenAI compatible embedding API
- 返回固定维度向量
- 记录 `llm_usage.stage = embedding`
- 失败时不阻塞 fetch 主流程

#### `M5-T4` 实现 embedding 仓储

- upsert content embedding
- upsert event embedding
- 根据 source text hash 判断是否需要重算
- 查询相似 event embedding

#### `M5-T5` 生成 content embedding

- 使用 title、hover_text、summary 组成文本
- 对新增或摘要变化的 content 生成向量
- 控制单轮 embedding 预算

#### `M5-T6` 生成 event embedding

- 使用 canonical title、summary、代表标题、tag 组成文本
- 事件更新后按 hash 判断是否重算
- 避免同一事件每轮无意义重算

#### `M5-T7` 实现 vector recall

- 根据 content embedding 查询相似 event
- 返回 event id、similarity、reason
- 设置 candidate limit
- 查询失败时降级为规则召回

#### `M5-T8` 合并召回结果

- 新增 `CompositeEventCandidateService`
- 合并 rule candidates 和 vector candidates
- 去重、排序、硬过滤
- 限制传给 Cluster LLM 的候选数量

#### `M5-T9` 增加召回回归测试

- 对比规则召回和 vector recall 命中情况
- 验证 vector failure fallback
- 验证候选数量不会爆炸

### 验收标准

- embedding 表可写入固定维度向量
- pgvector HNSW cosine index 可创建
- vector recall 可以补充规则召回漏掉的候选
- vector 查询失败时 fetch 主流程继续运行
- Cluster LLM 调用量不会因为候选增加而失控

### 3.7 `M6` 二次归并

### 目标

在 pgvector 召回稳定后，实现二次归并任务，修复在线归并偏保守导致的事件拆分，同时保留 lineage 和 evidence。

### 交付物

- `event_merge_history`
- `SecondaryMergeService`
- 相似事件对发现
- 二次归并硬过滤
- merge 后 evidence 和 score 更新

### 任务清单

#### `M6-T1` 添加 merge history migration

- 创建 `event_merge_history`
- 添加 `event.merged_into_event_id`
- 添加 `event_item.is_active`
- 添加 `event_item.created_by_merge_id`
- 添加必要索引和约束

#### `M6-T2` 定义二次归并模型

- 新增 `EventMergeCandidate`
- 新增 `EventMergeDecision`
- 新增 `EventMergeHistory`

#### `M6-T3` 发现相似事件对

- 选择 active 或近期 stale 事件
- 用 event embedding 查询相似事件
- 排除已 merged 事件
- 排除同一 merge history 已处理组合

#### `M6-T4` 实现硬过滤

- 核心实体明显不同则拒绝
- 时间、地点、关键数字冲突则拒绝
- event type 不兼容则拒绝
- 黑名单状态不直接决定合并，但要保留原因

#### `M6-T5` 接入 LLM merge 判定

- 对高相似候选调用 Cluster LLM
- 要求返回 same_event、related_but_distinct、unrelated
- 记录 confidence 和 reason
- 写入 `llm_usage.stage = cluster`

#### `M6-T6` 执行 merge

- 在事务中写入 `event_merge_history`
- source event 设置 `status = merged`
- source event 设置 `merged_into_event_id`
- 迁移或复制 active evidence 到 target event
- 原 event item 不硬删除

#### `M6-T7` 重算 target event

- 更新 target event summary
- 更新 tag
- 重算 score
- 后续摘要和推送过滤 merged source event

#### `M6-T8` 增加二次归并测试

- 明显重复事件可合并
- 明显不同事件不合并
- merge history 保留原因
- 原始 content item 不丢失

### 验收标准

- 系统能合并明显拆分的重复事件
- 误合并风险通过硬过滤和 LLM reason 可追踪
- 原始 content item、source event 和 event item lineage 不丢失
- 摘要和报告不重复展示 merged source event

### 3.8 `M7` Dashboard/Grafana 可选增强

### 目标

在 V2 主链路稳定后，基于 PostgreSQL 提供可选的运行健康和历史趋势查看能力。此里程碑不是 V2 早期要求。

### 交付物

- Grafana 查询视图或 SQL 示例
- 可选只读 dashboard spike
- 运行健康指标说明

### 任务清单

#### `M7-T1` 定义指标视图

- fetch run 成功率
- 每源失败率
- LLM token 和成本趋势
- 每日事件数
- 推送次数
- 重要事件分数分布

#### `M7-T2` 创建 SQL view

- 为 Grafana 提供稳定查询 view
- 不改变主业务表写入路径
- 不引入额外状态

#### `M7-T3` 编写 Grafana 接入说明

- 数据源配置
- 推荐面板
- 常用时间范围
- 成本和失败率告警建议

#### `M7-T4` 评估动态 Dashboard

- 只做 spike 或设计说明
- 评估 tag 云、事件检索、source 过滤、事件详情页
- 不引入登录、多用户或复杂权限作为 V2 必选项

### 验收标准

- 可以通过 SQL 或 Grafana 查看运行健康度和成本趋势
- Dashboard 仍是可选增强，不影响 V2 主链路完成定义

### 3.9 `M8` 稳定化、文档与回归

### 目标

补齐 V2 测试、运行说明、回归样本和调参记录，使系统可长期运行并便于后续维护。

### 交付物

- PostgreSQL 集成测试
- V2 regression corpus
- 运行说明更新
- 配置说明更新
- 调参记录和已知限制

### 任务清单

#### `M8-T1` 补齐单元测试

- source capability
- ranked scoring
- flash scoring
- tag generation
- LLM cost calculation
- vector recall merge
- secondary merge hard filters

#### `M8-T2` 补齐 PostgreSQL 集成测试

- migration 重复执行
- content upsert
- event item unique
- push log dedup
- app state upsert
- telemetry 写入
- llm usage 写入
- report snapshot 写入

#### `M8-T3` 扩展回归样本

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

#### `M8-T4` 更新运行文档

- PostgreSQL 准备方式
- migration 执行方式
- V2 配置示例
- 常用命令
- 静态报告输出目录
- 常见错误处理

#### `M8-T5` 更新测试文档

- 单元测试运行方式
- PostgreSQL 集成测试运行方式
- 需要的环境变量
- CI 覆盖范围

#### `M8-T6` 整理调参记录

- ranked 权重
- flash 时间窗口
- source weight
- vector similarity threshold
- secondary merge confidence threshold
- LLM 成本观察

### 验收标准

- V2 主链路有自动化测试覆盖
- PostgreSQL 主路径有集成测试或明确 CI profile
- 回归样本覆盖 ranked、flash、vector、merge 和 tag
- 文档足够支持新环境部署和排错

## 4. 推荐开发顺序

按依赖关系，建议严格按以下顺序推进：

1. `M0` V2 基础准备
2. `M1` PostgreSQL 持久化主链路
3. `M2` 可观测性与 LLM usage
4. `M3` Source 抽象与 DailyHotApi/flash
5. `M4` Tag 与静态报告
6. `M5` pgvector 候选召回
7. `M6` 二次归并
8. `M8` 稳定化、文档与回归
9. `M7` Dashboard/Grafana 可选增强

原因：

- `M0` 和 `M1` 决定 V2 主数据库路径，越早完成越少返工
- `M2` 的 telemetry 和 LLM usage 会影响后续 source、tag、embedding 的成本观察
- `M3` 需要 PostgreSQL source registry 和 scoring schema 支持
- `M4` 的报告依赖稳定 read model 和 tag 表
- `M5` 的 pgvector 依赖 PostgreSQL 和 LLM usage
- `M6` 的二次归并依赖向量召回质量
- `M8` 应贯穿执行，但在主要能力落地后集中收口
- `M7` 不阻塞 V2 主线，可以最后做或跳过

## 5. 可并行任务

虽然整体建议串行推进，但以下任务可以并行：

| 并行组 | 可并行任务 |
| --- | --- |
| `P1` | `M0-T1` 配置模型、`M0-T5` migration runner、`M0-T7` CLI 调整 |
| `P2` | `M1-T2` content 仓储、`M1-T3` event 仓储、`M1-T4` app state 仓储 |
| `P3` | `M2-T2` telemetry 契约、`M2-T6` LLM usage wrapper、`M2-T7` 成本估算 |
| `P4` | `M3-T1` source 模型、`M3-T4` NewsNow 改造、`M3-T5` DailyHotApi adapter |
| `P5` | `M4-T3` 规则 tag、`M4-T6` report read model、`M4-T8` HTML renderer |
| `P6` | `M5-T3` EmbeddingClient、`M5-T4` embedding 仓储、`M5-T8` 召回合并 |
| `P7` | `M6-T3` 相似事件对发现、`M6-T4` 硬过滤、`M6-T8` 二次归并测试 |
| `P8` | `M8-T1` 单元测试、`M8-T3` 回归样本、`M8-T4` 运行文档 |

## 6. 最小可运行版本

如果想尽快得到一个可用的 V2，可以先做一个 `MVP` 子集：

- 完成 `M0`
- 完成 `M1`
- 在 `M2` 中至少完成 `fetch_run_source`、`fetch_run_stage` 和 cluster/judge 的 `llm_usage`
- 在 `M3` 中先只把 NewsNow 改造成 source registry 下的 `ranked_news`
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

1. `M0-T1` 更新 `DatabaseConfig`
2. `M0-T2` 更新 `AppConfigValidator`
3. `M0-T3` 引入 `Npgsql` 和 `Dapper`
4. `M0-T4` 注册 `NpgsqlDataSource`
5. `M0-T5` 实现 `SqlMigrationRunner`
6. `M0-T6` 创建 `0001_init.sql`，包含 `CREATE EXTENSION IF NOT EXISTS vector`
7. `M0-T7` 移除 `Program.cs` 中的 `data-view` 命令
8. `M1-T1` 创建核心表 schema
9. `M1-T2` 实现 `PostgresContentRepository`
10. `M1-T5` 实现 `PostgresFetchRunRepository`

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
- cluster、judge、writer、tagging、embedding 的 LLM usage 可记录 token、成本、耗时、重试和错误
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
