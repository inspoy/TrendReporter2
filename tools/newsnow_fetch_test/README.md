# NewsNow 抓取诊断工具

这个目录提供一个独立的 Python 小工具，用于本地检查 NewsNow 信源和 WebExtract 摘要抽取链路。工具会读取 `sources.txt`，加载 `.env`，每个信源只抓取一次，并默认只评估每个信源前 3 条有效新闻。

## 环境准备

```bash
cd tools/newsnow_fetch_test
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env
```

编辑 `.env`：

```env
NEWSNOW_BASE_URL=http://localhost:3000
WEB_EXTRACT_URL=http://localhost:8000
NEWS_ITEM_LIMIT=3
```

`WEB_EXTRACT_URL` 可以不带协议，脚本会按主程序逻辑补上 `http://`，并自动请求 `{WEB_EXTRACT_URL}/fetch`。
`NEWS_ITEM_LIMIT` 控制每个信源评估多少条有效新闻，命令行 `--limit` 会覆盖这个值。

## 配置信源

编辑 `sources.txt`，每行一个 NewsNow source id。空行、以 `#` 开头的整行注释，以及 `#` 后面的行尾注释都会被忽略：

```text
# china
ifeng # 凤凰网
baidu # 百度热搜
```

## 运行

```bash
python newsnow_fetch_test.py
```

常用参数：

```bash
python newsnow_fetch_test.py --limit 5 --timeout 20
python newsnow_fetch_test.py --sources ./sources.txt --env-file ./.env
python newsnow_fetch_test.py --newsnow-base-url http://localhost:3000 --web-extract-url localhost:8000
```

## 输出说明

脚本输出 Markdown 表格，包含：

- `Source`：NewsNow 信源。
- `Rank`：条目在 NewsNow 返回列表中的原始排名。
- `Title`：新闻标题。
- `HoverText`：是否存在 `extra.hover`。
- `UrlFetch`：WebExtract 是否返回可用摘要。
- `TitleLength`：标题长度。
- `SummarySource`：最终摘要来源，优先级为 `HoverText`、`UrlFetch`、`TitleOnly`。
- `Summary`：最终用于诊断的摘要。
- `Error`：NewsNow 或 WebExtract 错误信息。

脚本会跳过非对象条目，以及标题和 URL 都为空的条目。WebExtract 响应会兼容顶层字段和 `data` 字段；非 2xx 响应也会先解析响应体；无效 JSON 会作为原始摘要文本处理。
