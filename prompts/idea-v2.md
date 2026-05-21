进一步增强可观测性，包括每次抓取的耗时，成本

事件召回过程，使用向量数据库(候选pgvector)

定期摘要报告，除了推送之外，再生成一个静态网页（或者动态web应用也行），目的是直观地看到当前热点事件，并且每个事件都有相关的新闻列表，每条新闻都可以点击查看原始链接

支持更多的source，V1只有News，V2尝试增加社媒话题Topic

newsnow的一些source，类型是“快讯”，没有排名，重点是`extra.date`，考虑增加快讯的支持，比如同时从多个信源监控到相同的事件，也认为是重要事件，应该立即推送
- 注意，不同source的`extra.date`还不一样，有的是unix时间戳，有的是字符串

除了newsnow，新增`DailyHotApi`作为新的`INewsSourceClient`

考虑给每条新闻/事件增加若干Tag，用户可以根据tag来快速检索感兴趣的事件
- 脑洞：也可以在config里订阅感兴趣的tag，作为立即推送的优先判定条件
- 脑洞2：除了摘要web页，还考虑加一个dashboard，总览全局，比如tag云

LLM调用优化：

1. 由于LLM返回的结果不稳定，每次调用LLM时，都给3次重试机会，重试次数不走配置了，定义一个`const int`就行
2. 每次FetchJob，都统计一下这次调用了多少次LLM，每个LlmClient消耗了多少Token，根据配置的单价计算成本，打印在控制台Log里

迁移LiteDB到外部PostgreSQL（Dapper/npgsql），同时也方便后续接入Grafana

事件二次归并（目前的归并策略偏保守，可能需要设计二次归并策略，把原本分开的事件合并起来）

进一步汉化（事件进程和sourceid，都要mapping到中文）

如果newsnow已经不能满足需求，探索一下其他可能性（另一个NewsClient，或者自己fork一份newsnow）