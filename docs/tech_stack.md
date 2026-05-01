# TrendReporter2 C# 工程结构说明

本文说明当前 C# 工程的项目划分、依赖方向和后续开发时的代码落点。

## 1. 总体结构

当前工程采用 .NET 8，解决方案文件为：

```text
TrendReporter2.sln
```

源码位于 `src/` 下，分为三个项目：

```text
src/
  TrendReporter2.App/
  TrendReporter2.Core/
  TrendReporter2.Infrastructure/
```

依赖方向固定为：

```text
TrendReporter2.App
  -> TrendReporter2.Core
  -> TrendReporter2.Infrastructure

TrendReporter2.Infrastructure
  -> TrendReporter2.Core

TrendReporter2.Core
  -> 无项目依赖
```

也就是说：

- `Core` 是内核层，不依赖外部工程。
- `Infrastructure` 实现外部依赖和持久化，依赖 `Core` 中定义的接口与模型。
- `App` 是进程入口，负责启动 Host、注册依赖、挂载后台服务。

## 2. 项目职责

### 2.1 `TrendReporter2.Core`

`Core` 放领域模型、配置模型、核心接口和纯业务规则。

当前目录：

```text
src/TrendReporter2.Core/
  Configuration/
  Jobs/
  Persistence/
```

当前职责：

- `Configuration/AppConfig.cs`
  定义 YAML 配置对应的强类型模型，包括 `newsNow`、`database`、`analysis`、`llm`、`tavily`、`filters`、`pushers`、`system`。
- `Configuration/AppConfigValidator.cs`
  做基础配置校验，例如 `newsNow.baseUrl`、`database.path`、`analysis.fetchInterval`、`pushTime` 格式等。
- `Configuration/TimeZoneResolver.cs`
  统一解析时区，并兼容 Windows 上的 `Asia/Shanghai`。
- `Jobs/IFetchJob.cs`
  一轮抓取任务接口，M1 开始会接入真实抓取逻辑。
- `Jobs/IDigestJob.cs`
  摘要任务接口，M5 会接入真实摘要逻辑。
- `Persistence/ITrendDatabaseInitializer.cs`
  数据库初始化接口。
- `Persistence/TrendCollectionNames.cs`
  LiteDB 集合名常量。

后续建议：

- 新闻、事件、评分、推送等领域对象优先放在 `Core`。
- 算法类、规则类、判定服务接口优先放在 `Core`。
- 不要在 `Core` 中直接引用 LiteDB、HTTP、LLM SDK 或具体第三方实现。

### 2.2 `TrendReporter2.Infrastructure`

`Infrastructure` 放外部系统适配和基础设施实现。

当前目录：

```text
src/TrendReporter2.Infrastructure/
  Configuration/
  Persistence/
  DependencyInjection.cs
```

当前职责：

- `Configuration/YamlAppConfigLoader.cs`
  使用 `YamlDotNet` 读取 YAML，并反序列化为 `AppConfig`。
- `Persistence/LiteDbInitializer.cs`
  创建 LiteDB 文件、集合和基础索引。
- `DependencyInjection.cs`
  集中注册 Infrastructure 层服务。

当前引入的基础依赖：

- `LiteDB`
- `Newtonsoft.Json`
- `YamlDotNet`
- `Microsoft.Extensions.Logging.Abstractions`

后续建议：

- `NewsNowClient` 放在 `Infrastructure/NewsSources/`。
- `TavilyClient` 放在 `Infrastructure/Enrichment/`。
- LLM 客户端放在 `Infrastructure/Llm/`。
- Unipush 推送器放在 `Infrastructure/Push/`。
- LiteDB 仓储实现放在 `Infrastructure/Persistence/`。

### 2.3 `TrendReporter2.App`

`App` 是控制台后台进程入口。

当前目录：

```text
src/TrendReporter2.App/
  Program.cs
  Scheduling/
```

当前职责：

- `Program.cs`
  解析命令行参数、加载配置、创建 Generic Host、注册 DI、初始化数据库、启动后台服务。
- `Scheduling/FetchSchedulerService.cs`
  抓取调度器骨架。启动后立即执行一次，之后按 `analysis.fetchInterval` 周期执行，并用 `SemaphoreSlim` 防止重入。
- `Scheduling/DigestSchedulerService.cs`
  摘要调度器骨架。每分钟检查当前本地时间是否命中 `analysis.push.pushTime`。
- `Scheduling/EmptyFetchJob.cs`
  M0 占位实现，后续会替换为真实 `FetchJob`。
- `Scheduling/EmptyDigestJob.cs`
  M0 占位实现，后续会替换为真实 `DigestJob`。

当前引入的基础依赖：

- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Console`

后续建议：

- `App` 只做进程启动、调度和依赖装配。
- 真实业务流程不要直接堆在 `Program.cs`。
- 新增后台服务时，优先放在 `Scheduling/` 或后续单独的 `Jobs/` 目录。

## 3. 配置文件

示例配置文件：

```text
config.example.yaml
```

默认启动逻辑：

- 默认读取 `config.yaml`。
- 也可以通过 `--config` 指定配置路径。

示例：

```powershell
dotnet run --project src\TrendReporter2.App\TrendReporter2.App.csproj -- --config config.example.yaml
```

M0 中 `config.example.yaml` 已整理为可解析 YAML，并将 `newsNow.baseUrl` 设为 `http://localhost:3000`，方便通过配置校验。实际部署时应复制为 `config.yaml` 后按本机环境修改。

## 4. 数据库

当前使用 LiteDB。

默认数据库路径：

```text
data/trend.db
```

该目录已加入 `.gitignore`，不会提交运行期数据。

M0 初始化的集合：

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

集合名集中定义在：

```text
src/TrendReporter2.Core/Persistence/TrendCollectionNames.cs
```

## 5. 常用命令

还原依赖：

```powershell
dotnet restore TrendReporter2.sln --configfile NuGet.Config
```

构建：

```powershell
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
```

验证配置与数据库初始化：

```powershell
dotnet run --project src\TrendReporter2.App\TrendReporter2.App.csproj --no-build -- validate
```

启动后台服务：

```powershell
dotnet run --project src\TrendReporter2.App\TrendReporter2.App.csproj
```

说明：

- 当前环境中普通 solution build 可能受到 C# shared compiler 或并行构建限制影响。
- 因此推荐使用 `-m:1 /p:UseSharedCompilation=false` 进行稳定构建。

## 6. 后续里程碑代码落点

M1 新闻抓取与原始入库：

- `Core`：新增 `NewsItem`、`ContentItem`、`ContentSnapshot`、`FetchRun` 等模型和仓储接口。
- `Infrastructure`：新增 `NewsNowClient`、LiteDB 仓储实现。
- `App`：将 `EmptyFetchJob` 替换为真实 `FetchJob`。

M2 正文增强：

- `Core`：新增 `EnrichmentResult`、增强判定服务接口。
- `Infrastructure`：新增 `TavilyClient`。
- `App`：在抓取链路中接入增强服务。

M3 事件建模与归并：

- `Core`：新增事件领域模型、候选召回接口、归并接口。
- `Infrastructure`：新增 LLM cluster 客户端和事件仓储。
- `App`：在抓取链路中接入事件匹配流程。

M4 评分与即时推送：

- `Core`：新增评分模型、评分规则、推送判定接口。
- `Infrastructure`：新增 Judge LLM 客户端、Unipush 推送器、`push_log` 仓储。
- `App`：在抓取链路末尾接入评分与即时推送。

M5 定时摘要与黑名单：

- `Core`：新增摘要查询、黑名单判定、摘要消息模型。
- `Infrastructure`：补充摘要相关仓储查询。
- `App`：将 `EmptyDigestJob` 替换为真实 `DigestJob`。
