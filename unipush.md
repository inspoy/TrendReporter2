Unipush是一个我自建的消息推送服务

Unipush本质上是一个webhook

Unipush的格式

发送POST请求到指定的url，需要在url里拼接参数`channels`

例如：`https://api.example.com/push?channels=gotify`

鉴权方式：在请求头中加入`Push-Key: xxxxxx`

提交请求体：
```json
{
  "cate": "[REQUIRED] 分类(每个渠道支持的分类可能不同)",
  "title": "[OPTIONAL] 标题",
  "msg": "[REQUIRED] 消息内容",
  "link": "[OPTIONAL] 链接"
}
```

TrendReporter2中，cate固定为"default"