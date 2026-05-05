# 运行说明

本文面向本地长期运行和一次性排障。真实密钥、推送密钥和本地 `config.yaml` 不应提交。

## 环境与配置

1. 安装 .NET 8 SDK。
2. 复制配置模板：`cp config.example.yaml config.yaml`。
3. 按本机环境修改 `config.yaml`。

重点配置项：

- `newsNow.baseUrl`：NewsNow 服务地址，抓取接口为 `GET /api/s?id=source`。
- `database.path`：LiteDB 文件路径，默认 `./data/trend.db`。
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

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- fetch-once
```

执行一次摘要：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- digest-once
```

启动后台服务：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj
```

查看数据库集合：

```bash
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- data-view content_item --limit 20
dotnet run --project src/TrendReporter2.App/TrendReporter2.App.csproj -- data-view fetch_run --limit 10 --json
```

## 数据库

运行期 LiteDB 默认写入 `data/trend.db`。`data/` 已被忽略，不应提交。

数据库初始化会创建并索引以下集合：`content_item`、`content_snapshot`、`event`、`event_item`、`event_score_snapshot`、`push_log`、`fetch_run`、`app_state`。

## 常见错误

- 配置校验失败：先运行 `validate --config config.example.yaml` 确认模板可用，再检查本地 `config.yaml` 的 URL、时间、并发数和时区。
- NewsNow 请求失败：单个信源失败会记录在 `fetch_run.Errors`，其他信源继续运行。检查 `newsNow.baseUrl` 和对应 `source`。
- WebExtract 失败：客户端会先解析响应体；只有传输异常、响应体声明失败或空摘要时才降级为标题/hover 摘要，不中断抓取。
- LLM 未配置或失败：事件归并会倾向创建新事件，重要性判定回到中性结果。
- Unipush 失败：失败会写入 `push_log` 并记录日志，V1 不自动重试。
- 调度任务异常：抓取和摘要调度器会记录未预期异常，并继续后续周期；取消令牌仍会正常停止进程。

## 摘要状态

当前摘要链路已有调度、状态去重、候选查询、消息组装和 `push_log` 记录；`digest-once` 可用于本地验证当下时段的摘要行为。即时推送和抓取主链路可独立验证。
