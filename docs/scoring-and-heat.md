# 即时推送中的 Score 与 Heat

本文解释即时推送消息里 `Score` 和 `Heat` 的计算规则。它们都是事件级指标，描述一个事件在当前抓取和近期趋势中的表现，不是单条新闻的指标。

即时推送中，`Score` 通常显示 1 位小数，`Heat` 通常显示 2 位小数。显示精度只影响消息里的呈现方式，不改变计算结果本身。

## 参数从哪里来

每条新闻在抓取时都会带有三个基础信息：来自哪个信源、排在该信源列表的第几位、该信源列表一共有多少条新闻。由于不同信源的列表长度可能不同，排名不能直接相加，需要先在各自信源列表内归一化。

随后，系统会把判断为描述同一件事的新闻聚合成一个事件。当前这轮抓取中，挂到同一事件下的新闻就是本轮事件证据。`Heat` 来自这些当前证据的归一化排名之和，`Score` 的覆盖度、排名、趋势等分量也从事件证据和近期历史中得到。

近期历史来自趋势窗口内保存的热度样本。趋势窗口、最少样本数、目标信源数、历史小时数、重复推送阈值等参数来自配置。可选的 LLM 重要性判断会给事件一个有上限的加分，用来修正纯规则评分无法覆盖的语义重要性。

## 单条新闻的归一化排名

归一化排名把单条新闻在某个信源列表中的位置压到 0 到 1 之间。列表越靠前，分值越高。

```text
if sourceListSize <= 1:
    normalizedRankScore = 1
else:
    normalizedRankScore = clamp(1 - (rank - 1) / (sourceListSize - 1), 0, 1)
```

其中 `rank` 是新闻在该信源列表中的排名，`sourceListSize` 是该列表总长度。`clamp(value, 0, 1)` 表示把结果限制在 0 到 1 之间。

当列表只有 1 条新闻，或列表长度无法形成有效区间时，归一化排名记为 1。正常情况下，榜首接近 1，列表末尾接近 0。

## Heat 怎么算

`Heat` 表示一个事件在当前这轮抓取中的热度。它只看本轮证据，不直接使用历史样本。

```text
Heat = Σ normalizedRankScore
```

求和范围是当前这轮抓取中，所有被归到同一事件下的新闻证据。一个事件被多个信源同时排在靠前位置时，`Heat` 会更高。

例如，某事件本轮有 3 条证据，归一化排名分别是 `1.00`、`0.50`、`0.00`，则：

```text
Heat = 1.00 + 0.50 + 0.00 = 1.50
```

即时推送会显示为 `Heat=1.50`。

## 趋势分怎么来

趋势分衡量事件在近期是否升温。它使用趋势窗口内的历史热度样本，并追加当前这轮的 `Heat`，形成一条热度序列。

为了减少单次波动，热度序列会先做 EWMA 平滑，alpha 固定为 `0.5`。第一个值直接初始化序列，之后每个值按下面的方式更新：

```text
smoothed = 0.5 * currentHeat + 0.5 * previousSmoothed
```

平滑后的序列用于计算趋势分。规则如下：

```text
if heatSeriesLength < 3:
    trend = 0
else:
    trend = clamp((recentAvg - pastAvg) / max(pastAvg, 0.2), 0, 1)
```

这里的 `pastAvg` 是平滑序列前半段的平均值，`recentAvg` 是后半段的平均值。样本少于 3 个时，趋势信息不足，趋势分为 0。分母中的 `0.2` 是下限，避免过去热度很低时把微小增长放大得过高。

即时推送展示的是当前原始 `Heat`，不是平滑后的热度。

## Score 怎么算

`Score` 是事件的综合评分，用于表达它是否值得即时推送以及优先级有多高。公式如下：

```text
Score = 100 * (
    0.35 * coverage +
    0.25 * rank +
    0.20 * trend +
    0.10 * persistence +
    0.10 * llmBoost
) + reactivationBonus
```

各分量含义如下：

- `coverage`：信源覆盖度。当前事件证据中，不同信源数量相对配置目标信源数 `analysis.event.sourceCount` 的比例，结果限制在 0 到 1。
- `rank`：平均排名质量。当前事件证据的归一化排名平均值，结果限制在 0 到 1。
- `trend`：趋势分。来自趋势窗口内热度序列的升温程度，结果限制在 0 到 1。
- `persistence`：持续时间分。事件从首次出现到当前的持续小时数，相对配置的 `analysis.historyHours` 计算，结果限制在 0 到 1。
- `llmBoost`：LLM 重要性加分。可选的重要性判断给出的加分，结果限制在 0 到 1。
- `reactivationBonus`：重新活跃奖励。沉寂后重新活跃的事件会获得固定加分，否则为 0。

一个简单例子：某事件本轮来自 3 个不同信源，配置的目标信源数也是 3，3 条证据的归一化排名是 `1.00`、`0.50`、`0.00`。此时 `coverage = 1.00`，`rank = 0.50`，`Heat = 1.50`。如果 `trend = 0.25`，`persistence = 0.50`，`llmBoost = 0.20`，且没有重新活跃奖励，则：

```text
Score = 100 * (0.35 * 1.00 + 0.25 * 0.50 + 0.20 * 0.25 + 0.10 * 0.50 + 0.10 * 0.20) + 0
      = 59.5
```

即时推送会显示为 `Score=59.5,Heat=1.50`。

## 阈值影响什么

配置里的阈值会影响事件能否被推送，但不会改变 `Score` 和 `Heat` 的显示精度，也不会替代上面的计算公式。

常见阈值包括：

- `analysis.event.sourceCount`：首次推送或覆盖条件需要达到的独立信源目标。
- `analysis.event.normalizedRankThreshold`：覆盖排名触发条件需要达到的平均归一化排名。
- `analysis.event.minTrendSamples`：趋势触发需要的最少历史样本数。
- `analysis.event.minTrendHeat`：趋势触发需要达到的最低热度。
- `analysis.repeatPush.sourceAddThreshold`：重复推送时需要新增的信源数量。
- `analysis.repeatPush.rankScoreImproveThreshold`：重复推送时需要达到的排名分提升。
- `analysis.repeatPush.scoreImproveThreshold`：重复推送时需要达到的总分提升。

黑名单也会影响推送资格。LLM 重要性判断除了提供 `llmBoost`，也可能让语义上重要的事件进入推送资格判断。
