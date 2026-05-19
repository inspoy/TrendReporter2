#!/usr/bin/env python3
"""Fetch DailyHotApi sources once and print a Markdown diagnostic table."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from typing import TypeAlias, TypedDict, cast
from urllib.parse import quote

import requests

DEFAULT_LIMIT = 3
LIMIT_ENV_NAME = "NEWS_ITEM_LIMIT"
WHITESPACE_RE = re.compile(r"\s+")
JsonObject: TypeAlias = dict[str, object]
JsonList: TypeAlias = list[object]


@dataclass(frozen=True)
class CliArgs:
    sources: str
    env_file: str
    limit: int | None
    dailyhotapi_base_url: str | None
    web_extract_url: str | None
    timeout: float
    health: bool
    params: bool


class ReportRow(TypedDict):
    Source: str
    ApiTitle: str
    Type: str
    Rank: object
    Title: str
    Hot: str
    UrlFetch: str
    TitleLength: int
    SummarySource: str
    Summary: str
    Cache: str
    UpdatedAt: str
    Error: str


@dataclass
class DailyHotItem:
    source: str
    rank: int
    api_title: str
    type_name: str
    title: str
    url: str
    hot: str
    summary_text: str
    from_cache: str
    updated_at: str
    total: str


@dataclass
class WebExtractResult:
    ok: bool
    summary: str = ""
    title: str = ""
    url: str = ""
    error: str = ""


def parse_args() -> CliArgs:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(
        description="Fetch each DailyHotApi source once, run WebExtract for item URLs, and print a Markdown table."
    )
    _ = parser.add_argument(
        "--sources",
        default=os.path.join(script_dir, "sources.txt"),
        help="Path to sources.txt. Blank lines and lines starting with # are ignored.",
    )
    _ = parser.add_argument(
        "--env-file",
        default=os.path.join(script_dir, ".env"),
        help="Path to .env file loaded with the built-in minimal loader.",
    )
    _ = parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help=f"Number of valid items to evaluate per source. Overrides {LIMIT_ENV_NAME}; default: {DEFAULT_LIMIT}.",
    )
    _ = parser.add_argument(
        "--dailyhotapi-base-url",
        default=None,
        help="Override DAILYHOTAPI_BASE_URL from .env or environment.",
    )
    _ = parser.add_argument(
        "--web-extract-url",
        default=None,
        help="Override WEB_EXTRACT_URL from .env or environment.",
    )
    _ = parser.add_argument(
        "--timeout",
        type=float,
        default=15.0,
        help="HTTP timeout in seconds for both DailyHotApi and WebExtract. Default: 15.",
    )
    _ = parser.add_argument(
        "--health",
        action="store_true",
        default=False,
        help="Check source health by inspecting the 'total' field from each source.",
    )
    _ = parser.add_argument(
        "--params",
        action="store_true",
        default=False,
        help="List supported parameters for each source.",
    )
    namespace = parser.parse_args()
    return CliArgs(
        sources=cast(str, namespace.sources),
        env_file=cast(str, namespace.env_file),
        limit=cast(int | None, namespace.limit),
        dailyhotapi_base_url=cast(str | None, namespace.dailyhotapi_base_url),
        web_extract_url=cast(str | None, namespace.web_extract_url),
        timeout=cast(float, namespace.timeout),
        health=cast(bool, namespace.health),
        params=cast(bool, namespace.params),
    )


def load_sources(path: str) -> list[str]:
    sources: list[str] = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            value = line.split("#", 1)[0].strip()
            if not value or value.startswith("#"):
                continue
            sources.append(value)
    return sources


def load_env_file(path: str) -> None:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            for raw_line in handle:
                line = raw_line.strip()
                if not line or line.startswith("#"):
                    continue
                if line.startswith("export "):
                    line = line[len("export ") :].lstrip()
                if "=" not in line:
                    continue
                key, value = line.split("=", 1)
                key = key.strip()
                if not key or key in os.environ:
                    continue
                value = value.strip().strip('"').strip("'")
                os.environ[key] = value
    except FileNotFoundError:
        return


def require_url(value: str | None, name: str) -> str:
    if value is None or not value.strip():
        raise SystemExit(f"Missing {name}. Set it in .env, environment, or CLI option.")
    return value.strip()


def resolve_limit(cli_limit: int | None, env_limit: str | None) -> int:
    if cli_limit is not None:
        limit = cli_limit
    elif env_limit is not None and env_limit.strip():
        try:
            limit = int(env_limit.strip())
        except ValueError as exc:
            raise SystemExit(f"{LIMIT_ENV_NAME} must be an integer.") from exc
    else:
        limit = DEFAULT_LIMIT

    if limit < 1:
        raise SystemExit("item limit must be >= 1")
    return limit


def normalize_base_url(base_url: str, *, add_scheme: bool) -> str:
    normalized = base_url.strip().rstrip("/")
    if add_scheme and "://" not in normalized:
        normalized = "http://" + normalized
    return normalized


def dailyhotapi_endpoint(base_url: str, source: str) -> str:
    base = normalize_base_url(base_url, add_scheme=False)
    return f"{base}/{quote(source, safe='')}"


def web_extract_endpoint(base_url: str) -> str:
    base = normalize_base_url(base_url, add_scheme=True)
    return f"{base}/fetch"


def compact(value: object | None) -> str:
    if value is None:
        return ""
    return WHITESPACE_RE.sub(" ", str(value)).strip()


def mapping_value_as_mapping(value: object) -> JsonObject | None:
    if isinstance(value, dict):
        return cast(JsonObject, value)
    return None


def first_string(root: JsonObject, name: str) -> str:
    candidates = [root.get(name)]
    data = mapping_value_as_mapping(root.get("data"))
    if data is not None:
        candidates.append(data.get(name))
    for candidate in candidates:
        value = compact(candidate)
        if value:
            return value
    return ""


def read_insights(root: JsonObject) -> str:
    insights_value: object | None = root.get("insights")
    data = mapping_value_as_mapping(root.get("data"))
    if not isinstance(insights_value, list) and data is not None:
        insights_value = data.get("insights")
    if not isinstance(insights_value, list):
        return ""
    insights = cast(list[object], insights_value)
    parts: list[str] = []
    for item in insights:
        text = compact(item)
        if text:
            parts.append(text)
    return compact(" ".join(parts))


def load_json_object(body: str) -> JsonObject | None:
    try:
        parsed = cast(object, json.loads(body))
    except json.JSONDecodeError:
        return None
    if isinstance(parsed, dict):
        return cast(JsonObject, parsed)
    return None


def parse_web_extract_body(body: str) -> WebExtractResult:
    root = load_json_object(body)
    if root is None:
        summary = compact(body)
        if not summary:
            return WebExtractResult(False, error="WebExtract returned empty non-JSON body")
        return WebExtractResult(True, summary=summary)

    data = root.get("data")
    data_mapping = mapping_value_as_mapping(data) if data is not None else None
    data_success = data_mapping.get("success") if data_mapping is not None else None
    success = root.get("success")
    if success is None:
        success = data_success
    if success is False:
        message = first_string(root, "message") or "WebExtract success=false"
        return WebExtractResult(False, error=message)

    summary = first_string(root, "summary") or read_insights(root)
    if not summary:
        message = first_string(root, "message") or "WebExtract returned empty summary"
        return WebExtractResult(False, error=message)

    return WebExtractResult(
        True,
        summary=summary,
        title=first_string(root, "title"),
        url=first_string(root, "url"),
    )


def fetch_web_extract(session: requests.Session, endpoint: str, url: str, timeout: float) -> WebExtractResult:
    if not url.strip():
        return WebExtractResult(False, error="No item URL")
    try:
        response = session.post(endpoint, json={"url": url}, timeout=timeout)
    except requests.Timeout:
        return WebExtractResult(False, error="WebExtract timeout")
    except requests.RequestException as exc:
        return WebExtractResult(False, error=f"WebExtract request failed: {exc}")
    return parse_web_extract_body(response.text)


def parse_code(root: JsonObject) -> int | None:
    code = root.get("code")
    if isinstance(code, int):
        return code
    if isinstance(code, str):
        try:
            return int(code.strip())
        except ValueError:
            return None
    return None


def fetch_dailyhot_source(
    session: requests.Session, base_url: str, source: str, timeout: float
) -> tuple[JsonObject | None, JsonList, str]:
    endpoint = dailyhotapi_endpoint(base_url, source)
    try:
        response = session.get(endpoint, timeout=timeout)
    except requests.Timeout:
        return None, [], "DailyHotApi timeout"
    except requests.RequestException as exc:
        return None, [], f"DailyHotApi request failed: {exc}"

    try:
        parsed = cast(object, json.loads(response.text))
    except json.JSONDecodeError as exc:
        return None, [], f"DailyHotApi invalid JSON: {exc}"

    if not isinstance(parsed, dict):
        return None, [], "DailyHotApi response root is not an object"

    root = cast(JsonObject, parsed)

    code = parse_code(root)
    if not response.ok and code != 200:
        return root, [], f"DailyHotApi HTTP {response.status_code}: {response.reason}"

    items = root.get("data")
    if not isinstance(items, list):
        return root, [], "DailyHotApi data is not an array"

    return root, cast(JsonList, items), ""


def choose_text(item_summary: str, web_result: WebExtractResult, title: str) -> tuple[str, str]:
    if item_summary:
        return item_summary, "Description"
    if web_result.ok and web_result.summary:
        return web_result.summary, "UrlFetch"
    return title, "TitleOnly"


def parse_item_text(item: JsonObject, *names: str) -> str:
    for name in names:
        value = compact(item.get(name))
        if value:
            return value
    return ""


def valid_items(source: str, raw_items: JsonList, limit: int) -> list[DailyHotItem]:
    result: list[DailyHotItem] = []
    for index, item in enumerate(raw_items, start=1):
        if not isinstance(item, dict):
            continue

        item_mapping = cast(JsonObject, item)
        title = parse_item_text(item_mapping, "title", "name")
        url = parse_item_text(item_mapping, "url", "mobileUrl", "link")
        summary_text = parse_item_text(item_mapping, "desc", "description", "summary", "hot")
        hot = parse_item_text(item_mapping, "hot", "hotness")
        if not title and not url and not summary_text:
            continue

        result.append(
            DailyHotItem(
                source=source,
                rank=index,
                api_title="",
                type_name="",
                title=title,
                url=url,
                hot=hot,
                summary_text=summary_text,
                from_cache="",
                updated_at="",
                total="",
            )
        )
        if len(result) >= limit:
            break
    return result


def markdown_cell(value: object | None, max_length: int | None = None) -> str:
    text = compact(value).replace("|", "\\|")
    if max_length is not None and len(text) > max_length:
        text = text[: max_length - 3].rstrip() + "..."
    return text


def print_table(rows: Sequence[ReportRow]) -> None:
    columns = [
        "Source",
        "ApiTitle",
        "Type",
        "Rank",
        "Title",
        "Hot",
        "UrlFetch",
        "TitleLength",
        "SummarySource",
        "Summary",
        "Cache",
        "UpdatedAt",
        "Error",
    ]
    print("| " + " | ".join(columns) + " |")
    print("| " + " | ".join("---" for _ in columns) + " |")
    for row in rows:
        print(
            "| "
            + " | ".join(markdown_cell(row.get(column, ""), 180 if column in {"Summary", "Error"} else 100) for column in columns)
            + " |"
        )


def build_error(*parts: str) -> str:
    return "; ".join(part for part in (compact(part) for part in parts) if part)


def extract_params(root: JsonObject) -> JsonObject:
    params = mapping_value_as_mapping(root.get("params"))
    if params is not None:
        return params
    data = mapping_value_as_mapping(root.get("data"))
    if data is not None:
        data_params = mapping_value_as_mapping(data.get("params"))
        if data_params is not None:
            return data_params
    return {}


def health_status(total_str: str) -> str:
    if not total_str:
        return "Unknown"
    try:
        n = int(total_str)
    except ValueError:
        return "Unknown"
    if n > 0:
        return "OK"
    return "Empty"


def print_health_table(
    sources: list[str], session: requests.Session, base_url: str, timeout: float
) -> None:
    columns = ["Source", "Name", "Total", "Status", "Error"]
    print("| " + " | ".join(columns) + " |")
    print("| " + " | ".join("---" for _ in columns) + " |")
    for source in sources:
        root, _items, error = fetch_dailyhot_source(session, base_url, source, timeout)
        name = first_string(root, "title") or first_string(root, "name") if root else ""
        total = first_string(root, "total") if root else ""
        status = health_status(total)
        print(
            "| "
            + " | ".join([
                markdown_cell(source),
                markdown_cell(name),
                markdown_cell(total),
                markdown_cell(status),
                markdown_cell(error),
            ])
            + " |"
        )


def format_param_values(param_def: JsonObject) -> str:
    type_def = mapping_value_as_mapping(param_def.get("type"))
    if type_def is None:
        return "-"
    parts = [f"{k}={compact(v)}" for k, v in type_def.items()]
    return ", ".join(parts)


def print_params_table(
    sources: list[str], session: requests.Session, base_url: str, timeout: float
) -> None:
    columns = ["Source", "Name", "Param", "Description", "Values"]
    print("| " + " | ".join(columns) + " |")
    print("| " + " | ".join("---" for _ in columns) + " |")
    for source in sources:
        root, _items, error = fetch_dailyhot_source(session, base_url, source, timeout)
        if error or root is None:
            print(
                "| "
                + " | ".join([
                    markdown_cell(source),
                    markdown_cell(""),
                    markdown_cell(""),
                    markdown_cell(""),
                    markdown_cell(error or "API error"),
                ])
                + " |"
            )
            continue
        name = first_string(root, "title") or first_string(root, "name")
        params = extract_params(root)
        if not params:
            print(
                "| "
                + " | ".join([
                    markdown_cell(source),
                    markdown_cell(name),
                    markdown_cell("-"),
                    markdown_cell("No parameters"),
                    markdown_cell("-"),
                ])
                + " |"
            )
            continue
        for param_key, param_value in params.items():
            param_def = mapping_value_as_mapping(param_value)
            description = first_string(param_def, "name") if param_def else ""
            values = format_param_values(param_def) if param_def else "-"
            print(
                "| "
                + " | ".join([
                    markdown_cell(source),
                    markdown_cell(name),
                    markdown_cell(param_key),
                    markdown_cell(description),
                    markdown_cell(values, 120),
                ])
                + " |"
            )


def main() -> int:
    args = parse_args()
    if args.timeout <= 0:
        raise SystemExit("--timeout must be > 0")

    load_env_file(args.env_file)
    dailyhotapi_base_url = require_url(
        args.dailyhotapi_base_url or os.getenv("DAILYHOTAPI_BASE_URL"), "DAILYHOTAPI_BASE_URL"
    )

    try:
        sources = load_sources(args.sources)
    except OSError as exc:
        raise SystemExit(f"Failed to read sources file: {exc}") from exc

    if not sources:
        print(f"No active sources found in {args.sources}. Uncomment or add one source per line.", file=sys.stderr)
        return 0

    if args.health or args.params:
        with requests.Session() as session:
            if args.health:
                print_health_table(sources, session, dailyhotapi_base_url, args.timeout)
            if args.params:
                if args.health:
                    print()
                print_params_table(sources, session, dailyhotapi_base_url, args.timeout)
        return 0

    limit = resolve_limit(args.limit, os.getenv(LIMIT_ENV_NAME))
    web_extract_base_url = require_url(args.web_extract_url or os.getenv("WEB_EXTRACT_URL"), "WEB_EXTRACT_URL")
    web_endpoint = web_extract_endpoint(web_extract_base_url)

    rows: list[ReportRow] = []
    with requests.Session() as session:
        for source in sources:
            root, raw_items, source_error = fetch_dailyhot_source(session, dailyhotapi_base_url, source, args.timeout)
            items = valid_items(source, raw_items, limit)
            api_title = ""
            type_name = ""
            from_cache = ""
            updated_at = ""
            total = ""
            if root is not None:
                api_title = first_string(root, "title") or first_string(root, "name")
                type_name = first_string(root, "type")
                from_cache = first_string(root, "fromCache")
                updated_at = first_string(root, "updateTime")
                total = first_string(root, "total")

            if not items:
                empty_row: ReportRow = {
                    "Source": source,
                    "ApiTitle": api_title,
                    "Type": type_name,
                    "Rank": "",
                    "Title": "",
                    "Hot": "",
                    "UrlFetch": "No",
                    "TitleLength": 0,
                    "SummarySource": "",
                    "Summary": "",
                    "Cache": from_cache,
                    "UpdatedAt": updated_at,
                    "Error": build_error(source_error, f"Total={total}" if total else "No valid DailyHotApi items"),
                }
                rows.append(empty_row)
                continue

            for item in items:
                web_result = fetch_web_extract(session, web_endpoint, item.url, args.timeout)
                summary, summary_source = choose_text(item.summary_text, web_result, item.title)
                item_row: ReportRow = {
                    "Source": item.source,
                    "ApiTitle": api_title,
                    "Type": type_name,
                    "Rank": item.rank,
                    "Title": item.title,
                    "Hot": item.hot,
                    "UrlFetch": "Yes" if web_result.ok else "No",
                    "TitleLength": len(item.title),
                    "SummarySource": summary_source,
                    "Summary": summary,
                    "Cache": from_cache,
                    "UpdatedAt": updated_at,
                    "Error": build_error(source_error, "" if web_result.ok else web_result.error),
                }
                rows.append(item_row)

    print_table(rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
