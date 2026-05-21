# TrendReporter2 V2 设计稿

## 1. 背景

TrendReporter2 V1 已经完成个人舆论趋势监控的最小主链路：从 NewsNow 抓取榜单新闻，写入内容与快照，按需富化弱标题，归并为事件，计算事件热度与重要性，并通过即时推送和定时摘要输出结果。

V2 的目标不是重写产品方向，而是在 V1 事件级趋势发现系统的基础上，提升数据平台、来源覆盖、召回质量、可观测性和可读输出能力。

本设计稿基于一个重要前提：**V2 从全新数据开始，不需要迁移历史 LiteDB 数据**。因此 V2 可以把 PostgreSQL 作为第一阶段基础设施，避免先在 LiteDB 上实现新模型、后续再迁移造成二次返工。

## 2. V2 定位

V2 定位为：

```text
以 PostgreSQL 为新数据平台，扩展多类型内容源，增强事件召回与归并质量，补齐运行可观测性，并输出更可读的事件摘要页面。
```

V2 仍然保留 V1 的核心产品判断：TrendReporter2 不是新闻阅读器，而是事件级趋势发现、追踪和推送系统。

## 3. 目标与非目标

### 3.1 目标

1. 将主存储从 LiteDB 切换到 PostgreSQL。
2. 在 PostgreSQL schema 中直接承载 V2 需要的新概念，包括 source、tag、LLM usage、report、embedding 和 event merge history。
3. 保留现有分层结构：`App -> Core`、`App -> Infrastructure`、`Infrastructure -> Core`。
4. 保留现有主链路形态：fetch -> ingest -> enrich -> match -> score/push -> digest/report。
5. 增强每轮运行的可观测性，包括阶段耗时、每源结果、LLM 调用次数、token 与成本估算。
6. 支持多内容源，不再只围绕 NewsNow 榜单建模。
7. 支持快讯/无排名来源，基于时间、多源覆盖和重复出现频率判断重要性。
8. 引入 tag，支持事件展示、检索和后续订阅策略。
9. 生成静态摘要网页，展示当前热点事件及相关原文链接。
10. 引入 pgvector 作为事件召回增强能力，并保留规则召回作为 fallback。
11. 在召回质量稳定后，引入事件二次归并能力。

### 3.2 非目标

1. 不迁移历史 LiteDB 数据。
2. 不做 LiteDB/PostgreSQL 双写兼容层。
3. 不在 V2 初期实现动态 Web 应用、登录、多用户或复杂权限。
4. 不把社媒 Topic 与新闻事件在第一阶段强行统一评分。
5. 不把 tag 订阅驱动即时推送作为 V2 初期必选能力。
6. 不把 fork NewsNow 或重做 NewsNow 生态作为主线任务；这类工作只作为调研 spike。
7. 不把 Grafana 或动态 Dashboard 作为 V2 起跑线。

## 4. V1 基线

V1 当前能力包括：

- 读取并校验 YAML 配置。
- 初始化 LiteDB 集合和索引。
- 通过 NewsNow 抓取配置中的新闻源。
- 写入 `content_item`、`content_snapshot` 和 `fetch_run`。
- 对弱标题或上下文不足的新闻调用 WebExtract 富化。
- 通过规则召回候选事件。
- 使用 OpenAI 兼容 Cluster LLM 辅助事件归并。
- 创建、合并、复活事件。
- 计算事件热度、趋势和综合评分。
- 使用 Judge LLM 修正重要性和事件阶段。
- 执行即时推送和重复推送控制。
- 执行定时摘要，并通过 `app_state` 和 `push_log` 保证摘要幂等。
- 提供 `data-view` 命令查看 LiteDB 集合。
- 包含 xUnit 测试和回归样本。

V1 的主要限制：

- LiteDB 不适合承载后续关系型查询、Grafana 接入和 pgvector。
- `FetchRun` 只记录粗粒度统计，没有 per-stage 或 LLM 成本明细。
- source 只是配置里的字符串，没有 source registry 或 capability。
- 内容模型默认围绕 ranked news，快讯/无排名 feed 没有独立评分语义。
- 事件召回是内存文本相似度，召回质量和可扩展性有限。
- 没有 tag、report artifact、embedding、event merge history 等 V2 概念。

## 5. 总体架构

V2 保留三层结构：

```text
TrendReporter2.App
  - CLI
  - Host / BackgroundService
  - FetchJob / DigestJob / ReportJob

TrendReporter2.Core
  - 配置模型
  - 领域模型
  - 业务规则
  - 仓储和适配器接口

TrendReporter2.Infrastructure
  - PostgreSQL / Dapper / Npgsql
  - NewsNow / DailyHotApi / 其他 source adapter
  - WebExtract
  - LLM adapters
  - Push adapters
  - 静态报告生成
```

V2 的关键变化在 Infrastructure：LiteDB persistence 被 PostgreSQL persistence 取代。Core 中的业务规则仍然保留，避免把事件归并、评分、黑名单、tag 策略等逻辑下沉到数据库适配层。

## 6. PostgreSQL 第一阶段

### 6.1 技术选择

V2 使用：

- PostgreSQL 作为主数据库。
- Npgsql 作为 PostgreSQL 驱动。
- Dapper 作为轻量 SQL 映射层。
- SQL migration 机制管理 schema 版本。
- pgvector extension 预先启用，embedding 表可以后续再写入。

不引入 EF Core 作为默认路径。原因是当前项目更适合显式 SQL、可控查询和轻量仓储实现；EF 的 change tracking 和 LINQ 不是 V2 的核心需求。

### 6.2 配置形态

数据库配置建议从 LiteDB 文件路径演进为 provider + connection string：

```yaml
database:
  provider: "postgres"
  connectionString: "Host=localhost;Port=5432;Database=trendreporter;Username=trendreporter;Password=..."
```

V2 不再保留 `litedb` provider，因为没有历史数据迁移要求。

### 6.3 Schema 原则

V2 不应一比一复制 LiteDB document 结构，而应使用关系型模型表达稳定关系：

- source 独立成表。
- content item 与 snapshot 分离。
- event 与 event evidence 分离。
- event tag、embedding、merge history 独立成表。
- LLM usage 和 report snapshot 独立成表。
- push log、app state 继续保留幂等能力。

所有运行时写入都应具备确定性 dedup key 或唯一约束，避免重复抓取、重复映射和重复推送。

### 6.4 删除`data-view`命令

V1 版本中有`data-view`命令用于查看liteDB数据库，由于liteDB在V2中被干掉了，所以data-view功能不需要再有了，直接去掉

## 7. 数据模型设计

### 7.1 Source

`source` 表记录内容来源的稳定信息：

| 字段 | 说明 |
| --- | --- |
| `id` | 内部 source id |
| `provider` | `newsnow`、`daily_hot_api` 等 |
| `external_id` | provider 内部 source id |
| `category` | `china`、`social`、`tech` 等分类 |
| `display_name` | 中文展示名 |
| `content_kind` | `ranked_news`、`flash_feed`、`topic` |
| `ranked` | 是否有排名 |
| `enabled` | 是否启用 |
| `weight` | 后续评分可用的来源权重 |
| `created_at` / `updated_at` | 时间戳 |

source capability 是 V2 多源设计的核心。不同 source 类型不能强行套用同一评分语义。

### 7.2 Fetch Run

`fetch_run` 记录一次抓取运行：

| 字段 | 说明 |
| --- | --- |
| `id` | run id |
| `started_at` / `finished_at` | 开始/结束时间 |
| `status` | `running`、`succeeded`、`partial`、`failed` |
| `source_count` | 来源总数 |
| `success_source_count` | 成功来源数 |
| `failure_source_count` | 失败来源数 |
| `fetched_item_count` | 抓取条目数 |
| `enriched_item_count` | 富化成功条目数 |
| `matched_event_count` | 映射事件条目数 |
| `pushed_event_count` | 推送事件数 |
| `estimated_llm_cost` | 本轮 LLM 估算成本 |
| `error_summary` | 错误摘要 |

`fetch_run_source` 记录每个 source 的运行结果：

| 字段 | 说明 |
| --- | --- |
| `run_id` | run id |
| `source_id` | source id |
| `status` | 成功/失败 |
| `duration_ms` | 抓取耗时 |
| `item_count` | 条目数 |
| `error` | 错误信息 |

`fetch_run_stage` 记录阶段耗时：

| 字段 | 说明 |
| --- | --- |
| `run_id` | run id |
| `stage` | `fetch`、`ingest`、`enrich`、`match`、`score`、`push`、`report` |
| `started_at` / `finished_at` | 阶段时间 |
| `duration_ms` | 阶段耗时 |
| `status` | 阶段状态 |
| `error` | 阶段错误 |

### 7.3 Content Item

`content_item` 是内容去重后的稳定条目：

| 字段 | 说明 |
| --- | --- |
| `id` | content id |
| `source_id` | 来源 |
| `dedup_key` | 去重键，建议唯一 |
| `source_item_id` | 外部条目 id |
| `content_kind` | `ranked_news`、`flash_feed`、`topic` |
| `title` | 标题 |
| `url` / `mobile_url` | 链接 |
| `published_at` | 发布时间 |
| `hover_text` | source 提供的 hover/摘要 |
| `summary` | 当前摘要 |
| `summary_source` | `title_only`、`hover_text`、`enrichment` 等 |
| `need_enrichment` | 是否需要富化 |
| `enrichment_status` | 富化状态 |
| `enrichment_tried_at` | 最近富化尝试时间 |
| `raw_payload` | 原始 JSON |
| `first_seen_at` / `last_seen_at` | 首次/最近出现时间 |
| `created_at` / `updated_at` | 时间戳 |

### 7.4 Content Snapshot

`content_snapshot` 记录每轮抓取中条目的动态表现：

| 字段 | 说明 |
| --- | --- |
| `id` | snapshot id |
| `run_id` | run id |
| `content_item_id` | content item id |
| `source_id` | source id |
| `captured_at` | 抓取时间 |
| `visual_order` | 页面/列表出现顺序 |
| `rank` | 排名；无排名来源可为空 |
| `source_list_size` | source 列表长度；无排名来源可为空 |
| `normalized_rank_score` | 归一化排名分；无排名来源可为空 |
| `freshness_score` | 快讯/无排名来源的新鲜度分 |

排名型 source 使用 `rank` 和 `normalized_rank_score`；快讯型 source 使用 `published_at`、`captured_at` 和 `freshness_score`。

### 7.5 Event

`event` 记录事件聚合体的核心状态：

| 字段 | 说明 |
| --- | --- |
| `id` | event id |
| `event_type` | `news_event`、`topic` |
| `canonical_title` | 标准标题 |
| `summary` | 摘要 |
| `status` | `active`、`stale` |
| `current_stage` | `initial`、`expanding`、`escalating`、`follow_up`、`cooling` |
| `progress_summary` | 进程摘要 |
| `first_seen_at` / `last_seen_at` | 事件出现时间 |
| `last_activated_at` | 最近复活时间 |
| `last_pushed_at` | 最近推送时间 |
| `push_count` | 推送次数 |
| `is_blacklisted` | 是否黑名单 |
| `blacklist_reason` | 黑名单原因 |
| `created_at` / `updated_at` | 时间戳 |

建议把以下内容拆表，而不是继续堆到 `event` 上：

- alias
- entity
- place
- key term
- milestone
- tag
- embedding
- merge history

### 7.6 Event Evidence

`event_item` 记录内容条目与事件的证据关系：

| 字段 | 说明 |
| --- | --- |
| `id` | 关系 id |
| `event_id` | event id |
| `content_item_id` | content item id |
| `confidence` | 归并置信度 |
| `matched_at` | 归并时间 |
| `match_reason` | 归因说明 |
| `is_active` | 当前是否有效 |

V2 为二次归并预留 `is_active`，避免后续需要硬删除或不可追踪地重映射 evidence。

### 7.7 Event Score

`event_score_snapshot` 记录事件评分历史：

| 字段 | 说明 |
| --- | --- |
| `id` | score id |
| `event_id` | event id |
| `run_id` | run id |
| `calculated_at` | 评分时间 |
| `coverage_score` | 覆盖分 |
| `rank_score` | 排名分 |
| `freshness_score` | 快讯新鲜度分 |
| `trend_score` | 趋势分 |
| `persistence_score` | 持续性分 |
| `llm_boost_score` | LLM 加权分 |
| `reactivation_bonus` | 复活加分 |
| `total_score` | 综合分 |
| `unique_source_count` | 独立来源数 |
| `heat_value` | 热度值 |
| `trigger_reasons` | 触发原因 JSON |
| `current_stage` | 当前阶段 |

V2 需要区分 ranked source 与 flash source 的评分贡献，避免无排名内容被强行折算成虚假 rank。

### 7.8 Tag

`tag`、`event_tag` 和可选的 `content_item_tag` 支持事件分类、展示与检索。

`tag`：

| 字段 | 说明 |
| --- | --- |
| `id` | tag id |
| `name` | tag 名称 |
| `display_name` | 中文展示名 |
| `category` | tag 分类，如 topic、entity、domain、risk |
| `created_at` | 创建时间 |

`event_tag`：

| 字段 | 说明 |
| --- | --- |
| `event_id` | event id |
| `tag_id` | tag id |
| `confidence` | 置信度 |
| `source` | `rule`、`llm`、`manual` |
| `created_at` | 创建时间 |

V2 初期 tag 只用于展示和检索；tag 订阅影响推送优先级属于后续增强。

### 7.9 LLM Usage

`llm_usage` 记录每次 LLM 调用：

| 字段 | 说明 |
| --- | --- |
| `id` | usage id |
| `run_id` | run id |
| `stage` | `cluster`、`judge`、`writer`、`tagging`、`embedding` |
| `model` | 模型名 |
| `request_id` | 外部请求 id，可为空 |
| `content_item_id` | 相关 content item，可为空 |
| `event_id` | 相关 event，可为空 |
| `input_tokens` | 输入 token |
| `output_tokens` | 输出 token |
| `cache_read_tokens` | cache read token |
| `estimated_cost` | 估算成本 |
| `duration_ms` | 耗时 |
| `success` | 是否成功 |
| `retry_count` | 重试次数 |
| `error` | 错误信息 |
| `created_at` | 创建时间 |

LLM 重试次数在代码中定义常量，例如固定 3 次，不走配置。

### 7.10 Embedding

`content_embedding` 和 `event_embedding` 为 pgvector 召回做准备。

`event_embedding` 建议字段：

| 字段 | 说明 |
| --- | --- |
| `event_id` | event id |
| `embedding_model` | embedding 模型 |
| `embedding_version` | embedding 版本 |
| `source_text_hash` | 生成 embedding 的文本 hash |
| `embedding` | pgvector 向量 |
| `created_at` / `updated_at` | 时间戳 |

embedding 必须记录模型和 source text hash，否则后续无法判断是否需要重算。

### 7.11 Event Merge History

`event_merge_history` 记录二次归并历史：

| 字段 | 说明 |
| --- | --- |
| `id` | merge id |
| `source_event_id` | 被合并事件 |
| `target_event_id` | 目标事件 |
| `confidence` | 置信度 |
| `reason` | 合并原因 |
| `decided_by` | `rule`、`llm`、`manual` |
| `created_at` | 合并时间 |

二次归并不能简单删除旧事件，应保留 lineage，便于排查误合并。

### 7.12 Report Snapshot

`report_snapshot` 记录静态报告生成结果：

| 字段 | 说明 |
| --- | --- |
| `id` | report id |
| `report_type` | `digest_html`、`daily`、`manual` |
| `slot_time` | 摘要时段 |
| `generated_at` | 生成时间 |
| `file_path` | 静态文件路径 |
| `event_count` | 事件数 |
| `payload_json` | 报告结构化内容 |

报告内容应从 read model 生成，避免在模板中直接拼复杂业务逻辑。

## 8. Source 抽象

V2 source 分为三类：

### 8.1 Ranked News

典型来源：NewsNow 榜单、DailyHotApi 热榜。

核心信号：

- source 覆盖数
- rank
- normalized rank score
- 热度变化
- 持续活跃时间

### 8.2 Flash Feed

典型来源：NewsNow 快讯、DailyHotApi 快讯、RSS 类 feed。

核心信号：

- published_at
- 多 source 短时间窗口命中
- 重复出现频率
- source 权重
- 是否与既有事件匹配

Flash feed 不应伪造 rank，也不应直接使用 ranked news 的排名阈值。

### 8.3 Topic / Social

典型来源：微博、知乎、抖音、V2EX 等社交或社区话题。

V2 初期可以先作为内容类型入库和展示，不急于和 news event 完全统一评分。Topic 需要单独确认：

- 生命周期
- 去重键
- 热度指标
- 与 news event 的关系
- 推送阈值

## 9. 快讯评分策略

快讯/无排名内容的重要性判断建议使用独立 trigger reason：

- `flash_multi_source`：短时间窗口内多个 source 报道同一事件。
- `flash_repeated`：同一事件在多个快讯条目中重复出现。
- `flash_follow_up`：命中旧事件并构成后续进展。
- `flash_trusted_source`：高权重 source 单独触发。

评分时应把 ranked signal 和 flash signal 分开计算，再汇总到 `total_score`。

## 10. 可观测性设计

V2 可观测性分为四层：

1. **Run 级别**：每轮总耗时、状态、成功/失败来源数、总成本。
2. **Source 级别**：每个 source 的耗时、条目数、错误。
3. **Stage 级别**：fetch、ingest、enrich、match、score、push、report 的耗时与错误。
4. **LLM 级别**：调用次数、token、重试次数、成本、失败率。

日志仍然保留中文，但结构化字段应稳定，包括：

- `runId`
- `sourceId`
- `contentItemId`
- `eventId`
- `stage`
- `durationMs`
- `model`
- `estimatedCost`

## 11. LLM 调用优化

V2 的 LLM 优化包含：

1. Cluster/Judge/Writer/Tagging/Embedding 各自记录 usage。
2. 固定最多 3 次重试。
3. 解析失败、HTTP 失败、空响应都记录到 `llm_usage`。
4. token usage 来自 OpenAI-compatible response 的 `usage` 字段；缺失时记录为空，不伪造。
5. 成本基于配置中的 per-million token 单价估算。
6. FetchJob 完成时输出本轮 LLM 汇总。

LLM 失败不应让整轮 fetch 失败，除非该阶段未来被显式定义为强依赖。

## 12. Tag 设计

V2 tag 有三种来源：

1. 规则提取：source、实体、关键词、分类。
2. LLM 生成：主题、领域、风险类型。
3. 手动维护：用户明确关心的 tag。

V2 初期只实现：

- tag 入库
- event_tag 关联
- 静态报告展示

后续再考虑：

- 配置订阅 tag
- tag 命中提高推送优先级
- tag 云 dashboard

## 13. 静态摘要网页

V2 先实现静态 HTML，不先做动态 Dashboard。

静态报告应包含：

- 生成时间
- 报告窗口
- 事件列表
- 每个事件的标题、摘要、阶段、进程摘要
- score、heat、source count、trigger reason
- tag
- 相关新闻列表
- 每条新闻的 source、发布时间、标题、原文链接

建议生成方式：

1. DigestJob 或独立 ReportJob 查询 report read model。
2. 生成结构化 report payload。
3. 使用模板渲染 HTML。
4. 写入本地 `data/reports/` 或配置指定目录。
5. 可选把报告链接放入定时摘要推送。

动态 Dashboard 的触发条件：

- 静态页面无法满足筛选和检索。
- tag 查询和事件详情浏览成为高频需求。
- 需要长期历史趋势图。

## 14. pgvector 召回

V2 的 pgvector 定位是增强候选召回，而不是替代事件归并决策。

推荐流程：

1. 为 content/event 生成 embedding。
2. 写入 `content_embedding` / `event_embedding`。
3. `IEventCandidateService` 增加 vector recall 实现。
4. vector recall 返回候选事件及相似度。
5. 与规则召回结果合并、去重、排序。
6. 最终是否归并仍由现有规则和 Cluster LLM 判断。

规则召回必须保留为 fallback：

- embedding 服务不可用时仍可运行。
- pgvector 查询失败时不阻塞整轮 fetch。
- 便于对比 vector recall 是否改善漏召。

索引建议：

- 默认使用 HNSW。
- cosine 相似度使用 `vector_cosine_ops`。
- embedding 维度固定后再建索引。
- 记录 embedding model/version，避免不同模型向量混用。

## 15. 二次归并

V1 在线归并偏保守，宁可拆开也避免误合并。V2 可以通过二次归并修复拆得过细的问题。

二次归并应在 pgvector 召回之后实现，因为它依赖更好的候选发现能力。

二次归并流程：

1. 选择活跃或近期 stale 事件。
2. 通过 vector recall 找相似事件对。
3. 使用规则硬过滤明显冲突的事件。
4. 对高相似候选调用 LLM 判断是否应合并。
5. 写入 `event_merge_history`。
6. 将 source event 的 active evidence 迁移到 target event，或标记 source event 为 merged。
7. 重新计算 target event 的 summary、tags、score。

误合并保护：

- 不删除原始 content item。
- 不硬删除 source event。
- 保留 merge history。
- 支持后续人工排查和可能的拆分。

## 16. Dashboard 与 Grafana

Dashboard 和 Grafana 不属于 V2 初期主线，但 PostgreSQL 第一阶段会为它们打基础。

Grafana 适合展示：

- fetch run 成功率
- 每源失败率
- LLM token 和成本趋势
- 每日事件数
- 推送次数
- 重要事件分数分布

动态 Dashboard 适合展示：

- tag 云
- 事件检索
- source 过滤
- 事件详情页
- 热度趋势图

建议顺序：先静态报告，再 Grafana 指标面板，最后再考虑自建动态 Dashboard。

## 17. 开发阶段

### Phase 1: PostgreSQL 主库迁移

- 引入 PostgreSQL 配置。
- 引入 Npgsql/Dapper。
- 建立 SQL migration 机制。
- 设计并创建核心表。
- 实现 PostgreSQL persistence。
- 替换 LiteDB 初始化与仓储注册。
- 保持现有 fetch/match/score/push 行为不变。

验收标准：

- `validate` 可校验 PostgreSQL 配置。
- `fetch-once` 可写入 PostgreSQL。
- 事件归并、评分、推送测试通过。
- 不依赖 LiteDB 文件。

### Phase 2: 可观测性与 LLM usage

- 扩展 `fetch_run_source` 和 `fetch_run_stage`。
- 新增 `llm_usage`。
- LLM 固定 3 次重试。
- FetchJob 完成时输出成本汇总。

验收标准：

- 每轮 fetch 可看到阶段耗时。
- 每次 LLM 调用可追踪 token、耗时、成本和错误。
- LLM 失败仍按现有降级策略继续运行。

### Phase 3: Source 抽象与 DailyHotApi

- 引入 source registry。
- 抽象 source capability。
- 实现 DailyHotApi adapter。
- 支持 ranked source 与 flash source。
- NewsNow 快讯 source 走 flash 语义。

验收标准：

- NewsNow ranked source 行为不回退。
- DailyHotApi ranked source 可入库并评分。
- flash source 不依赖 rank 也能参与事件发现。

### Phase 4: Tag 与静态报告

- 新增 tag/event_tag。
- 规则或 LLM 生成 tag。
- 生成静态 HTML 摘要。
- 摘要推送可附带报告路径或链接。

验收标准：

- 每个摘要报告可打开查看。
- 每个事件展示相关新闻列表和原文链接。
- tag 可在报告中展示。

### Phase 5: pgvector 召回

- 新增 embedding 表。
- 接入 embedding 生成。
- 实现 vector candidate recall。
- 与规则召回合并。
- 增加召回质量回归样本。

验收标准：

- vector recall 失败时规则召回可继续工作。
- 候选召回命中率相对 V1 有可观测改善。
- LLM 调用量不会因候选爆炸失控。

### Phase 6: 二次归并

- 新增 event_merge_history。
- 实现相似事件对发现。
- 增加二次归并 LLM 判定。
- 保留 lineage 和 evidence 变更记录。

验收标准：

- 可合并明显拆分的重复事件。
- 误合并有可追踪原因。
- 原始 content item 不丢失。

### Phase 7: Grafana / Dashboard

- Grafana 查询指标视图。
- 可选动态 Dashboard。
- tag 云、事件检索、source 过滤。

验收标准：

- 可以查看运行健康度和成本趋势。
- 可以按 tag/source 浏览事件。

## 18. 测试策略

### 18.1 单元测试

继续覆盖：

- enrichment policy
- candidate recall
- event matcher
- event scoring
- blacklist
- repeat push
- digest idempotency

新增覆盖：

- flash source scoring
- tag 生成规则
- source capability 判断
- LLM usage cost calculation
- vector recall result merge
- secondary merge hard filters

### 18.2 集成测试

新增 PostgreSQL 集成测试：

- schema migration 可重复执行。
- content upsert 和 snapshot 写入。
- event item 唯一约束。
- push log dedup。
- app state upsert。
- fetch run stage/source telemetry。

如本地测试环境无法稳定提供 PostgreSQL，可先将仓储测试设计为可选 integration profile，但 CI 最终应覆盖 PostgreSQL 主路径。

### 18.3 回归样本

扩展现有 regression corpus：

- ranked news merge
- ranked news no-merge
- stale reactivation
- flash multi-source merge
- topic/noise no-merge
- blacklist
- push dedup
- secondary merge
- tag generation

## 19. 风险与开放问题

### 19.1 风险

1. PostgreSQL schema 过早复杂化。
2. Source 类型抽象过度，影响第一阶段交付。
3. Flash source 与 ranked source 混合评分导致误推送。
4. Tag 体系膨胀，生成大量低质量 tag。
5. pgvector 召回带来候选爆炸，增加 LLM 成本。
6. 二次归并误合并重要事件。
7. 静态报告演化成过早的动态 Web 项目。

### 19.2 缓解策略

1. PostgreSQL 第一阶段只迁主链路核心表和必要预留表。
2. Source capability 只定义 V2 确认需要的三类：ranked、flash、topic。
3. Flash scoring 单独计算，不伪造 rank。
4. Tag 初期只展示和检索，不参与强推送策略。
5. pgvector 只负责召回候选，最终归并仍由规则和 LLM 决策。
6. 二次归并保留 merge history，不删除原始事件和内容。
7. 先做静态报告，动态 Dashboard 后置。

### 19.3 开放问题

1. PostgreSQL schema migration 工具最终选择。
2. PostgreSQL 本地开发环境是否使用 Docker Compose。
3. DailyHotApi 的部署方式：使用公共实例、自部署，还是两者都支持。
4. Flash source 的时间窗口默认值。
5. Source 权重是否进入 V2 初期评分。
6. Tag taxonomy 是否需要用户手动配置种子 tag。
7. Embedding 模型选择和成本预算。
8. 静态报告输出目录和访问方式。

## 20. 建议实施顺序

最终建议顺序：

1. PostgreSQL 主库迁移。
2. 可观测性与 LLM usage/cost。
3. Source 抽象、DailyHotApi 和快讯支持。
4. Tag 与静态摘要页面。
5. pgvector 召回。
6. 二次归并。
7. Grafana / Dashboard。

这个顺序的核心理由是：当前没有历史数据包袱，PostgreSQL 先行可以减少后续重复实现；但 Dashboard、二次归并和 tag 推送策略仍应后置，避免 V2 初期范围失控。

## 21. 结论

V2 应以 PostgreSQL 数据平台为第一步，从全新数据开始重新组织持久化模型。这样可以直接承载 source、tag、LLM usage、report、embedding 和 merge history 等 V2 概念，避免先扩展 LiteDB 再迁移造成技术债。

同时，V2 不应演变成一次性大重写。现有 Core 业务规则、FetchJob 主链路、事件归并和评分模型仍然是稳定资产。正确的演进方式是在 PostgreSQL 基础上逐步扩展：先增强可观测性，再扩展 source，再提升输出体验，最后引入 pgvector 和二次归并。
