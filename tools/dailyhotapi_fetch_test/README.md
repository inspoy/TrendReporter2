# DailyHotApi 抓取诊断工具

这个目录提供一个独立的 Python 小工具，用于本地检查 DailyHotApi 信源和 WebExtract 摘要抽取链路。工具会读取 `sources.txt`，加载 `.env`，每个信源只抓取一次，并默认只评估每个信源前 3 条有效新闻。

## 环境准备

```bash
cd tools/dailyhotapi_fetch_test
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env
```

编辑 `.env`：

```env
DAILYHOTAPI_BASE_URL=https://api-hot.imsyy.top
WEB_EXTRACT_URL=http://localhost:8000
NEWS_ITEM_LIMIT=3
```

`WEB_EXTRACT_URL` 可以不带协议，脚本会按主程序逻辑补上 `http://`，并自动请求 `{WEB_EXTRACT_URL}/fetch`。
`NEWS_ITEM_LIMIT` 控制每个信源评估多少条有效新闻，命令行 `--limit` 会覆盖这个值。

## 配置信源

编辑 `sources.txt`，每行一个 DailyHotApi source id。空行、以 `#` 开头的整行注释，以及 `#` 后面的行尾注释都会被忽略：

```text
# china
weibo # 微博热搜
zhihu # 知乎热榜
```

## 运行

```bash
python dailyhotapi_fetch_test.py
```

常用参数：

```bash
python dailyhotapi_fetch_test.py --limit 5 --timeout 20
python dailyhotapi_fetch_test.py --sources ./sources.txt --env-file ./.env
python dailyhotapi_fetch_test.py --dailyhotapi-base-url https://api-hot.imsyy.top --web-extract-url localhost:8000
```

## 输出说明

脚本输出 Markdown 表格，包含：

- `Source`：输入的 DailyHotApi source id。
- `ApiTitle`：接口返回的标题或名称信息。
- `Type`：接口返回的类型字段。
- `Rank`：条目在 DailyHotApi 返回列表中的原始排名。
- `Title`：新闻标题。
- `Hot`：条目的热度字段。
- `UrlFetch`：WebExtract 是否返回可用摘要。
- `TitleLength`：标题长度。
- `SummarySource`：最终摘要来源，优先级为 `Description`、`UrlFetch`、`TitleOnly`。
- `Summary`：最终用于诊断的摘要。
- `Cache`：接口返回的 `fromCache` 值。
- `UpdatedAt`：接口返回的更新时间。
- `Error`：DailyHotApi 或 WebExtract 错误信息。

脚本会跳过非对象条目，以及标题、URL 和摘要都为空的条目。DailyHotApi 响应会兼容 `code=200` 或 HTTP 2xx；条目字段会按常见别名读取，WebExtract 响应会兼容顶层字段和 `data` 字段；无效 JSON 会作为原始摘要文本处理。
