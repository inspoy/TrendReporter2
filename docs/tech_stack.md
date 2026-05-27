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
  定义 YAML 配置对应的强类型模型，包括 `newsNow`、`database`、`analysis`、`llm`、`enrichment`、`filters`、`pushers`、`system`。
- `Configuration/AppConfigValidator.cs`
  做基础配置校验，例如 `newsNow.baseUrl`、`database.provider`、`database.connectionString`、`analysis.fetchInterval`、`pushTime` 格式等。
- `Configuration/TimeZoneResolver.cs`
  统一解析时区，并兼容 Windows 上的 `Asia/Shanghai`。
- `Jobs/IFetchJob.cs`
  一轮抓取任务接口；业务流程已存在，但 PostgreSQL 仓储主路径仍等待 V2M1 接入。
- `Jobs/IDigestJob.cs`
  摘要任务接口；摘要业务流程已存在，但 PostgreSQL 状态和推送日志仓储仍等待 V2M1 接入。
- `Persistence/ITrendDatabaseInitializer.cs`
  旧 LiteDB 初始化接口，V2 默认 DI 不再注册。
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
- `Persistence/SqlMigrationRunner.cs`
  执行 PostgreSQL V2M0 启动迁移并维护 `schema_migration`。
- `Persistence/Migrations/0001_init.sql`
  创建 V2M1 主链路需要的 PostgreSQL 表结构。
- `Persistence/LiteDb*Repository.cs`
  过渡期 LiteDB 适配器源码仍保留以便编译和既有测试，但 V2 默认运行路径不会回退到 LiteDB；V2M1 会替换为 PostgreSQL 仓储。
- `DependencyInjection.cs`
  集中注册 Infrastructure 层服务，包括 `NpgsqlDataSource`、迁移 runner、HTTP/LLM/推送相关适配和过渡期仓储接口。

当前引入的基础依赖：

- `LiteDB`（仅过渡期适配器/测试仍引用）
- `Dapper`
- `Newtonsoft.Json`
- `Npgsql`
- `YamlDotNet`
- `Microsoft.Extensions.Logging.Abstractions`

后续建议：

- `NewsNowClient` 放在 `Infrastructure/News/`。
- 网页抽取客户端放在 `Infrastructure/Enrichment/`。
- LLM 客户端放在 `Infrastructure/Llm/`。
- Unipush 推送器放在 `Infrastructure/Push/`。
- PostgreSQL 仓储实现放在 `Infrastructure/Persistence/`，不要新增 LiteDB 回退路径。

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
  解析命令行参数、加载配置、创建 Generic Host、注册 DI、初始化数据库迁移、启动后台服务。
- `Scheduling/FetchSchedulerService.cs`
  抓取调度器。启动后立即执行一次，之后按 `analysis.fetchInterval` 周期执行，并用 `SemaphoreSlim` 防止重入。
- `Scheduling/DigestSchedulerService.cs`
  摘要调度器。每分钟检查当前本地时间是否命中 `analysis.push.pushTime`。
- `Scheduling/FetchJob.cs`
  抓取、增强、事件归并、评分和即时推送编排；V2M0 阶段因 PostgreSQL 仓储未完成而不会进入运行主路径。
- `Scheduling/DigestJob.cs`
  定时摘要候选过滤、消息组装、推送和状态标记编排；V2M0 阶段因 PostgreSQL 仓储未完成而不会进入运行主路径。

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

V1 中 `config.example.yaml` 已整理为可解析 YAML，并将 `newsNow.baseUrl` 设为 `http://localhost:3000`，方便通过配置校验。实际部署时应复制为 `config.yaml` 后按本机环境修改。

## 4. 数据库

当前使用 PostgreSQL 作为基础数据库，示例配置提供本地占位连接串和启动迁移开关。

V2M0 只保证 PostgreSQL provider、`NpgsqlDataSource` 和迁移脚本；迁移会创建 V2M1 主链路需要的表结构。抓取、事件、评分、推送日志和摘要状态的 PostgreSQL 仓储将在 V2M1 实现，完成前非 `validate` 模式会快速提示 V2M0/V2M1 限制并退出，不会写入 PostgreSQL 主链路，也不会回退到本地 `data/trend.db`。

## 5. 常用命令

还原依赖：

```powershell
dotnet restore TrendReporter2.sln --configfile NuGet.Config
```

构建：

```powershell
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
```

验证配置：

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

## 6. V1 已完成能力代码落点

以下代码落点对应 V1M1-V1M5 已实现的主链路能力。V2 会在保留这些业务资产的前提下，把运行期持久化主路径迁移到 PostgreSQL。

V1M1 新闻抓取与原始入库：

- `Core`：维护 `NewsItem`、`ContentItem`、`ContentSnapshot`、`FetchRun` 等模型和仓储接口。
- `Infrastructure`：维护 NewsNow 抓取客户端、内容入库服务和现有过渡期持久化适配器。
- `App`：通过 `FetchJob`/`FetchSchedulerService` 编排抓取与入库流程。

V1M2 正文增强：

- `Core`：维护增强结果、增强判定服务接口和弱标题策略。
- `Infrastructure`：维护 WebExtract 增强客户端和增强服务实现。
- `App`：在抓取链路中编排增强服务。

V1M3 事件建模与归并：

- `Core`：维护事件领域模型、候选召回接口、归并接口和匹配规则。
- `Infrastructure`：维护 LLM cluster 客户端和事件持久化适配器。
- `App`：在抓取链路中编排事件匹配流程。

V1M4 评分与即时推送：

- `Core`：维护评分模型、评分规则、推送判定接口和黑名单策略。
- `Infrastructure`：维护 Judge LLM 客户端、Unipush 推送器和 `push_log` 持久化适配器。
- `App`：在抓取链路末尾编排评分与即时推送。

V1M5 定时摘要与黑名单：

- `Core`：维护摘要查询、黑名单判定和摘要消息模型。
- `Infrastructure`：维护摘要候选、状态和推送日志相关持久化适配器。
- `App`：通过 `DigestJob`/`DigestSchedulerService` 编排摘要候选过滤、消息组装、推送和状态标记。
