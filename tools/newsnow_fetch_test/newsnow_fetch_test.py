#!/usr/bin/env python3
"""Fetch NewsNow sources once and print a Markdown diagnostic table."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass
from typing import Any
from urllib.parse import urlencode

import requests
from dotenv import load_dotenv

ACCEPTED_NEWSNOW_STATUSES = {"success", "cache"}
DEFAULT_LIMIT = 3
LIMIT_ENV_NAME = "NEWS_ITEM_LIMIT"
WHITESPACE_RE = re.compile(r"\s+")


@dataclass
class NewsItem:
    source: str
    rank: int
    title: str
    url: str
    summary_text: str


@dataclass
class WebExtractResult:
    ok: bool
    summary: str = ""
    title: str = ""
    url: str = ""
    error: str = ""


def parse_args() -> argparse.Namespace:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(
        description="Fetch each NewsNow source once, run WebExtract for item URLs, and print a Markdown table."
    )
    parser.add_argument(
        "--sources",
        default=os.path.join(script_dir, "sources.txt"),
        help="Path to sources.txt. Blank lines and lines starting with # are ignored.",
    )
    parser.add_argument(
        "--env-file",
        default=os.path.join(script_dir, ".env"),
        help="Path to .env file loaded with python-dotenv.",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help=f"Number of valid items to evaluate per source. Overrides {LIMIT_ENV_NAME}; default: {DEFAULT_LIMIT}.",
    )
    parser.add_argument(
        "--newsnow-base-url",
        default=None,
        help="Override NEWSNOW_BASE_URL from .env or environment.",
    )
    parser.add_argument(
        "--web-extract-url",
        default=None,
        help="Override WEB_EXTRACT_URL from .env or environment.",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=15.0,
        help="HTTP timeout in seconds for both NewsNow and WebExtract. Default: 15.",
    )
    return parser.parse_args()


def load_sources(path: str) -> list[str]:
    sources: list[str] = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            value = line.split("#", 1)[0].strip()
            if not value or value.startswith("#"):
                continue
            sources.append(value)
    return sources


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


def newsnow_endpoint(base_url: str, source: str) -> str:
    base = normalize_base_url(base_url, add_scheme=False)
    return f"{base}/api/s?{urlencode({'id': source})}"


def web_extract_endpoint(base_url: str) -> str:
    base = normalize_base_url(base_url, add_scheme=True)
    return f"{base}/fetch"


def compact(value: Any) -> str:
    if value is None:
        return ""
    return WHITESPACE_RE.sub(" ", str(value)).strip()


def first_string(root: dict[str, Any], name: str) -> str:
    candidates = [root.get(name)]
    data = root.get("data")
    if isinstance(data, dict):
        candidates.append(data.get(name))
    for candidate in candidates:
        value = compact(candidate)
        if value:
            return value
    return ""


def read_insights(root: dict[str, Any]) -> str:
    insights = root.get("insights")
    data = root.get("data")
    if not isinstance(insights, list) and isinstance(data, dict):
        insights = data.get("insights")
    if not isinstance(insights, list):
        return ""
    return compact(" ".join(compact(item) for item in insights if compact(item)))


def parse_web_extract_body(body: str) -> WebExtractResult:
    try:
        root = json.loads(body)
    except json.JSONDecodeError:
        summary = compact(body)
        if not summary:
            return WebExtractResult(False, error="WebExtract returned empty non-JSON body")
        return WebExtractResult(True, summary=summary)

    if not isinstance(root, dict):
        summary = compact(body)
        if not summary:
            return WebExtractResult(False, error="WebExtract returned empty JSON body")
        return WebExtractResult(True, summary=summary)

    data = root.get("data")
    data_success = data.get("success") if isinstance(data, dict) else None
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


def fetch_newsnow_source(session: requests.Session, base_url: str, source: str, timeout: float) -> tuple[list[Any], str]:
    endpoint = newsnow_endpoint(base_url, source)
    try:
        response = session.get(endpoint, timeout=timeout)
        body = response.text
    except requests.Timeout:
        return [], "NewsNow timeout"
    except requests.RequestException as exc:
        return [], f"NewsNow request failed: {exc}"

    if not response.ok:
        return [], f"NewsNow HTTP {response.status_code}: {response.reason}"

    try:
        root = response.json()
    except json.JSONDecodeError as exc:
        return [], f"NewsNow invalid JSON: {exc}"

    if not isinstance(root, dict):
        return [], "NewsNow response root is not an object"

    status = compact(root.get("status"))
    if status.lower() not in ACCEPTED_NEWSNOW_STATUSES:
        return [], f"NewsNow unsupported status: {status or '(empty)'}"

    items = root.get("items")
    if not isinstance(items, list):
        return [], "NewsNow items is not an array"
    return items, ""


def valid_items(source: str, raw_items: list[Any], limit: int) -> list[NewsItem]:
    result: list[NewsItem] = []
    for index, item in enumerate(raw_items, start=1):
        if not isinstance(item, dict):
            continue
        title = compact(item.get("title"))
        url = compact(item.get("url"))
        if not title and not url:
            continue
        extra = item.get("extra")
        summary_text = compact(extra.get("hover")) if isinstance(extra, dict) else ""
        result.append(NewsItem(source=source, rank=index, title=title, url=url, summary_text=summary_text))
        if len(result) >= limit:
            break
    return result


def choose_summary(item: NewsItem, web_result: WebExtractResult) -> tuple[str, str]:
    if item.summary_text:
        return item.summary_text, "SummaryText"
    if web_result.ok and web_result.summary:
        return web_result.summary, "UrlFetch"
    return item.title, "TitleOnly"


def markdown_cell(value: Any, max_length: int | None = None) -> str:
    text = compact(value).replace("|", "\\|")
    if max_length is not None and len(text) > max_length:
        text = text[: max_length - 3].rstrip() + "..."
    return text


def print_table(rows: list[dict[str, Any]]) -> None:
    columns = [
        "Source",
        "Rank",
        "Title",
        "SummaryText",
        "UrlFetch",
        "TitleLength",
        "SummarySource",
        "Summary",
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


def main() -> int:
    args = parse_args()
    if args.timeout <= 0:
        raise SystemExit("--timeout must be > 0")

    load_dotenv(args.env_file)
    limit = resolve_limit(args.limit, os.getenv(LIMIT_ENV_NAME))
    newsnow_base_url = require_url(args.newsnow_base_url or os.getenv("NEWSNOW_BASE_URL"), "NEWSNOW_BASE_URL")
    web_extract_base_url = require_url(args.web_extract_url or os.getenv("WEB_EXTRACT_URL"), "WEB_EXTRACT_URL")
    web_endpoint = web_extract_endpoint(web_extract_base_url)

    try:
        sources = load_sources(args.sources)
    except OSError as exc:
        raise SystemExit(f"Failed to read sources file: {exc}") from exc

    rows: list[dict[str, Any]] = []
    if not sources:
        print_table(rows)
        print(f"No active sources found in {args.sources}. Uncomment or add one source per line.", file=sys.stderr)
        return 0

    with requests.Session() as session:
        for source in sources:
            raw_items, source_error = fetch_newsnow_source(session, newsnow_base_url, source, args.timeout)
            items = valid_items(source, raw_items, limit)
            if not items:
                rows.append(
                    {
                        "Source": source,
                        "Rank": "",
                        "Title": "",
                        "SummaryText": "No",
                        "UrlFetch": "No",
                        "TitleLength": 0,
                        "SummarySource": "",
                        "Summary": "",
                        "Error": source_error or "No valid NewsNow items",
                    }
                )
                continue

            for item in items:
                web_result = fetch_web_extract(session, web_endpoint, item.url, args.timeout)
                summary, summary_source = choose_summary(item, web_result)
                rows.append(
                    {
                        "Source": item.source,
                        "Rank": item.rank,
                        "Title": item.title,
                        "SummaryText": "Yes" if item.summary_text else "No",
                        "UrlFetch": "Yes" if web_result.ok else "No",
                        "TitleLength": len(item.title),
                        "SummarySource": summary_source,
                        "Summary": summary,
                        "Error": build_error(source_error, "" if web_result.ok else web_result.error),
                    }
                )

    print_table(rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
