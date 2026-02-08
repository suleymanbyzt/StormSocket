---
_layout: landing
---

# StormSocket

**Modern, high-performance, event-based TCP/WebSocket/SSL library for .NET** built on `System.IO.Pipelines`.

[![NuGet](https://img.shields.io/nuget/v/StormSocket)](https://www.nuget.org/packages/StormSocket)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/suleymanbyzt/StormSocket/blob/master/LICENSE)

## Features

- **Event-based API** — subscribe to `OnConnected`, `OnDataReceived`, `OnDisconnected` with simple delegates
- **TCP & WebSocket** — full RFC 6455 WebSocket support alongside raw TCP
- **SSL/TLS** — first-class `SslStream` integration for secure connections
- **Auto-reconnect** — clients reconnect automatically with configurable backoff
- **Message framing** — built-in length-prefix, delimiter, and raw framers (or bring your own)
- **Session management** — track connected clients, broadcast, and group messaging
- **Middleware pipeline** — plug in authentication, logging, rate limiting, and more
- **Rate limiting** — built-in middleware with per-session or per-IP limiting
- **Backpressure** — slow consumer detection with configurable policies
- **Multi-target** — supports net6.0 through net10.0
- **Zero dependencies** — only `System.IO.Pipelines`

## Quick Install

```bash
dotnet add package StormSocket
```

## Quick Example

```csharp
using System.Net;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
});

server.OnDataReceived += async (session, data) =>
{
    await session.SendAsync(data); // echo
};

await server.StartAsync();
```

## Next Steps

- [Getting Started](docs/getting-started.md) — installation, first TCP server, first WebSocket server
- [Middleware](docs/middleware.md) — pipeline, rate limiting, custom middleware
- [API Reference](api/StormSocket.html) — full API documentation generated from XML comments
