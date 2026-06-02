# TrendReporter2 Grafana 监控接入

## 1. 数据源配置

在 Grafana 中添加 PostgreSQL 数据源：

| 配置项 | 值 |
| --- | --- |
| **Name** | `TrendReporter2` |
| **Host** | PostgreSQL 主机地址（与 `config.yaml` 中 `database.connectionString` 相同） |
| **Database** | trendreporter（或你的实际库名） |
| **User / Password** | 最低权限只读用户 |
| **TLS/SSL Mode** | 按实际环境配置 |
| **Min time interval** | `5m`（抓取间隔通常为 1 小时，5 分钟足够） |

**最低权限用户创建（可选）：**

```sql
CREATE ROLE grafana_reader WITH LOGIN PASSWORD '<密码>';
GRANT CONNECT ON DATABASE trendreporter TO grafana_reader;
GRANT USAGE ON SCHEMA metrics TO grafana_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA metrics TO grafana_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA metrics GRANT SELECT ON TABLES TO grafana_reader;
```

## 2. 推荐面板

### 2.1 Run Success Rate（时间序列）

**用途：** 监控每轮抓取的健康度，快速发现连续失败或部分成功。

**面板类型：** Time series

**查询：**

```sql
SELECT
    run_date AS time,
    success_rate_pct
FROM metrics.run_success_rate
WHERE $__timeFilter(run_date)
ORDER BY run_date;
```

**配置建议：**
- 添加 Threshold：`80`（红色），`95`（绿色）
- Unit: `Percent (0-100)`
- 叠加 total_runs 作为右侧 Y 轴（柱状图）

---

### 2.2 Per-Source Failure Rate（按源分面时间序列）

**用途：** 发现哪些信息源频繁失败，及时排查或禁用。

**面板类型：** Time series（分面）

**查询：**

```sql
SELECT
    fetch_date AS time,
    failure_rate_pct,
    display_name AS metric
FROM metrics.run_source_failure_rate
WHERE $__timeFilter(fetch_date)
ORDER BY fetch_date;
```

**配置建议：**
- Legend: 右侧，按 display_name
- Threshold: `50`（黄色），`100`（红色）

---

### 2.3 LLM Daily Cost（折线图）

**用途：** 追踪 LLM 调用成本趋势，发现异常飙升。

**面板类型：** Time series

**查询：**

```sql
SELECT
    usage_date AS time,
    total_estimated_cost
FROM metrics.llm_cost_trend_7d
ORDER BY usage_date;
```

**配置建议：**
- Unit: `currencyUSD`
- 叠加 call_count 作为右侧 Y 轴（虚线）

---

### 2.4 LLM Cost by Stage（饼图 / 堆叠柱状图）

**用途：** 了解 LLM 成本在各 stage（cluster、judge、tagging、embedding）之间的分布。

**面板类型：** Pie chart 或 Bar chart（堆叠）

**查询：**

```sql
SELECT
    stage,
    total_cost,
    cost_pct
FROM metrics.llm_stage_cost_pct;
```

**配置建议：**
- Pie chart: Value = total_cost, Label = stage
- Bar chart: 堆叠模式，X = stage, Y = cost_pct

---

### 2.5 Stage Duration P50/P95（按 stage 分面）

**用途：** 发现哪个阶段是性能瓶颈，例如 enrichment 或 secondary_merge 耗时过长。

**面板类型：** Time series（分面）

**查询（P95）：**

```sql
SELECT
    run_date AS time,
    p95_duration_ms,
    stage AS metric
FROM metrics.run_stage_duration
WHERE $__timeFilter(run_date)
ORDER BY run_date;
```

**查询（P50）：**

```sql
SELECT
    run_date AS time,
    p50_duration_ms,
    stage AS metric
FROM metrics.run_stage_duration
WHERE $__timeFilter(run_date)
ORDER BY run_date;
```

**配置建议：**
- 两个面板上下排列（P50 在上，P95 在下）
- Unit: `milliseconds (ms)`
- 也可以在一个面板里使用 Transform: "Prepare time series" -> "Multi-frame" 合并展示

---

### 2.6 Event Score Distribution（柱状图 / 仪表盘）

**用途：** 当前活跃事件的分数分布，快速了解重要事件密度。

**面板类型：** Bar chart（垂直）或 Gauge

**查询：**

```sql
SELECT
    score_bucket,
    event_count
FROM metrics.event_score_distribution
ORDER BY score_bucket;
```

**配置建议：**
- Bar chart: X = score_bucket, Y = event_count
- 颜色：0-30（灰）、30-60（蓝）、60-80（橙）、80-100（红）
- 也可用 Stat 面板展示 80-100 分段事件数

---

### 2.7 Daily Events / Pushes（双轴时间序列）

**用途：** 每日事件发现量和推送量的对比。

**面板类型：** Time series（双 Y 轴）

**查询：**

```sql
SELECT
    event_date AS time,
    new_events,
    pushed_events,
    instant_pushes,
    digest_pushes
FROM metrics.event_daily_counts
WHERE $__timeFilter(event_date)
ORDER BY event_date;
```

**配置建议：**
- Y 轴 1（左）：new_events（柱状图，浅色）
- Y 轴 2（右）：instant_pushes + digest_pushes（折线）
- 使用 Transform "Prepare time series" 拆分字段

---

### 2.8 Latest Run Summary（表格 / Stat 面板）

**用途：** 一眼看到最新一轮抓取的关键数据。

**面板类型：** Stat（多值）或 Table

**查询：**

```sql
SELECT
    run_id,
    status,
    duration_minutes,
    source_count,
    success_source_count,
    failure_source_count,
    fetched_item_count,
    matched_event_count,
    pushed_event_count,
    estimated_llm_cost,
    fetch_duration_ms,
    match_duration_ms,
    score_duration_ms,
    started_at,
    finished_at
FROM metrics.latest_run_summary;
```

**配置建议：**
- 使用多个 Stat 面板：Status、Duration (min)、Items Fetched、Events Matched、LLM Cost
- Table 面板展示 stage 耗时明细（fetch_duration_ms, match_duration_ms 等）

---

## 3. 告警建议

以下告警规则可在 Grafana Alerting 中配置，需先关联通知渠道（Email、Webhook 等）。

### 3.1 运行连续失败

**条件：** 连续 3 次 fetch_run 成功率 < 80%

**查询（Grafana Alert condition）：**

```sql
SELECT
    run_date AS time,
    success_rate_pct
FROM metrics.run_success_rate
WHERE run_date >= now() - interval '3 days'
ORDER BY run_date DESC
LIMIT 3;
```

**Grafana 配置：**
- Reduce: `Min` of `success_rate_pct`
- Threshold: `IS BELOW 80`
- Evaluation: every `1h`, for `5m`

---

### 3.2 LLM 成本异常飙升

**条件：** 当日 LLM 成本超过前 7 天日均值的 2 倍

**查询（Grafana Alert condition）：**

```sql
WITH today_cost AS (
    SELECT sum(estimated_cost) AS cost
    FROM llm_usage
    WHERE created_at >= date_trunc('day', now())
),
avg_7d AS (
    SELECT sum(estimated_cost) / 7.0 AS avg_daily_cost
    FROM llm_usage
    WHERE created_at >= now() - interval '7 days'
      AND created_at < date_trunc('day', now())
)
SELECT
    coalesce((SELECT cost FROM today_cost), 0) AS today_cost,
    coalesce((SELECT avg_daily_cost FROM avg_7d), 0) AS avg_daily_cost,
    case
        when (SELECT avg_daily_cost FROM avg_7d) > 0
             and (SELECT cost FROM today_cost) > 2.0 * (SELECT avg_daily_cost FROM avg_7d)
        then 1
        else 0
    end as alert_flag
FROM (SELECT 1) AS dummy;
```

**Grafana 配置：**
- Reduce: `Last` of `alert_flag`
- Threshold: `IS ABOVE 0`
- Evaluation: every `1h`

---

### 3.3 单源连续全失败

**条件：** 某个 source 连续 3 次抓取全部失败

**查询：**

```sql
SELECT
    source_id,
    display_name,
    failure_rate_pct
FROM metrics.run_source_failure_rate
WHERE fetch_date >= now() - interval '3 days'
  AND failure_rate_pct = 100
GROUP BY source_id, display_name
HAVING count(*) >= 3;
```

**建议：** 此告警适合配置为 Grafana Alert（当返回行时触发），或作为定期巡检 SQL。

---

## 4. 常用时间范围

| 场景 | 推荐范围 | 刷新 |
| --- | --- | --- |
| 即时运行健康 | Last 24 hours | 5m |
| LLM 成本趋势 | Last 7 days | 1h |
| 运行成功率 | Last 30 days | 1h |
| Stage 耗时变化 | Last 30 days | 1h |
| 事件/推送趋势 | Last 30 days | 1h |

## 5. 排错

### 视图查询为空

确认数据库中有数据：`SELECT count(*) FROM fetch_run;`

如果没有数据，视图会返回空结果（而非错误），Grafana 面板会显示 "No data"。

### 视图权限不足

Grafana 使用的数据库用户需要 `metrics` schema 的 `SELECT` 权限。参见第 1 节的最低权限 SQL。

### 新 migration 未执行

监控视图由 `0009_monitoring_views.sql` 管理。如果启动时未执行到该 migration，检查 `schema_migration` 表确认版本。视图可以随时通过 `metrics` schema 下的 `CREATE OR REPLACE VIEW` 手动更新。
