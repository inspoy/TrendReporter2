# TrendReporter2 V1 里程碑与任务清单

## 1. 目标

本文档基于 [v1-design.md](v1-design.md) 和 [technical-design.md](technical-design.md)，将 V1 开发拆分为可执行的里程碑和任务清单。

拆分原则：

- 优先打通主链路，再补强效果
- 每个里程碑都应产出可验证结果
- 每个任务都尽量对应明确代码模块
- 先实现“能跑通”，再优化“更聪明”

## 2. 总体节奏

建议将 V1 拆为 7 个里程碑：

| 进度 | 里程碑 | 名称 | 目标 |
| --- | --- | --- | --- |
| ✅ | `M0` | 项目骨架与基础设施 | 建立可运行工程骨架、配置、日志、数据库初始化 |
| ✅ | `M1` | 新闻抓取与原始入库 | 打通 NewsNow 抓取与 `content_item/content_snapshot` 落库 |
| ✅ | `M2` | 正文增强与摘要补充 | 接入 Tavily，补足新闻摘要 |
| ✅ | `M3` | 事件建模与归并 | 建立事件表、候选召回、LLM 归并 |
| 🚧 | `M4` | 评分与即时推送 | 实现热度分、重要性判定、重复推送控制 |
| 🚧 | `M5` | 定时摘要与降噪 | 实现早晚摘要、黑名单与摘要去重 |
| 🚧 | `M6` | 稳定性与回归测试 | 完成日志、错误处理、测试和运行校验 |

## 3. 里程碑详情

### 3.1 `M0` 项目骨架与基础设施

### 目标

搭建 .NET 8 项目骨架，建立配置加载、LiteDB 初始化、日志和后台服务运行框架。

### 交付物

- 可启动的控制台/后台程序
- 配置文件读取与校验
- LiteDB 连接与集合初始化
- 日志输出基础能力
- 两个空调度任务骨架：抓取任务、摘要任务

### 任务清单

#### `M0-T1` 建立解决方案与项目结构

- 创建 `TrendReporter2.sln`
- 创建项目：
  - `src/TrendReporter2.App`
  - `src/TrendReporter2.Core`
  - `src/TrendReporter2.Infrastructure`
- 配置项目引用关系

#### `M0-T2` 引入基础依赖

- 引入 `LiteDB`
- 引入 `Newtonsoft.Json`
- 引入 `YamlDotNet`
- 引入 `Microsoft.Extensions.Hosting`
- 引入 `Microsoft.Extensions.Logging`
- 引入 `Microsoft.Extensions.Http`

#### `M0-T3` 定义配置模型

- 创建 `AppConfig`
- 创建 `NewsNowConfig`
- 创建 `DatabaseConfig`
- 创建 `AnalysisConfig`
- 创建 `PushConfig`
- 创建 `EventAnalysisConfig`
- 创建 `RepeatPushConfig`
- 创建 `LlmConfig`
- 创建 `TavilyConfig`
- 创建 `FilterConfig`
- 创建 `SystemConfig`

#### `M0-T4` 实现 YAML 配置加载

- 读取本地 YAML 文件
- 反序列化为配置对象
- 增加基础校验：
  - `newsNow.baseUrl` 非空
  - `database.path` 非空
  - `analysis.fetchInterval > 0`
  - `pushTime` 格式合法

#### `M0-T5` 建立 DI 和 Host 启动逻辑

- 配置 `Generic Host`
- 注册配置对象
- 注册日志
- 注册数据库工厂
- 注册空实现的抓取任务和摘要任务

#### `M0-T6` 实现 LiteDB 初始化

- 打开数据库连接
- 创建集合
- 初始化索引
- 确保重复启动不报错

#### `M0-T7` 建立基础后台任务框架

- 创建 `FetchSchedulerService`
- 创建 `DigestSchedulerService`
- 先只输出调度日志，不执行业务逻辑

### 验收标准

- 程序可以启动并正常退出
- 配置加载成功时输出摘要日志
- 缺失必要配置时能明确报错
- LiteDB 文件能自动创建
- 数据集合和索引可以初始化成功

### 3.2 `M1` 新闻抓取与原始入库

### 目标

打通从 NewsNow 抓取新闻，到落库 `content_item` 与 `content_snapshot` 的主链路。

### 交付物

- `NewsNowClient`
- 一轮完整抓取任务
- `fetch_run` 记录
- 内容去重与快照落库

### 任务清单

#### `M1-T1` 定义抓取领域模型

- 创建 `NewsItem`
- 创建 `FetchRun`
- 创建 `ContentItem`
- 创建 `ContentSnapshot`

#### `M1-T2` 实现 `NewsNowClient`

- 调用 `GET /api/s?id=source`
- 解析返回 JSON
- 将返回项映射为 `NewsItem`
- 处理 `status = success/cache`

#### `M1-T3` 实现抓取任务主流程

- 创建 `FetchJob`
- 遍历配置中的全部 `sources`
- 为每个 source 发起请求
- 汇总成功/失败信息

#### `M1-T4` 实现 `content_item` 去重入库

- 用 `(Source, SourceItemId)` 作为唯一识别
- 新数据插入
- 已存在数据更新 `UpdatedAt`
- 保存原始 JSON 到 `RawPayload`

#### `M1-T5` 实现 `content_snapshot` 落库

- 为每轮抓取的每条新闻生成快照
- 保存 `RunId`、`CapturedAt`、`Rank`

#### `M1-T6` 实现 `fetch_run` 记录

- 任务开始时创建 `Running`
- 任务结束后更新为 `Succeeded/Partial/Failed`
- 记录信源成功数、失败数、抓取条数

#### `M1-T7` 接入抓取调度器

- 启动后立即抓取一次
- 之后按 `fetchInterval` 周期执行
- 防止重入

### 验收标准

- 能成功抓取至少一个 newsNow 信源
- 同一条新闻不会重复创建多条 `content_item`
- 每轮抓取都会新增对应 `content_snapshot`
- `fetch_run` 能反映任务状态和统计信息

### 3.3 `M2` 正文增强与摘要补充

### 目标

接入 Tavily，对“标题信息不足”的新闻补充摘要，提升后续事件归并质量。

### 交付物

- `ITavilyClient`
- `EnrichmentService`
- `NeedEnrichment` 判断逻辑
- `Summary` 和 `SummarySource` 写回

### 任务清单

#### `M2-T1` 定义增强结果模型

- 创建 `EnrichmentResult`
- 约定字段：
  - `Summary`
  - `Title`
  - `Url`
  - `RawPayload`

#### `M2-T2` 实现增强判定逻辑

- 按标题长度判定
- 按关键词规则判定
- 按信源白名单判定
- 若 `hover` 足够完整，则可不增强

#### `M2-T3` 抽象并实现 `ITavilyClient`

- 根据 Tavily API 文档封装请求
- 处理成功响应
- 处理超时、限流、失败响应

#### `M2-T4` 实现 `EnrichmentService`

- 找出本轮需增强的新闻
- 控制单轮请求数量不超过 `maxRequestsPerRun`
- 写回 `Summary`
- 标记 `SummarySource = Tavily`

#### `M2-T5` 增加降级行为

- Tavily 失败时使用 `TitleOnly`
- 错误只记日志，不中断抓取主流程

### 验收标准

- 命中增强条件的新闻可写回摘要
- Tavily 失败不会导致整轮任务失败
- 未增强新闻仍可继续参与后续流程

### 3.4 `M3` 事件建模与归并

### 目标

建立 `event` 相关集合，打通候选事件召回、LLM 归并、新建/更新/复活事件的流程。

### 交付物

- `event`、`event_item`
- 候选事件召回逻辑
- `Cluster` LLM 接入
- 事件创建、更新、复活能力

### 任务清单

#### `M3-T1` 定义事件领域模型

- 创建 `EventAggregate`
- 创建 `EventItem`
- 创建 `EventType`
- 创建 `EventStatus`

#### `M3-T2` 实现事件仓储

- 新建事件
- 更新事件
- 根据时间窗口查询候选事件
- 按 `staleHours` 查询陈旧事件

#### `M3-T3` 实现候选事件召回

- 取最近 `historyHours` 的 `Active` 事件
- 补充最近 7 天内标题相近的 `Stale` 事件
- 根据标题、摘要做粗匹配排序
- 返回前 10-20 个候选

#### `M3-T4` 抽象 `IClusterLlmClient`

- 接 OpenAI 兼容接口
- 约束返回结构化 JSON
- 处理非法 JSON 和空返回

#### `M3-T5` 实现 `EventMatcher`

- 对每条新闻做候选召回
- 调用 `Cluster` 模型判断是否归并
- 满足阈值则归并到已有事件
- 不满足则创建新事件

#### `M3-T6` 实现复活逻辑

- 发现命中的旧事件超过 `staleHours`
- 更新 `Status = Active`
- 更新 `LastActivatedAt`
- 后续推送中标记“旧事件后续”

#### `M3-T7` 落库事件映射关系

- 将新闻与事件写入 `event_item`
- 记录 `Confidence`
- 避免重复映射

### 验收标准

- 新新闻可以被正确归并到已有事件或创建为新事件
- 同一事件下可以关联多条新闻
- 陈旧事件再次出现时会复活原事件，而不是新建事件

### 3.5 `M4` 评分与即时推送

### 目标

建立事件评分体系，实现重要事件资格判定、重复推送控制和即时推送。

### 交付物

- `event_score_snapshot`
- 热度分与趋势分计算
- `Judge` LLM 接入
- 事件阶段判断与发展进程摘要
- 即时推送链路

### 任务清单

#### `M4-T1` 定义评分模型

- 创建 `EventScore`
- 创建 `EventScoreSnapshot`
- 创建 `TriggerReason` 枚举或常量

#### `M4-T2` 实现热度分计算

- 读取单轮内事件覆盖的新闻快照
- 计算 `HeatValue = Σ(1 / rank)`

#### `M4-T3` 实现资格判定

- 判断是否满足：
  - 多信源且平均排名靠前
  - 最近 `trendThreshold` 小时热度整体上升
  - 陈旧事件复活

#### `M4-T4` 实现趋势分计算

- 获取事件最近 N 小时热度样本
- 计算前半窗与后半窗均值
- 归一化为 `0-1`

#### `M4-T5` 抽象 `IJudgeLlmClient`

- 让模型返回：
  - `boostScore`
  - `labels`
  - `summary`
  - `stage`
  - `progressSummary`
  - `reason`
- 失败时返回默认值

#### `M4-T6` 实现事件发展进程生成

- 基于事件新闻、快照和评分结果判断当前阶段
- 提炼关键进展节点
- 生成可直接用于推送的进程摘要
- 写回事件对象

#### `M4-T7` 实现综合评分

- 计算：
  - `CoverageScore`
  - `RankScore`
  - `TrendScore`
  - `PersistenceScore`
  - `LlmBoostScore`
  - `ReactivationBonus`
- 写入 `event_score_snapshot`

#### `M4-T8` 实现重复推送判定

- 对比上次推送时的：
  - 信源数
  - 平均排名
  - 综合分
- 满足配置阈值时允许再次推送

#### `M4-T9` 实现 Unipush 推送器

- 按文档生成请求
- 填充 `cate = default`
- 发送即时推送

#### `M4-T10` 记录 `push_log`

- 生成幂等键
- 保存请求体
- 保存成功/失败结果

### 验收标准

- 重要事件会在抓取轮次后被自动识别
- 满足条件的事件能触发即时推送
- 同一事件不会在无变化时重复推送
- 满足升级条件时支持二次推送

### 3.6 `M5` 定时摘要与降噪

### 目标

实现早晚摘要推送、关键词黑名单，以及摘要幂等控制。

### 交付物

- 摘要调度器
- 摘要查询与排序逻辑
- 关键词黑名单
- 带发展进程的摘要推送消息生成

### 任务清单

#### `M5-T1` 实现黑名单过滤

- 读取 `filters.blacklistKeywords`
- 用 `CanonicalTitle + Summary` 匹配
- 命中后设置 `IsBlacklisted`

#### `M5-T2` 实现摘要候选查询

- 选取统计窗口内活跃事件
- 排除黑名单事件
- 按 `TotalScore` 排序
- 取前 `pushCount` 条

#### `M5-T3` 实现摘要消息组装

- 生成摘要标题
- 生成事件列表内容
- 每条事件至少包含：
  - 事件标题
  - 一句话摘要
  - 当前阶段
  - 发展进程摘要或关键节点
  - 热度依据
  - 代表来源

#### `M5-T4` 实现摘要调度器

- 每分钟检查是否命中 `pushTime`
- 使用 `app_state` 控制当日同一时刻只执行一次

#### `M5-T5` 实现摘要推送日志

- 将摘要推送写入 `push_log`
- 使用 `digest:{date}:{time}` 作为幂等键

### 验收标准

- 到达配置时刻后可自动发送事件摘要
- 黑名单事件不会出现在即时推送和摘要中
- 应用重启后，同一摘要时刻不会重复发送

### 3.7 `M6` 稳定性与回归测试

### 目标

补齐错误处理、日志、测试与运行校验，使系统具备可长期运行的稳定性。

### 交付物

- 关键单元测试
- 基础集成测试
- 更完整日志
- 错误降级与运行说明

### 任务清单

#### `M6-T1` 完善结构化日志

- 抓取开始/结束
- 单信源结果
- Tavily 成功/失败
- LLM 成功/失败
- 事件新建/归并/复活
- 推送触发原因

#### `M6-T2` 完善错误处理

- 单信源抓取失败不阻塞全局
- Tavily 失败降级
- LLM 失败降级
- Unipush 失败写日志

#### `M6-T3` 增加单元测试

- 热度分计算
- 趋势分计算
- 资格判定
- 重复推送判定
- 黑名单过滤
- 增强判定逻辑

#### `M6-T4` 增加集成测试

- NewsNow 响应解析
- LiteDB 落库
- Tavily 结果写回
- Unipush 请求体生成

#### `M6-T5` 准备回归样本

- 收集一批真实新闻标题/摘要样本
- 验证误合并和漏合并情况

#### `M6-T6` 编写运行说明

- 配置方式
- 启动方式
- 常见错误说明
- 数据库文件位置说明

### 验收标准

- 主链路具备错误降级能力
- 关键算法有自动化测试覆盖
- 本地长时间运行不出现明显重复推送或任务重叠

## 4. 推荐开发顺序

按依赖关系，建议严格按以下顺序推进：

1. `M0` 项目骨架与基础设施
2. `M1` 新闻抓取与原始入库
3. `M2` 正文增强与摘要补充
4. `M3` 事件建模与归并
5. `M4` 评分与即时推送
6. `M5` 定时摘要与降噪
7. `M6` 稳定性与回归测试

原因：

- `M1` 产出的抓取数据是后续所有模块的输入
- `M2` 的摘要质量会直接影响 `M3` 归并效果
- `M4` 依赖 `M3` 的事件结构
- `M5` 依赖 `M4` 的评分结果
- `M6` 适合在主功能打通后统一补齐

## 5. 可并行任务

虽然整体建议串行推进，但以下任务可以并行：

| 并行组 | 可并行任务 |
| --- | --- |
| `P1` | `M0-T3` 配置模型、`M0-T6` LiteDB 初始化、`M0-T7` 调度器骨架 |
| `P2` | `M1-T2` NewsNowClient、`M1-T4` content_item 仓储、`M1-T6` fetch_run |
| `P3` | `M2-T2` 增强判定、`M2-T3` TavilyClient |
| `P4` | `M3-T3` 候选召回、`M3-T4` Cluster LLM Client |
| `P5` | `M4-T2` 热度分、`M4-T5` Judge LLM Client、`M4-T8` Unipush |
| `P6` | `M5-T1` 黑名单过滤、`M5-T3` 摘要消息组装 |

## 6. 最小可运行版本

如果想尽快得到一个可用版本，可以先做一个 `MVP` 子集：

- 完成 `M0`
- 完成 `M1`
- 在 `M2` 中先只实现“标题即摘要”的降级版本，不接 Tavily
- 在 `M3` 中先用规则召回 + LLM 归并
- 在 `M4` 中先实现首次推送，不做重复推送
- 暂缓 `M5` 摘要

这样可以更快得到一个“能抓、能归并、能推送”的早期版本。

## 7. 建议的第一批开发任务

如果下一步要直接进入编码，我建议先做这一批：

1. 创建解决方案和三个项目
2. 建立配置模型和 YAML 读取
3. 建立 LiteDB 工厂与集合初始化
4. 实现 `NewsNowClient`
5. 实现 `FetchJob`
6. 实现 `content_item/content_snapshot/fetch_run` 落库
7. 让抓取调度器先跑起来

完成这 7 步后，项目就会从“只有文档”进入“已有真实数据进入数据库”的状态。

## 8. 完成定义

V1 可以认为“基本完成”，需要满足以下条件：

- 系统能按周期抓取全部配置新闻源
- 新闻能够稳定入库并保留排名历史
- 事件可以被归并、更新和复活
- 重要事件能被即时推送
- 早晚摘要能按配置时刻发送
- 黑名单生效
- 长时间运行不会明显重复推送
- 核心算法具备基础测试覆盖
