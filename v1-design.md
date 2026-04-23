# TrendReporter2 V1 产品设计稿

## 1. 产品定位

TrendReporter2 是一个纯个人使用的舆论趋势分析工具。

V1 的核心目标不是做“新闻阅读器”，而是做“事件级趋势发现与推送系统”：

- 持续监控一组传统媒体新闻源
- 将多条相关新闻归并为“事件”
- 判断哪些事件值得立刻关注
- 在固定时间输出高价值事件摘要

一句话定义：

> 自动从传统媒体榜单中发现值得关注的重要事件，并持续追踪其热度变化与后续进展。

## 2. 已确认的产品边界

### 2.1 目标用户

- 仅供作者本人使用

### 2.2 V1 范围

- 只处理传统媒体/严肃媒体新闻源
- 新闻源全部抓取，分类字段仅用于标注新闻类型
- 基于事件而非单条新闻进行分析和推送
- 支持即时推送与定时摘要推送
- 支持关键词黑名单，黑名单命中的事件只记录，不推送
- 正文补充依赖 Tavily
- 事件归并与重要性判定依赖 LLM，且两个任务应支持使用不同模型

### 2.3 V1 非目标

- 不处理社交平台和社区类话题趋势
- 不做可视化前端界面
- 不做多用户、多订阅、多账号系统
- 不做情绪分析、立场分析、传播路径分析

## 3. 核心概念

### 3.1 内容项（Content Item）

一次抓取返回的单条新闻。它是原始事实记录，不直接面向用户。

### 3.2 事件（Event）

由若干条指向同一现实事件的新闻归并得到。事件是分析、评分、推送、摘要的核心对象。

### 3.3 事件类型（Event Type）

`event` 表统一承载“新闻事件”和未来可能引入的“社交话题”，通过 `Type` 字段区分。  
V1 只会实际使用 `NewsEvent`，但底层数据结构预留 `Topic` 类型扩展能力。

### 3.4 热度分（Heat Score）

热度分用于描述事件在某个时间点的综合关注度，来自于：

- 报道该事件的信源数量
- 各信源中的排名位置
- 最近若干次抓取中的变化趋势
- 持续活跃时长
- LLM 给出的重要性修正

## 4. 典型使用场景

### 4.1 即时发现大新闻

系统每轮抓取完成后都重新评估事件。如果某个事件首次满足重要事件条件，或者属于陈旧事件复活，立即推送。

### 4.2 获取早晚事件摘要

系统在配置的时间点生成“事件列表”摘要，只输出当前周期内最值得关注的前 N 个事件。

### 4.3 跟踪旧事件的新进展

如果一个事件超过 `staleHours` 未再被报道，则视为陈旧事件；其后再次被报道时，不新建事件，而是复活原事件，并在推送中标记“旧事件后续”。

## 5. 功能设计

### 5.1 定时抓取

- 按 `analysis.fetchInterval` 周期抓取 newsNow 的全部配置新闻源
- 每次抓取都保存原始新闻项及当次排名
- 同一新闻在不同时间被抓到，应保留多次排名快照

说明：

- 即时推送不等于实时流式推送，而是“每次抓取完成后立即判断一次”
- 默认抓取间隔为 1 小时，但应允许通过配置调整

### 5.2 新闻补充与摘要增强

问题：

- 某些信源标题过短、上下文不足，仅靠标题难以归并和摘要

V1 方案：

- 默认不对所有新闻调用 Tavily
- 仅对“标题信息不足”的新闻触发 Tavily 抓正文/摘要
- 如果“标题是否充分”难以稳定判断，可支持对指定信源强制启用 Tavily

建议配置扩展：

- `tavily.apiKey`
- `tavily.enabledSources`
- `tavily.maxRequestsPerRun`

### 5.3 事件归并

目标：

- 将多条指向同一事件的新闻归并到一个 `event`

策略：

- 先基于标题、摘要、时间窗口做候选召回
- 再交给 LLM 做归并辅助与核心判定
- 归并策略允许偏激进，并通过配置暴露阈值，便于后续调参

归并原则：

- 同一现实事件尽量合并
- 误合并容忍度高于漏合并
- 陈旧事件再次出现时复用原事件，不创建新事件

建议 LLM 输出结构：

```json
{
  "matched": true,
  "eventId": "optional-existing-event-id",
  "canonicalTitle": "string",
  "summary": "string",
  "confidence": 0.92,
  "reason": "string"
}
```

### 5.4 事件重要性判定

重要事件的资格判定来自配置中的两条主规则，满足任一即可：

1. 多信源同时出现且排名靠前
2. 最近若干小时热度分整体上升

同时补充一条特殊规则：

3. 陈旧事件复活时，立即触发推送，且在文案中标注“旧事件后续”

黑名单规则：

- 命中关键词黑名单的事件只记录、不推送

### 5.5 即时推送

首次推送触发条件：

- 事件首次满足重要事件资格判定
- 或事件为复活的陈旧事件

重复推送触发条件：

- 相比上次推送，新增信源数达到 `analysis.repeatPush.sourceAddThreshold`
- 或事件平均排名提升达到 `analysis.repeatPush.rankImproveThreshold`

推送原则：

- 不重复推送无明显变化的事件
- 允许同一事件在“明显升级”时再次推送

### 5.6 定时摘要推送

摘要推送基于 `analysis.push.pushTime` 触发。

输出规则：

- 只输出事件，不输出原始新闻列表
- 每次推送前 `analysis.push.pushCount` 个事件
- 排序依据为事件综合得分，而非单次榜单排名

建议事件摘要结构：

- 事件标题
- 一句话摘要
- 热度依据
- 代表信源
- 相关链接

## 6. 评分与排序设计

### 6.1 设计原则

事件评分涉及两件事：

- 判断事件是否值得进入“重要事件候选集”
- 对候选事件进行排序

但不建议用一个纯黑盒分数同时解决所有问题。  
V1 更稳妥的做法是：

- 先用规则门槛判断“有没有资格”
- 再用连续分数判断“排第几”

### 6.2 资格判定

事件具备重要性资格，当且仅当满足以下任一条件：

- `uniqueSourceCount >= sourceCount` 且 `avgRank <= rankThreshold`
- 最近 `trendThreshold` 小时内热度分整体上升
- 事件属于陈旧复活事件

### 6.3 连续评分公式

建议 V1 使用可解释的加权分：

```text
TotalScore = 100 * (
  0.35 * coverageScore +
  0.25 * rankScore +
  0.20 * trendScore +
  0.10 * persistenceScore +
  0.10 * llmBoostScore
) + reactivationBonus
```

字段说明：

- `coverageScore`
  不同信源覆盖度，反映“共识性”
- `rankScore`
  综合排名表现，反映“当前热度”
- `trendScore`
  最近若干小时热度变化，反映“升温速度”
- `persistenceScore`
  连续活跃程度，反映“是否持续发酵”
- `llmBoostScore`
  LLM 对事件公共重要性的修正，例如重大突发、政策级变化、国际冲突等
- `reactivationBonus`
  陈旧事件复活时给予额外加分，便于优先推送

### 6.4 热度分计算建议

为了避免直接对“排名数字”做脆弱判断，建议先把每次抓取中的事件映射为热度分，再对热度分做趋势分析。

一个可落地的 V1 方案：

```text
eventHeatAtTime = Σ(1 / rank)
```

说明：

- 同一时刻，事件被越多信源报道，热度越高
- 排名越靠前，贡献越高
- 排名第一的贡献为 `1.0`，第二为 `0.5`，第三为 `0.333...`

趋势判定建议：

- 比较最近 `N` 小时热度均值与更早窗口的热度均值
- 允许中间小幅波动，不要求严格单调
- 只要整体趋势向上即可判定为“升温”

## 7. 数据模型设计

LiteDB 建议至少包含以下集合：

### 7.1 `content_item`

原始新闻条目。

建议字段：

- `Id`
- `Source`
- `Category`
- `Type`
- `SourceItemId`
- `Title`
- `Url`
- `MobileUrl`
- `PubTime`
- `HoverText`
- `Summary`
- `NeedEnrichment`
- `CreatedAt`
- `RawPayload`

### 7.2 `content_snapshot`

每次抓取到的排名快照。

建议字段：

- `Id`
- `ContentItemId`
- `CapturedAt`
- `Rank`
- `Source`
- `Category`

### 7.3 `event`

统一事件表。

建议字段：

- `Id`
- `Type`
- `CanonicalTitle`
- `Summary`
- `Status`
- `FirstSeenAt`
- `LastSeenAt`
- `LastActivatedAt`
- `LastPushedAt`
- `PushCount`
- `IsBlacklisted`
- `BlacklistReason`

说明：

- `Type` 用于区分未来的 `NewsEvent` 与 `Topic`
- V1 不需要拆成两张表

### 7.4 `event_item`

事件与原始新闻的映射表。

建议字段：

- `Id`
- `EventId`
- `ContentItemId`
- `Confidence`
- `MatchedAt`

### 7.5 `event_score_snapshot`

事件每次分析得到的评分记录。

建议字段：

- `Id`
- `EventId`
- `CalculatedAt`
- `CoverageScore`
- `RankScore`
- `TrendScore`
- `PersistenceScore`
- `LlmBoostScore`
- `ReactivationBonus`
- `TotalScore`
- `UniqueSourceCount`
- `AvgRank`
- `HeatValue`
- `TriggerReasons`

### 7.6 `push_log`

推送记录与幂等控制。

建议字段：

- `Id`
- `EventId`
- `PushType` (`Instant` / `Digest`)
- `PushedAt`
- `Title`
- `Payload`

## 8. 系统流程

### 8.1 主流程

```text
定时抓取
  -> 保存原始新闻与排名快照
  -> 判断是否需要 Tavily 增强
  -> 生成/更新新闻摘要
  -> 候选事件召回
  -> LLM 归并与事件更新
  -> 计算事件热度与综合得分
  -> 判断是否触发即时推送
  -> 记录推送日志
```

### 8.2 摘要流程

```text
到达配置推送时刻
  -> 选取统计窗口内的候选事件
  -> 过滤黑名单
  -> 按综合得分排序
  -> 截取前 N 个事件
  -> 生成事件列表摘要
  -> 发送推送
```

## 9. 配置设计建议

现有配置已覆盖 V1 主流程，但建议继续补充以下结构：

```yaml
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
    rankThreshold: 3
    trendThreshold: 6
    staleHours: 24
    mergeThreshold: 0.75
  repeatPush:
    sourceAddThreshold: 2
    rankImproveThreshold: 3

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

tavily:
  apiKey: ""
  enabledSources: []
  maxRequestsPerRun: 20

filters:
  blacklistKeywords: []
```

说明：

- `llm` 建议拆分为 `cluster` 和 `judge`
- `filters.blacklistKeywords` 用于推送降噪
- `mergeThreshold` 用于暴露激进归并的调参入口

## 10. V1 实现优先级

建议按以下顺序开发：

1. 抓取 newsNow，落库 `content_item` 与 `content_snapshot`
2. 实现事件表与事件映射表
3. 接入 Tavily，完成指定新闻源的摘要增强
4. 接入 LLM 归并流程
5. 实现事件评分、资格判定与即时推送
6. 实现定时摘要推送
7. 实现黑名单过滤与重复推送控制

## 11. 风险与注意事项

### 11.1 Tavily 成本控制

- 必须限制调用条件与单轮调用上限
- 不建议全量抓正文

### 11.2 LLM 判定稳定性

- 需要结构化 JSON 输出
- 需要对非法输出、空输出、超时进行兜底

### 11.3 激进归并的副作用

- 可能产生误合并
- 但对 V1 来说，可接受“少量误合并换取更少漏报”

### 11.4 排名尺度差异

- 不同信源的榜单长度、排序含义可能不同
- V1 可先统一按相对靠前处理，后续再按信源加权

## 12. 结论

TrendReporter2 的 V1 方向已经明确：

- 以事件为核心对象
- 以传统媒体为首批数据源
- 以规则门槛 + 连续评分的方式识别重要事件
- 以即时推送和定时摘要作为主要输出

数据库层面不需要为“事件”和“话题”拆成两套表，统一 `event` 表并保留 `Type` 字段即可。  
分析层面则应保留未来扩展空间，让不同类型内容使用不同的特征与评分配置。
