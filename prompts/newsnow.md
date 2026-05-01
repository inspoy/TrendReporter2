newsnow是一个新闻聚合服务，用于获取最新的新闻列表

主要接口：/api/s?id=source
方法：GET

source在config里面有配置

返回样例：
```json
{
  status: "success" | "cache",
  id: string,
  updatedTime: number, // Unix时间戳，毫秒
  items: NewsItem[]
}
```

NewsItem是单个新闻的数据结构：
```json
{
  id: string | number,          // 唯一标识符
  title: string,                // 新闻标题
  url: string,                  // 完整文章链接
  mobileUrl?: string,           // 移动端优化链接
  pubDate?: number | string,    // 发布时间戳
  extra?: {
    hover?: string,             // 悬停预览文本
    date?: number | string,     // 格式化日期
    info?: false | string,      // 附加元数据
    diff?: number,              // 时间差
    icon?: false | string | {   // 来源图标
      url: string,
      scale: number
    }
  }
}
```