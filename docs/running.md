# 运行说明

本文面向本地长期运行和一次性排障。真实密钥、推送密钥和本地 `config.yaml` 不应提交。

## 环境与配置

1. 安装 .NET 8 SDK。
2. 复制配置模板：`cp config.example.yaml config.yaml`。
3. 按本机环境修改 `config.yaml`。

重点配置项：

- `newsNow.baseUrl`：NewsNow 服务地址，抓取接口为 `GET /api/s?id=source`。
- `database.provider`：示例固定为 `postgres`。
- `database.connectionString`：PostgreSQL 连接串，占位值仅用于本地示例。
- `database.migrateOnStartup`：是否在启动时执行迁移，示例中显式开启。
- `enrichment.webExtractUrl`：网页抽取服务地址。配置 `https://extract.local` 时，程序会请求 `https://extract.local/fetch`。
- `llm.cluster`、`llm.judge`、`llm.writer`：OpenAI 兼容模型配置。为空时相关 LLM 能力会降级。
- `pushers`：推送通道。当前实现包含 `unipush`，会发送 `cate/title/msg/link` JSON 并带 `Push-Key` 请求头。
- `system.timeZone`：摘要调度使用的时区，默认 `Asia/Shanghai`。

## 启动与常用命令

还原与构建：

```bash
dotnet restore TrendReporter2.sln --configfile NuGet.Config
dotnet build TrendReporter2.sln --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
dotnet build TrendReporter2.sln --configuration Release --no-restore -m:1 /p:UseSharedCompilation=false --verbosity minimal
```

校验配置：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- validate --config config.example.yaml
```

执行一次抓取：

V2M0 当前只提供 PostgreSQL 连接和迁移基础；`fetch-once` 会在启动迁移后提示抓取所需 PostgreSQL 仓储将在 V2M1 实现，并以非零状态退出，不会回退到 LiteDB。

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- fetch-once
```

执行一次摘要：

`digest-once` 在 V2M0 阶段同样会快速提示摘要所需 PostgreSQL 仓储尚未实现。

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- digest-once
```

启动后台服务：

后台模式在 V2M0 阶段保留命令入口，但会快速退出，等待 V2M1 接入抓取、事件、评分、推送日志和摘要状态的 PostgreSQL 仓储。

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj
```

查看数据库内容：使用 `psql` 或 SQL 客户端直接连接 PostgreSQL。

## 数据库

V2M0 已完成 PostgreSQL provider 配置、`NpgsqlDataSource` 注册和启动迁移。迁移会创建 V2M1 主链路需要的表结构，但抓取、事件、评分、推送日志和摘要状态的 PostgreSQL 仓储尚未实现；因此非 `validate` 模式会在迁移后快速失败并输出中文 V2M0/V2M1 提示。`config.example.yaml` 仅提供本地占位连接串，不包含真实凭据。

## 常见错误

- 配置校验失败：先运行 `validate --config config.example.yaml` 确认模板可用，再检查本地 `config.yaml` 的 URL、时间、并发数和时区。
- 非 `validate` 模式提示 PostgreSQL 仓储将在 V2M1 实现：这是 V2M0 的预期限制，不代表配置校验或迁移基础失败。
- NewsNow 请求失败：单个信源失败会记录在 `fetch_run.Errors`，其他信源继续运行。检查 `newsNow.baseUrl` 和对应 `source`。
- WebExtract 失败：客户端会先解析响应体；只有传输异常、响应体声明失败或空摘要时才降级为标题/hover 摘要，不中断抓取。
- LLM 未配置或失败：事件归并会倾向创建新事件，重要性判定回到中性结果。
- Unipush 失败：失败会写入 `push_log` 并记录日志，V1 不自动重试。
- 调度任务异常：抓取和摘要调度器会记录未预期异常，并继续后续周期；取消令牌仍会正常停止进程。

## 摘要状态

摘要调度、状态去重、候选查询、消息组装和 `push_log` 记录的业务链路已保留；V2M1 PostgreSQL 仓储完成前，`digest-once` 只用于验证启动限制提示，不能完成真实摘要写入。
