# SuperSocket.Server.AspNetCore

使用 Asp.Net Core Kestrel 作为 TCP 和 WebSocket 服务器。

### 原因

在实际使用中，发现自带的 Server 在某些 k8s 集权环境中发送图片或者大数据包（100+ kb）的时候，会阻塞，发不到客户端。改用 Kestrel 之后，能够正常发送。

### 限制

目前只支持单个 TCP 和 WebSocket 服务
