# TrendReporter2

TrendReporter2 是一个面向个人使用的舆论趋势分析工具，用于从传统媒体榜单中持续发现值得关注的重要事件，并追踪事件的热度变化、后续进展和发展过程。

V1 的目标不是新闻阅读器，而是事件级趋势发现与推送系统：抓取新闻源、归并相关新闻为事件、判断事件重要性，并在需要时即时推送或生成定时摘要。

## 当前状态

项目使用 .NET 8 构建，当前代码已具备以下能力：

- 读取并校验 YAML 配置。
- 初始化 LiteDB 数据库及基础集合、索引。
- 通过 NewsNow 抓取配置中的新闻源，并写入 `content_item`、`content_snapshot` 和 `fetch_run`。
- 对需要补充上下文的新闻调用配置的网页抽取服务，写回摘要与增强状态。
- 基于规则召回候选事件，并可通过 OpenAI 兼容的 Cluster LLM 辅助事件归并。
- 支持后台调度、单次抓取、配置校验和 LiteDB 数据查看命令。

根据 [里程碑文档](docs/milestones.md)，M0-M2 已完成，M3-M6 仍在推进中。评分、即时推送、定时摘要、黑名单降噪和完整回归测试仍属于后续工作。

## 核心功能设计

- **定时抓取**：按 `analysis.fetchInterval` 周期抓取 NewsNow 中配置的全部信源。
- **原始数据留存**：保存新闻条目和每轮排名快照，保留事件热度变化所需的历史数据。
- **正文/摘要增强**：对标题信息不足的新闻按预算调用增强服务，降低后续误归并风险。
- **事件归并**：以事件为核心对象，使用候选召回和 LLM 判定将多条相关新闻归并到同一事件。
- **热度与重要性判定**：通过多信源覆盖、源内排名、趋势变化、持续活跃时长和 LLM 修正计算事件价值。
- **推送与摘要**：设计上支持即时推送重要事件，以及按配置时间输出高价值事件摘要。

## 技术栈

- 运行时：`.NET 8`
- 进程模型：`Generic Host + BackgroundService`
- 数据库：`LiteDB`
- 配置：`YamlDotNet`
- JSON：`Newtonsoft.Json`
- HTTP：`HttpClientFactory`
- 日志：`Microsoft.Extensions.Logging`

## 项目结构

```text
TrendReporter2.sln
config.example.yaml
docs/
src/
  TrendReporter2.App/             # 程序入口、CLI、后台调度、数据查看
  TrendReporter2.Core/            # 配置模型、领域模型、服务接口、核心规则
  TrendReporter2.Infrastructure/  # LiteDB、NewsNow、增强服务、LLM 等外部适配
```

依赖方向固定为：

```text
TrendReporter2.App -> TrendReporter2.Core
TrendReporter2.App -> TrendReporter2.Infrastructure
TrendReporter2.Infrastructure -> TrendReporter2.Core
TrendReporter2.Core -> 无项目依赖
```

## 快速开始

### 1. 准备环境

安装 .NET 8 SDK，并确保本机可以访问配置中的 NewsNow 服务。示例配置默认使用：

```yaml
newsNow:
  baseUrl: "http://localhost:3000"
```

### 2. 准备配置

复制示例配置：

```bash
cp config.example.yaml config.yaml
```

按本机环境修改 `config.yaml`，重点检查：

- `newsNow.baseUrl`：NewsNow 服务地址。
- `newsNow.sources`：需要抓取的分类和信源。
- `database.path`：LiteDB 文件路径，默认 `./data/trend.db`。
- `enrichment.web_extract_url`：网页抽取服务地址；为空时增强客户端不可用。
- `llm.cluster`：事件归并模型配置；为空时会跳过 LLM 归并并创建新事件。
- `pushers`：推送通道配置，后续推送功能会使用。
- `system.timeZone`：调度时区，默认 `Asia/Shanghai`。

不要将包含真实 API Key、推送密钥或本地环境信息的 `config.yaml` 提交到仓库。

### 3. 还原和构建

```bash
dotnet restore TrendReporter2.sln --configfile NuGet.Config
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
```

项目文档中建议使用 `-m:1 /p:UseSharedCompilation=false`，以规避部分环境中的 shared compiler 或并行构建限制。

### 4. 校验配置和数据库初始化

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate
```

如需指定配置文件：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```

### 5. 执行一次抓取

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- fetch-once
```

### 6. 启动后台服务

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj
```

后台服务启动后会立即执行一轮抓取，之后按 `analysis.fetchInterval` 周期运行。摘要调度器当前仍挂载在进程中，但真实摘要任务尚未完成。

## 常用命令

查看 LiteDB 集合数据：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- data-view content_item --limit 20
```

输出 JSON：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- data-view fetch_run --limit 10 --json
```

可查看的集合名定义在 `src/TrendReporter2.Core/Persistence/TrendCollectionNames.cs`，当前包括：

```text
content_item
content_snapshot
event
event_item
event_score_snapshot
push_log
fetch_run
app_state
```

## 配置概览

主要配置段如下：

- `newsNow`：NewsNow 服务地址和分类信源列表。
- `database`：LiteDB 数据库路径。
- `analysis`：抓取间隔、分析窗口、事件归并阈值、重复推送阈值等。
- `llm`：事件归并、重要性判断、摘要润色等模型配置。
- `enrichment`：网页抽取服务配置、单轮增强预算、标题长度阈值和冷却时间。
- `filters`：黑名单关键词。
- `pushers`：推送通道配置，当前示例为 Unipush。
- `system`：时区和并发限制。

完整示例见 [config.example.yaml](config.example.yaml)。

## 数据库

运行期数据默认写入：

```text
data/trend.db
```

`data/` 属于运行期目录，不应提交到仓库。数据库初始化会创建内容、快照、事件、事件映射、评分快照、推送日志、抓取记录和应用状态集合。

## 测试与验证

当前仓库暂未包含独立测试项目。开发或变更后建议至少执行：

```bash
dotnet restore TrendReporter2.sln --configfile NuGet.Config
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```

后续 M6 计划补齐关键单元测试、集成测试、真实新闻回归样本和更完整的运行说明。

## 详细文档

- [V1 产品设计稿](docs/v1-design.md)
- [V1 技术设计稿](docs/technical-design.md)
- [C# 工程结构说明](docs/tech_stack.md)
- [V1 里程碑与任务清单](docs/milestones.md)

## 许可证

见 [LICENSE](LICENSE)。
