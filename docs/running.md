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

`fetch-once` 会先按配置执行 PostgreSQL 启动迁移，然后运行抓取、入库、富化、事件归并、评分和推送日志写入。该路径使用 PostgreSQL/Dapper 仓储；数据库不可用时会报错退出，不会回退到 LiteDB。

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- fetch-once
```

执行一次摘要：

`digest-once` 会从 PostgreSQL 查询摘要候选，写入 `push_log`，并通过 `app_state` 标记同一日期/时段已处理。

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- digest-once
```

启动后台服务：

后台模式会启动抓取调度器和摘要调度器。抓取调度器启动后立即执行一轮，摘要调度器按 `analysis.push.pushTime` 和 `system.timeZone` 检查触发。

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj
```

查看数据库内容：使用 `psql` 或 SQL 客户端直接连接 PostgreSQL。

## 数据库

V2 使用 PostgreSQL 作为运行期主数据库。启动迁移由 `SqlMigrationRunner` 按文件名顺序执行并校验 checksum；V2M0 的 `0001_init.sql` 保持不可变，V2M1 的附加约束和索引位于后续 migration。抓取、内容、快照、事件、评分、推送日志、运行记录和摘要状态都通过 PostgreSQL/Dapper 仓储读写。`config.example.yaml` 仅提供本地占位连接串，不包含真实凭据。

## 常见错误

- 配置校验失败：先运行 `validate --config config.example.yaml` 确认模板可用，再检查本地 `config.yaml` 的 URL、时间、并发数和时区。
- PostgreSQL 连接失败：检查 `database.connectionString`、数据库是否启动、账号权限以及是否允许创建 `vector` 扩展。
- NewsNow 请求失败：单个信源失败会记录在 `fetch_run.Errors`，其他信源继续运行。检查 `newsNow.baseUrl` 和对应 `source`。
- WebExtract 失败：客户端会先解析响应体；只有传输异常、响应体声明失败或空摘要时才降级为标题/hover 摘要，不中断抓取。
- LLM 未配置或失败：事件归并会倾向创建新事件，重要性判定回到中性结果。
- Unipush 失败：失败会写入 `push_log` 并记录日志，当前不会自动重试。
- 调度任务异常：抓取和摘要调度器会记录未预期异常，并继续后续周期；取消令牌仍会正常停止进程。

## 摘要状态

摘要调度、状态去重、候选查询、消息组装和 `push_log` 记录已接入 PostgreSQL。重复执行同一摘要时段时，`app_state` 和 `push_log.dedup_key` 会共同避免重复推送。
