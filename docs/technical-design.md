# TrendReporter2 V1 技术设计稿

## 1. 文档目标

本文档用于将 [v1-design.md](v1-design.md) 中的产品方案落到具体工程实现，覆盖以下内容：

- 进程形态与模块划分
- 配置结构与运行时调度
- LiteDB 数据模型与索引
- NewsNow、Tavily、LLM、Unipush 的接入边界
- 事件归并、评分、推送判定的实现方案
- 事件发展进程摘要的生成与复用方案
- 失败重试、幂等、日志与测试策略

本文档面向 V1，优先保证：

- 单机可稳定运行
- 逻辑可解释、易调参
- 后续能扩展到社交平台“话题趋势”

## 2. 设计原则

### 2.1 单进程优先

V1 为个人使用工具，采用单进程后台服务即可，不引入消息队列、分布式任务系统、独立数据库服务。

### 2.2 事件优先于新闻

抓取、入库以新闻为基础，但分析、评分、推送、摘要均以事件为核心对象。

### 2.3 外部依赖全部可替换

NewsNow、Tavily、LLM、Unipush 全部放在明确的适配器接口之后，后续可以平滑替换为其他实现。

### 2.4 先规则后模型

V1 不做复杂机器学习模型，采用：

- 规则门槛做资格判定
- LLM 辅助归并与重要性修正
- 连续热度分做排序

### 2.5 每轮运行都必须幂等

无论抓取、增强、归并、评分还是推送，重复运行不能导致脏数据和重复通知。

### 2.6 高成本依赖按需触发

V1 需要显式控制外部成本：

- Tavily 只在“文本信息不足且首轮召回偏弱”时调用
- 高成本远端 LLM 只做归并判定和少量重要性修正
- 简单摘要、进程润色优先使用本地小模型或模板化回退

## 3. 技术栈与运行方式

### 3.1 技术栈

- 运行时：`.NET 8`
- 数据库：`LiteDB`
- JSON：`Newtonsoft.Json`
- YAML：`YamlDotNet`
- HTTP：`HttpClientFactory`
- 日志：`Microsoft.Extensions.Logging`
- 后台服务：`Generic Host + BackgroundService`

### 3.2 运行方式

V1 推荐实现为一个长期运行的控制台/后台程序：

- 启动后加载配置
- 初始化数据库与索引
- 启动抓取调度循环
- 启动摘要调度循环
- 所有任务在同一进程内运行

不建议使用系统 `cron` 管理抓取和摘要，因为：

- 抓取周期与摘要时刻都已在 YAML 内配置
- 事件复活、重复推送、摘要去重都依赖应用内部状态
- 单进程更容易控制“不重叠执行”和幂等

## 4. 总体架构

### 4.1 进程内模块

建议按以下逻辑拆分模块：

| 模块 | 责任 |
| --- | --- |
| `Host` | 启动程序、加载依赖、管理生命周期 |
| `Configuration` | 读取并校验 YAML 配置 |
| `Scheduler` | 调度抓取任务和摘要任务 |
| `NewsSource` | 抓取 NewsNow 数据并标准化 |
| `Repository` | 负责 LiteDB 集合访问与索引 |
| `Enrichment` | 调用 Tavily 补正文摘要 |
| `EventMatching` | 候选召回、LLM 归并、事件更新 |
| `Scoring` | 计算热度分与综合得分 |
| `EventProgress` | 生成事件阶段判断、关键进展节点与发展进程摘要 |
| `PushDecision` | 判断首次推送、重复推送、复活推送 |
| `Digest` | 生成早晚事件列表摘要 |
| `Pusher` | 抽象推送通道，V1 先实现 Unipush |
| `Observability` | 日志、指标、运行状态记录 |

### 4.2 逻辑流程

主链路如下：

```text
Scheduler
  -> FetchJob
    -> NewsNowClient
    -> ContentIngestService
    -> EventCandidateService (Pass1)
    -> EnrichmentService (Conditional)
    -> EventCandidateService (Pass2, Optional)
    -> EventMatchingService
    -> EventScoringService
    -> EventProgressService
    -> PushDecisionService
    -> PushDispatcher
```

摘要链路如下：

```text
Scheduler
  -> DigestJob
    -> DigestQueryService
    -> EventProgressService
    -> DigestComposer
    -> PushDispatcher
```

## 5. 建议项目结构

V1 可以先做成单解决方案下的 3 个项目，既保留边界，又不过度工程化。

```text
TrendReporter2.sln
  src/
    TrendReporter2.App/
    TrendReporter2.Core/
    TrendReporter2.Infrastructure/
  docs/
    idea.md
    v1-design.md
    technical-design.md
```

职责建议：

- `TrendReporter2.App`
  程序入口、DI、配置加载、后台任务注册
- `TrendReporter2.Core`
  领域模型、配置模型、服务接口、核心算法
- `TrendReporter2.Infrastructure`
  LiteDB、HTTP 客户端、LLM/Tavily/Unipush 实现

如果你希望 V1 更快落地，也可以先只建一个项目，但命名空间仍按 `Core / Infrastructure / Jobs` 分层。

## 6. 配置设计

### 6.1 配置目标

配置需满足以下要求：

- 所有运行参数均可通过 YAML 调整
- 不需要修改代码即可切换新闻源、抓取频率、摘要时刻、评分阈值
- LLM 归并模型与判定模型可独立配置
- Tavily 调用条件可控
- 黑名单和推送规则可调

### 6.2 建议配置结构

在现有 [config.example.yaml](../config.example.yaml) 基础上，建议演进为：

```yaml
newsNow:
  baseUrl: ""
  sources:
    china: []
    tech: []
    world: []
    finance: []
    social: []

database:
  path: "./data/trend.db"

analysis:
  fetchInterval: 3600
  historyHours: 24
  push:
    pushTime:
      - "09:20"
      - "18:20"
    pushCount: 5
  event:
    sourceCount: 3
    normalizedRankThreshold: 0.75
    trendWindowHours: 6
    staleHours: 24
    archiveRecallDays: 30
    candidateLimit: 20
    mergeThreshold: 0.82
    staleMergeThreshold: 0.88
    minTrendSamples: 3
    minTrendHeat: 1.5
  repeatPush:
    sourceAddThreshold: 2
    rankScoreImproveThreshold: 0.15
    scoreImproveThreshold: 12

llm:
  cluster:
    baseUrl: ""
    apiKey: ""
    model: ""
    maxTokens: 2048
  judge:
    baseUrl: ""
    apiKey: ""
    model: ""
    maxTokens: 2048
  writer:
    mode: "local"
    baseUrl: "http://127.0.0.1:11434/v1"
    apiKey: ""
    model: ""
    maxTokens: 1024

tavily:
  apiKey: ""
  enabledSources: []
  maxRequestsPerRun: 5
  minTitleLength: 14
  onlyWhenRecallWeak: true
  recallWeakScoreThreshold: 0.35
  retryCooldownHours: 12

filters:
  blacklistKeywords: []

system:
  timeZone: "Asia/Shanghai"
  maxParallelFetch: 4
  maxParallelEnrichment: 2
  maxParallelLlm: 2
```

### 6.3 关键配置说明

- `analysis.fetchInterval`
  每轮抓取间隔，单位秒
- `analysis.push.pushTime`
  摘要触发时刻数组，不使用 cron
- `analysis.event.normalizedRankThreshold`
  重要事件资格判定使用的源内归一化排名阈值，范围 `0-1`
- `analysis.event.trendWindowHours`
  趋势计算窗口，单位小时
- `analysis.event.archiveRecallDays`
  陈旧事件召回窗口，避免“旧事件后续”被切成新事件
- `analysis.event.mergeThreshold`
  活跃事件直接归并阈值
- `analysis.event.staleMergeThreshold`
  陈旧事件/复活事件归并阈值，应高于普通归并阈值
- `analysis.event.minTrendSamples`
  趋势判定所需的最少样本数
- `analysis.event.minTrendHeat`
  趋势判定所需的最小累计热度，避免低样本噪声
- `tavily.enabledSources`
  指定必须做正文增强的信源
- `tavily.minTitleLength`
  标题信息不足的简单启发式阈值
- `tavily.recallWeakScoreThreshold`
  首轮召回分过低时，才允许触发 Tavily
- `llm.writer`
  本地小模型配置，用于简单摘要与进程润色
- `system.timeZone`
  统一抓取与摘要调度时区

## 7. 数据模型设计

### 7.1 集合总览

LiteDB 建议包含以下集合：

- `content_item`
- `content_snapshot`
- `event`
- `event_item`
- `event_score_snapshot`
- `push_log`
- `fetch_run`
- `app_state`

`fetch_run` 和 `app_state` 虽然未在产品稿中强调，但在技术实现上非常有价值：

- `fetch_run` 用于记录每轮抓取状态、错误和耗时
- `app_state` 用于摘要去重和轻量级运行时状态保存

### 7.2 `content_item`

原始新闻条目，按“新闻实体”去重。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键，建议使用 `Guid` 或 `Ulid` |
| `Source` | `string` | 信源标识，如 `ifeng` |
| `Category` | `string` | 来源分类，如 `china` |
| `Type` | `string` | 内容类型，V1 固定为 `News` |
| `SourceItemId` | `string` | NewsNow 返回的新闻唯一标识 |
| `Title` | `string` | 原始标题 |
| `Url` | `string` | 原始链接 |
| `MobileUrl` | `string?` | 移动链接 |
| `PubTime` | `DateTimeOffset?` | 原始发布时间 |
| `HoverText` | `string?` | NewsNow 悬浮信息 |
| `Summary` | `string?` | 增强后的摘要 |
| `SummarySource` | `string?` | `TitleOnly` / `Tavily` / `LocalLlm` / `JudgeLlm` |
| `NeedEnrichment` | `bool` | 是否具备增强候选资格 |
| `EnrichmentStatus` | `string` | `None` / `Pending` / `Succeeded` / `Failed` / `Skipped` |
| `EnrichmentTriedAt` | `DateTimeOffset?` | 最近一次尝试 Tavily 的时间 |
| `CreatedAt` | `DateTimeOffset` | 首次入库时间 |
| `UpdatedAt` | `DateTimeOffset` | 最后更新时间 |
| `RawPayload` | `string` | 原始 JSON |

唯一索引建议：

- `(Source, SourceItemId)`

普通索引建议：

- `Category`
- `CreatedAt`
- `NeedEnrichment`
- `EnrichmentStatus`

### 7.3 `content_snapshot`

记录每次抓取时这条新闻在榜单中的位置。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键 |
| `RunId` | `string` | 关联 `fetch_run` |
| `ContentItemId` | `string` | 关联 `content_item` |
| `CapturedAt` | `DateTimeOffset` | 抓取时间 |
| `Source` | `string` | 信源 |
| `Category` | `string` | 分类 |
| `Rank` | `int` | 排名，1 为最热 |
| `SourceListSize` | `int` | 当次榜单总长度 |
| `NormalizedRankScore` | `double` | 源内归一化排名分，范围 `0-1` |

唯一索引建议：

- `(ContentItemId, CapturedAt)`

普通索引建议：

- `RunId`
- `Source`
- `CapturedAt`

### 7.4 `event`

统一事件表，V1 与未来话题能力共用。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键 |
| `Type` | `string` | `NewsEvent` / `Topic` |
| `CanonicalTitle` | `string` | 事件主标题 |
| `Summary` | `string` | 事件摘要 |
| `Aliases` | `string[]` | 常见别名或同义表述 |
| `Entities` | `string[]` | 事件主体实体，用于召回与归并 |
| `Places` | `string[]` | 事件地点锚点 |
| `KeyTerms` | `string[]` | 核心关键词 |
| `RepresentativeTitles` | `string[]` | 最近 1-3 条代表新闻标题 |
| `CurrentStage` | `string?` | 当前发展阶段，如 `Initial` / `Expanding` / `Escalating` / `FollowUp` / `Cooling` |
| `ProgressSummary` | `string?` | 面向推送的事件发展进程摘要 |
| `Milestones` | `EventMilestone[]` | 最近 3-5 个关键发展节点 |
| `ProgressUpdatedAt` | `DateTimeOffset?` | 最近一次进程摘要更新时间 |
| `Status` | `string` | `Active` / `Stale` |
| `FirstSeenAt` | `DateTimeOffset` | 首次出现时间 |
| `LastSeenAt` | `DateTimeOffset` | 最后一次被新闻命中时间 |
| `LastActivatedAt` | `DateTimeOffset` | 最近一次复活时间 |
| `LastPushedAt` | `DateTimeOffset?` | 最近推送时间 |
| `PushCount` | `int` | 总推送次数 |
| `LastPushScore` | `double?` | 最近一次推送时综合分 |
| `LastPushRankScore` | `double?` | 最近一次推送时归一化排名分 |
| `LastPushSourceCount` | `int?` | 最近一次推送时信源数 |
| `IsBlacklisted` | `bool` | 是否命中黑名单 |
| `BlacklistReason` | `string?` | 黑名单命中原因 |
| `CreatedAt` | `DateTimeOffset` | 创建时间 |
| `UpdatedAt` | `DateTimeOffset` | 更新时间 |

索引建议：

- `Status`
- `Type`
- `LastSeenAt`
- `IsBlacklisted`
- `UpdatedAt`

### 7.5 `event_item`

事件与新闻项的映射。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键 |
| `EventId` | `string` | 关联事件 |
| `ContentItemId` | `string` | 关联新闻 |
| `Confidence` | `double` | 归并置信度 |
| `MatchedAt` | `DateTimeOffset` | 归并时间 |
| `MatchReason` | `string?` | LLM 返回的原因摘要 |

唯一索引建议：

- `(EventId, ContentItemId)`
- `ContentItemId`

普通索引建议：

- `ContentItemId`
- `MatchedAt`

### 7.6 `event_score_snapshot`

每轮分析的评分结果，用于趋势判断、摘要排序和追溯“为什么推了”。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键 |
| `EventId` | `string` | 关联事件 |
| `RunId` | `string` | 关联 `fetch_run` |
| `CalculatedAt` | `DateTimeOffset` | 计算时间 |
| `CoverageScore` | `double` | 覆盖度分 |
| `RankScore` | `double` | 排名分 |
| `TrendScore` | `double` | 趋势分 |
| `PersistenceScore` | `double` | 持续性分 |
| `LlmBoostScore` | `double` | LLM 修正分 |
| `ReactivationBonus` | `double` | 复活加分 |
| `TotalScore` | `double` | 总分 |
| `UniqueSourceCount` | `int` | 不同信源数 |
| `AvgRank` | `double` | 原始平均排名，便于调试解释 |
| `AvgNormalizedRank` | `double` | 源内归一化后的平均排名分 |
| `HeatValue` | `double` | 当次热度分 |
| `SmoothedHeatValue` | `double` | EWMA 平滑后的热度分 |
| `TrendEvidenceCount` | `int` | 趋势判定实际使用的样本数 |
| `CurrentStage` | `string?` | 本轮判断的事件阶段 |
| `TriggerReasons` | `string[]` | 命中的规则 |

索引建议：

- `EventId`
- `RunId`
- `CalculatedAt`
- `TotalScore`

### 7.7 `push_log`

推送幂等与历史记录。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键 |
| `EventId` | `string?` | 即时推送关联的事件 |
| `PushType` | `string` | `Instant` / `Digest` |
| `PushedAt` | `DateTimeOffset` | 推送时间 |
| `Title` | `string` | 推送标题 |
| `Payload` | `string` | 实际发送 JSON |
| `DedupKey` | `string` | 幂等键 |
| `Success` | `bool` | 是否推送成功 |
| `Error` | `string?` | 失败原因 |

唯一索引建议：

- `DedupKey`

普通索引建议：

- `EventId`
- `PushType`
- `PushedAt`

### 7.8 `fetch_run`

每轮抓取的执行记录。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 主键 |
| `StartedAt` | `DateTimeOffset` | 开始时间 |
| `FinishedAt` | `DateTimeOffset?` | 结束时间 |
| `Status` | `string` | `Running` / `Succeeded` / `Failed` / `Partial` |
| `SourceCount` | `int` | 总信源数 |
| `SuccessSourceCount` | `int` | 成功信源数 |
| `FailureSourceCount` | `int` | 失败信源数 |
| `FetchedItemCount` | `int` | 抓取总条数 |
| `EnrichedItemCount` | `int` | Tavily 增强条数 |
| `MatchedEventCount` | `int` | 命中/更新事件数 |
| `PushedEventCount` | `int` | 即时推送事件数 |
| `Errors` | `string[]` | 错误摘要 |

索引建议：

- `StartedAt`
- `Status`

### 7.9 `app_state`

保存轻量级应用状态。

建议字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Key` | `string` | 状态键 |
| `Value` | `string` | 序列化值 |
| `UpdatedAt` | `DateTimeOffset` | 更新时间 |

典型用途：

- 记录某个摘要时刻今天是否已执行
- 记录上次成功抓取时间
- 记录调度器内部游标

## 8. 核心领域模型

建议在 `Core` 中定义以下对象：

- `NewsItem`
- `ContentItem`
- `ContentSnapshot`
- `EventAggregate`
- `EventProfile`
- `EventMilestone`
- `EventProgress`
- `EventScore`
- `FetchRun`
- `PushMessage`
- `DigestResult`
- `AppConfig`

建议定义以下枚举：

- `EventType`
- `EventStatus`
- `PushType`
- `SummarySourceType`
- `RunStatus`

## 9. 服务与接口设计

### 9.1 抓取相关

#### `INewsSourceClient`

职责：

- 从外部来源抓取新闻列表
- 标准化为内部 `NewsItem`

建议接口：

```csharp
public interface INewsSourceClient
{
    Task<IReadOnlyList<NewsItem>> FetchAsync(string category, string source, CancellationToken ct);
}
```

V1 实现：

- `NewsNowClient`

#### `IFetchJob`

职责：

- 执行一次完整抓取流程

建议接口：

```csharp
public interface IFetchJob
{
    Task RunAsync(CancellationToken ct);
}
```

### 9.2 入库与增强

#### `IContentIngestService`

职责：

- 将抓取结果写入 `content_item`
- 生成 `content_snapshot`
- 标记是否需要正文增强

#### `IEnrichmentService`

职责：

- 结合标题信息不足标记与首轮召回结果，判断哪些内容需要增强
- 在预算、冷却时间和去重约束下调用 Tavily
- 写回摘要与来源

### 9.3 事件归并

#### `IEventCandidateService`

职责：

- 为新闻召回候选事件

输入：

- 当前新闻标题
- 当前摘要
- 当前新闻抽取出的实体/地点/时间锚点
- 时间窗口

输出：

- 带粗召回分和命中特征的候选事件列表，数量建议限制为 `10-20`

#### `IEventMatcher`

职责：

- 基于候选事件与当前新闻调用 LLM，判断是否归并到既有事件
- 若不能归并，则创建新事件

#### `IClusterLlmClient`

职责：

- 调用归并模型
- 强制返回结构化 JSON

建议输出契约：

```json
{
  "decision": "same_event",
  "eventId": "evt_xxx",
  "canonicalTitle": "string",
  "summary": "string",
  "confidence": 0.91,
  "reason": "string"
}
```

### 9.4 评分与判定

#### `IEventScoringService`

职责：

- 读取事件近期快照
- 计算热度分和总分
- 写入 `event_score_snapshot`

#### `IEventProgressService`

职责：

- 基于事件的新闻、快照和评分结果生成“发展进程”
- 判断事件当前阶段
- 提炼 3-5 个关键节点
- 生成可直接用于推送的进程摘要
- 将结果写回 `event`

#### `IJudgeLlmClient`

职责：

- 调用重要性判定模型
- 输出重要性修正和标签

建议输出契约：

```json
{
  "importance": "high",
  "boostScore": 0.15,
  "labels": ["breaking", "policy"],
  "reason": "string"
}
```

#### `IWriterLlmClient`

职责：

- 使用本地小模型对结构化摘要做低成本润色
- 为事件摘要、发展进程摘要提供自然语言版本
- 模型不可用时允许直接回退模板输出

#### `IPushDecisionService`

职责：

- 判断是否首次推送
- 判断是否重复推送
- 判断是否属于复活推送

### 9.5 推送与摘要

#### `IPusher`

职责：

- 发送统一格式的推送消息

建议接口：

```csharp
public interface IPusher
{
    string Type { get; }
    Task PushAsync(PushMessage message, CancellationToken ct);
}
```

V1 实现：

- `UnipushPusher`

#### `IDigestJob`

职责：

- 在指定时刻生成事件摘要
- 选择前 `N` 个事件并组装带发展进程的推送消息

## 10. 外部依赖适配

### 10.1 NewsNow

使用 `GET /api/s?id=source` 拉取榜单。

实现建议：

- `baseUrl` 统一从配置读取
- 每个信源单独请求
- 单个信源失败不应阻塞整轮任务
- 保留原始 JSON 到 `content_item.RawPayload`

### 10.2 Tavily

V1 只将 Tavily 作为“正文抓取与摘要增强”能力，不让其参与评分或推送判定。

实现建议：

- 只有在“标题信息不足”且“首轮召回分不足”时才调用
- 增强前先做去重和冷却控制，避免同一条新闻重复请求
- 失败后记录日志，不阻塞主流程
- 对超时、限流、付费额度不足做好降级

由于当前仓库未包含 Tavily 文档，建议在实现时定义内部抽象：

```csharp
public interface ITavilyClient
{
    Task<EnrichmentResult?> EnrichAsync(ContentItem item, CancellationToken ct);
}
```

### 10.3 LLM

V1 中 LLM 分为三类：

- `Cluster` 模型：做事件归并
- `Judge` 模型：做重要性修正与标签判断，仅对高价值候选事件调用
- `Writer` 模型：优先使用本地小模型，做摘要补足和发展进程润色

实现建议：

- 三类模型独立配置
- 使用 OpenAI 兼容接口
- `Cluster` 和 `Judge` 要求 `response_format=json` 或等价约束
- `Writer` 可以使用普通文本输出，但输入应尽量结构化
- 所有输出必须经过 JSON schema 校验或手工校验

### 10.4 Unipush

实现时统一生成：

```json
{
  "cate": "default",
  "title": "string",
  "msg": "string",
  "link": "string"
}
```

请求规则：

- `POST {url}?channels={channels}`
- Header: `Push-Key: {secret}`

## 11. 调度设计

### 11.1 抓取调度

使用一个 `BackgroundService` 维护循环：

1. 启动后立即执行一次抓取
2. 之后每隔 `fetchInterval` 秒触发一轮
3. 若上一轮尚未完成，则本轮跳过并记录日志

实现建议：

- 使用 `PeriodicTimer`
- 使用进程内 `SemaphoreSlim` 防止重入

### 11.2 摘要调度

摘要使用另一个 `BackgroundService`：

1. 每分钟检查一次当前本地时间
2. 若命中 `pushTime` 中的某个时刻，则尝试执行摘要任务
3. 通过 `app_state` 判定当日该时刻是否已执行，避免重启后重复发送

建议摘要幂等键：

```text
digest:{yyyy-MM-dd}:{HH:mm}
```

### 11.3 时区处理

所有调度、统计窗口和推送时间一律以 `system.timeZone` 为准。  
数据库落库建议保存 `DateTimeOffset`，避免时区丢失。

## 12. 关键算法设计

### 12.1 何时调用 Tavily

V1 采用“两阶段按需增强”：

1. 入库时先做低成本判断，只标记 `NeedEnrichment = true`，不立即调用 Tavily
2. 先基于 `Title + HoverText + 已有 Summary` 做一轮候选召回
3. 只有同时满足以下条件时，才实际调用 Tavily：
   - `NeedEnrichment = true`
   - 首轮召回最高分低于 `tavily.recallWeakScoreThreshold`
   - 当前条目未在冷却窗口内尝试过 Tavily
   - 本轮 Tavily 调用数未超 `maxRequestsPerRun`
4. Tavily 成功后更新摘要，再执行第二轮候选召回

建议的低成本标记条件：

- 标题长度小于 `tavily.minTitleLength`
- 标题包含明显省略式表述，如“详情”“来了”“突发”“更新中”
- 当前信源位于 `tavily.enabledSources`
- 标题不包含可识别主体，且 `hover` 信息也不足

简单文本摘要优先由本地 `Writer` 模型生成；若本地模型不可用，则退回模板化摘要。

### 12.2 候选事件召回

V1 不引入向量库，采用“倒排关键词 + 规则特征”的分层召回：

1. 先构建当前新闻的轻量画像：
   - 标题标准化结果
   - `HoverText` / 摘要中的关键词
   - 机构、人名、地点、时间、数字等锚点
2. 召回最近 `historyHours` 内的 `Active` 事件
3. 再召回最近 `archiveRecallDays` 天内的 `Stale` 事件，用于识别“旧事件后续”
4. 额外补充“同源近重复快捷命中”：
   - 同一 `Source + SourceItemId`
   - 同一 URL
   - 高相似标题对应的既有事件
5. 对召回结果做粗打分并保留最多 `candidateLimit` 个候选

建议粗召回特征：

- 标题 token overlap
- 字符 2/3-gram Jaccard 相似度
- 实体重合度
- 地点/时间/数字锚点一致性
- 代表标题命中度
- 发布时间距离

建议先做硬过滤，以下情况直接降权或剔除：

- 核心实体明显不重合
- 时间、地点或关键数字明显冲突
- 陈旧事件已超过 `archiveRecallDays` 且无别名/实体命中

### 12.3 事件归并

归并流程：

1. 用候选召回缩小范围
2. 将当前新闻与候选事件材料喂给 `Cluster` 模型
3. `Cluster` 模型返回四类结果之一：
   - `same_event`
   - `follow_up`
   - `related_but_distinct`
   - `unrelated`
4. 若结果为 `same_event` 且 `confidence >= mergeThreshold`，归并到已有事件
5. 若结果为 `follow_up` 且 `confidence >= staleMergeThreshold`，并且实体/别名至少命中一项，则归并到既有事件并标记复活
6. 其他情况创建新事件

对复活事件的判断：

- 若命中的既有事件 `LastSeenAt` 距当前超过 `staleHours`
- 则更新其 `Status = Active`
- 记录 `LastActivatedAt`
- 在后续推送阶段打上“旧事件后续”标签

V1 不实现独立的事件二次归并任务；因此在线归并应偏保守，宁可先拆开，也不要把两个不同事件误并到一起。

### 12.4 热度分

单次抓取热度分：

```text
normalizedRankScore = 1 - (rank - 1) / max(sourceListSize - 1, 1)
HeatValue = Σ(normalizedRankScore)
```

说明：

- 同一事件在多个信源上同时出现，热度自然累加
- 排名越靠前，贡献越高
- 不同信源榜单长度不同，因此先做源内归一化再累加
- V1 默认所有信源等权，后续版本再引入信源权重

### 12.5 资格判定

满足以下任一条件即进入重要事件候选集：

- `UniqueSourceCount >= sourceCount && AvgNormalizedRank >= normalizedRankThreshold`
- `TrendEvidenceCount >= minTrendSamples` 且窗口内累计热度 `>= minTrendHeat`，并且趋势分显著上升
- 事件属于陈旧复活事件

### 12.6 趋势分

建议算法：

1. 取最近 `trendWindowHours` 小时的热度样本，并按小时补齐空桶
2. 若样本数不足 `minTrendSamples`，直接视为趋势证据不足
3. 对热度序列做 EWMA 平滑，减少单次抓取抖动
4. 将平滑后的序列拆成前半窗和后半窗
5. 若后半窗平均热度明显高于前半窗，认定整体升温
6. 差值越大，趋势分越高

示例公式：

```text
trendScore = clamp((ewmaRecent - ewmaPast) / max(ewmaPast, 0.2), 0, 1)
```

### 12.7 综合评分

```text
TotalScore = 100 * (
  0.35 * coverageScore +
  0.25 * rankScore +
  0.20 * trendScore +
  0.10 * persistenceScore +
  0.10 * llmBoostScore
) + reactivationBonus
```

各分项建议归一化到 `0-1`。

补充建议：

- `coverageScore` 使用“饱和式增长”，避免单一大事件因信源数极多无限抬高
- `rankScore` 基于 `AvgNormalizedRank` 计算，而非原始平均排名
- `llmBoostScore` 只对已进入候选集或接近阈值的事件调用 `Judge` 模型，避免把高成本 LLM 用在明显低价值事件上

### 12.8 重复推送判定

若事件已推送过，再次推送需满足任一条件：

- 新增报道信源数 `>= sourceAddThreshold`
- 当前 `rankScore` 相较上次推送提升 `>= rankScoreImproveThreshold`
- 当前综合分相较上次推送提升 `>= scoreImproveThreshold`

建议保存以下基线：

- `LastPushSourceCount`
- `LastPushRankScore`
- `LastPushScore`

### 12.9 黑名单判定

黑名单以事件级生效。

实现建议：

1. 用 `CanonicalTitle + Summary` 拼接文本
2. 命中任一关键词则设置 `IsBlacklisted = true`
3. 继续入库和评分，但不进入推送和摘要输出

### 12.10 事件发展进程生成

目标：

- 让推送不仅告诉用户“发生了什么”，还要告诉用户“事情发展到哪一步了”

V1 建议将事件阶段统一为以下几类：

- `Initial`
  刚进入监控，报道源较少
- `Expanding`
  正在扩散到更多信源
- `Escalating`
  热度和排名同时快速改善
- `FollowUp`
  旧事件出现新进展，或陈旧事件复活
- `Cooling`
  仍被报道，但热度开始回落

建议生成流程：

1. 收集事件证据：
   - 首次出现时间
   - 最近一次出现时间
   - 首次被哪些信源报道
   - 信源扩散时间点
   - 热度峰值时间点
   - 是否发生复活
   - 最近一次新增报道或明显排名变化
2. 基于规则先判断当前阶段
3. 从证据中提取 3-5 个关键节点，形成 `Milestones`
4. 优先调用本地 `Writer` 模型将结构化节点润色为自然语言进程摘要
5. 若本地模型不可用，则回退为模板化摘要

关键节点建议格式：

```json
{
  "time": "2026-04-23T09:00:00+08:00",
  "kind": "heat_peak",
  "label": "热度快速上升",
  "source": "thepaper",
  "summary": "事件从少数报道扩散至多个信源，平均排名明显提升。"
}
```

模板化回退文案建议：

```text
先在{firstSeenTime}被{firstSource}等信源报道，随后扩散到{sourceCount}个信源，目前处于{currentStage}阶段，最新进展为{latestMilestone}。
```

推送和摘要输出建议：

- 即时推送展示“当前阶段 + 一句进程摘要”
- 定时摘要展示“当前阶段 + 2-3 个关键节点”
- 复活事件优先展示“旧事件后续”节点

## 13. 事务、幂等与并发控制

### 13.1 幂等原则

- 同一 `(Source, SourceItemId)` 只能存在一个 `content_item`
- 同一时刻的同一摘要只能发一次
- 同一事件的同一推送原因只能记一次 `push_log`
- 同一轮内同一事件的进程摘要只更新一次

### 13.2 推送幂等键

建议：

- 即时推送：`instant:{eventId}:{runId}:{reason}`
- 摘要推送：`digest:{yyyy-MM-dd}:{HH:mm}`

### 13.3 并发控制

V1 建议：

- 抓取可按信源有限并发
- Tavily/LLM 调用单独限流
- 同一轮事件分析串行执行

原因：

- 规模不大
- LiteDB 写入锁模型更适合低并发写
- 串行更方便保证事件更新的一致性

## 14. 错误处理与降级

### 14.1 抓取失败

- 单个信源失败只记入 `fetch_run.Errors`
- 其余信源继续执行
- 若全部信源失败，整轮标记为 `Failed`

### 14.2 Tavily 失败

- 保留 `TitleOnly` 作为摘要降级
- 不阻塞归并流程

### 14.3 LLM 失败

- 归并失败时，优先创建新事件，避免丢数据
- 判定失败时，`llmBoostScore = 0`
- 进程摘要生成失败时，退回模板化节点摘要
- 所有失败均需记录可追踪日志

### 14.4 推送失败

- 记录失败的 `push_log`
- V1 可先不自动重试
- 后续如有需要，可增加补发任务

## 15. 日志与可观测性

V1 不需要复杂监控系统，但至少应有可读日志。

建议日志粒度：

- 应用启动与配置摘要
- 每轮抓取开始/结束
- 每个信源抓取结果
- Tavily 增强次数与失败数
- 新建/更新/复活事件
- 事件阶段变化与进程摘要更新时间
- 推送触发原因
- 摘要发送结果

建议日志字段：

- `runId`
- `source`
- `eventId`
- `contentItemId`
- `pushType`
- `durationMs`

## 16. 测试策略

### 16.1 单元测试

优先覆盖：

- 标题增强判断
- 候选召回粗打分与硬过滤
- 热度分计算
- 趋势分计算
- 事件阶段判断
- 进程摘要模板回退
- 重要性资格判定
- 重复推送判定
- 黑名单过滤

### 16.2 集成测试

建议覆盖：

- NewsNow 响应到 `content_item/content_snapshot` 的落库
- Tavily 结果写回
- LLM 归并成功与失败分支
- Unipush 请求体生成

### 16.3 回归样本

建议积累一组真实新闻样本作为固定回归集，用于观察：

- 是否误合并
- 是否漏合并
- 是否重复推送过多

## 17. 开发顺序建议

建议按以下顺序实现：

1. 建立解决方案、配置模型、LiteDB 基础仓储
2. 打通 NewsNow 抓取与原始数据落库
3. 完成抓取调度器与 `fetch_run`
4. 实现 Tavily 增强链路
5. 实现事件表、候选召回、LLM 归并
6. 实现评分、趋势、重要性判定
7. 实现即时推送与重复推送控制
8. 实现摘要调度与事件列表摘要
9. 完成黑名单、日志、测试补齐

## 18. 待补充项

当前还有两项实现前需要补充或确认，但不影响本文档成立：

- Tavily 的具体 API 请求/响应字段，待查阅正式文档后补全
- 是否需要在配置中增加信源级权重，V1 可先统一权重，后续再扩展
- 事件二次归并任务与向量库召回均留到后续版本，V1 暂不实现

## 19. 结论

V1 技术方案采用：

- 单进程 `.NET 8` 后台服务
- `Generic Host + BackgroundService` 做调度
- `LiteDB` 做持久化
- `NewsNow + Tavily + 两类 LLM + Unipush` 组成外部依赖
- “规则门槛 + 热度分 + LLM 修正”作为事件判定与排序基础

这个方案足够轻量，能快速落地，也为后续加入“社交平台话题趋势”保留了扩展空间。
