# StormSocket vs SignalR, Kestrel, and the other socket libraries

.NET already has good answers for real-time communication. SignalR and Kestrel ship with the
platform and are supported by Microsoft; NetCoreServer has years of production exposure and about
1.5 million downloads on [nuget.org](https://www.nuget.org/packages/NetCoreServer/). StormSocket has
neither. It is a young library with very little production usage, one maintainer, and a few thousand
downloads.

So this page is not an argument that StormSocket is better. It is an attempt to answer the only
question worth answering — *which of these should you actually use* — and for a lot of readers the
answer will be one of the others. Where that is the case, it says so.

Everything below is either something you can check in this repository, a number we measured and
published the command for, or a documented fact about the other library with the source linked.

## Pick this if

| Your situation | Use |
|---|---|
| Browser front end, JavaScript/TypeScript client you also own, and you want reconnect, transport fallback and scale-out handed to you | **SignalR** |
| Existing ASP.NET Core app, one or two WebSocket endpoints, and you want them on the same port behind the same auth middleware | **Kestrel + `app.UseWebSockets()`** |
| Non-browser peers — devices, game clients, native apps, other services — speaking a protocol you designed, over raw TCP or plain WebSocket | **StormSocket** |
| You also need UDP, a UDP multicast group, or an HTTP server from the same library | **NetCoreServer** |
| RFC 6455 conformance against hostile or sloppy peers is a requirement you have to be able to demonstrate | **StormSocket**, with the Autobahn report from CI |
| Low tolerance for risk: you need vendor support, a large user base, and someone else's production scars | **SignalR or Kestrel**, not StormSocket |

## vs SignalR

SignalR is not a WebSocket library. It is an RPC framework that uses WebSocket as its preferred
transport, and it gives you a large amount that StormSocket has no equivalent for:

- **Hubs.** Client and server call methods on each other; parameters are model-bound. Strongly-typed
  hubs (`Hub<TClient>`) make the client call sites compile-checked.
- **Transport fallback.** WebSockets, then server-sent events, then long polling, chosen
  automatically ([docs](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction)). If a
  corporate proxy eats WebSocket upgrades, SignalR still works and you do not find out.
- **Automatic reconnect** in the clients, with connection management handled for you.
- **Scale-out.** A [Redis backplane](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane)
  or [Azure SignalR Service](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale) so groups
  and broadcasts work across many server instances.
- **Streaming** in both directions, and two hub protocols (JSON and MessagePack).
- **Official clients** for JavaScript/TypeScript, .NET, Java and Swift
  ([supported platforms](https://learn.microsoft.com/en-us/aspnet/core/signalr/supported-platforms)).
  Python and C++ are not among them — the C++ client is documented as experimental and unsupported,
  and Python clients are third-party.

StormSocket has none of that. There is no hub, no RPC, no fallback transport, no backplane, and no
browser client library. You get frames in and frames out, and the protocol on top of them is yours to
design.

**For most browser-facing applications, SignalR is the right answer, and you should not reach for
this library instead.** A chat app, a live dashboard, a notification feed, a collaborative editor
where the peer is a web page you also wrote — SignalR does all of that with less code than
StormSocket, and it degrades gracefully on networks you do not control.

Where SignalR stops fitting is specific:

- **The peer is not a browser and will not take a dependency.** An embedded device, a game client, a
  Rust or Go service, a third party integrating against a published spec. SignalR's wire protocol is
  [documented](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md),
  but "install the SignalR client" is a real ask, and hand-writing a hub protocol implementation is
  worse than hand-writing a WebSocket one.
- **The protocol is already decided.** You are implementing MQTT over WebSocket, a `graphql-ws`
  server, a market data feed with a fixed binary layout, or something a spec tells you the exact
  bytes for. A hub cannot express that; it wants to own the message envelope.
- **You need per-connection control over framing and backpressure.** Which frames are fragmented,
  what happens when one subscriber falls behind, whether a slow consumer is dropped or disconnected.
- **You need raw TCP as well.** SignalR is HTTP-only.
- **You have to demonstrate protocol conformance.** See the Autobahn section below.

The same echo server, on both:

```csharp
// SignalR
public sealed class EchoHub : Hub
{
    public Task Echo(string message) => Clients.Caller.SendAsync("echo", message);
}

app.MapHub<EchoHub>("/echo");
// The peer must speak the SignalR hub protocol — in practice, use a SignalR client.
```

```csharp
// StormSocket
builder.Services.AddStormWebSocketServer().ListenOnAnyIP(8080).AddHandler<EchoHandler>();

public sealed class EchoHandler : IWebSocketHandler
{
    public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken ct)
        => session.SendTextAsync(message.Text, ct);
}
// The peer is any RFC 6455 client, including `new WebSocket("ws://host:8080")`.
```

The difference is not line count. It is what the other end has to be.

## vs Kestrel and `app.UseWebSockets()`

This is the closest comparison and the one most likely to end with "you don't need StormSocket."
`app.UseWebSockets()` plus `System.Net.WebSockets.WebSocket` is a complete, well-tested WebSocket
server, and it is what SignalR itself runs on.

What you get from Kestrel that StormSocket cannot give you at all:

- **One port for HTTP and WebSocket.** Your `/api/*` endpoints and your `/ws` endpoint share a
  listener, a certificate and a hostname. StormSocket binds its own port — see
  [hosting](hosting.md#alongside-aspnet-core).
- **The whole ASP.NET Core pipeline in front of the socket.** Authentication, authorization,
  `HttpContext.User`, routing, CORS, rate limiting middleware, request logging, forwarded headers.
  A WebSocket endpoint is just an endpoint, so `[Authorize]` works on it.
- **TLS termination, HTTP/2 and RFC 8441.** WebSockets over HTTP/2 has been supported since .NET 7
  ([docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets)), which
  StormSocket does not implement.
- **Deployment reality.** IIS, YARP, App Service, ingress controllers, and every proxy in between
  have been tested against Kestrel. They have not been tested against this.
- **Scale.** It is one of the most heavily exercised HTTP servers in existence.

We have not run the Autobahn suite against Kestrel and publish no comparison against it. Nothing on
this page should be read as a claim that StormSocket is more correct than the framework's own
WebSocket implementation.

What Kestrel leaves to you is the part above the socket. The canonical echo endpoint from the
Microsoft docs looks like this:

```csharp
app.UseWebSockets();

app.Map("/echo", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    byte[] buffer = new byte[4096];

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, context.RequestAborted);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
            break;
        }

        await socket.SendAsync(
            buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, context.RequestAborted);
    }
});
```

For one endpoint that echoes, that is fine and you should stop reading here. The loop stops being
fine when the application grows the things a fleet of connections needs, because none of them are in
the box:

| You need | Kestrel | StormSocket |
|---|---|---|
| Receive loop | You write and own it, per endpoint | `OnMessageReceived` / `IWebSocketHandler` |
| Message reassembly across fragments | Loop on `EndOfMessage` yourself into your own buffer | Automatic, bounded by `MaxMessageSize` (4 MB default), 1009 on overflow |
| A registry of live connections | Your own dictionary, and its removal path | `server.Sessions` |
| Broadcast, rooms | Your own fan-out | `BroadcastTextAsync`, `Groups` |
| One slow client stalling a broadcast | Your problem | `SlowConsumerPolicy`: `Wait`, `Drop`, `Disconnect` |
| Cap on concurrent connections, and per source IP | Not a WebSocket-level concept | `MaxConnections`, `MaxConnectionsPerIp`, claimed at accept, before TLS and the upgrade |
| Per-connection message rate limit | ASP.NET Core rate limiting caps the *upgrade request*, not messages on an open socket | `RateLimitMiddleware`, sliding window, per session or per IP, counts control frames |
| Idle timeout on an open connection | You write it | `WebSocketOptions.IdleTimeout` (ping/pong does not reset it) |
| Heartbeat and dead-peer detection | `KeepAliveInterval` sends pings (2 minutes by default); missed-pong counting is yours | `HeartbeatOptions` with `MaxMissedPongs` |
| Why a connection ended | `WebSocketCloseStatus`, plus inference | `DisconnectReason` enum, twelve cases |
| Metrics | ASP.NET Core's HTTP meters; connection-level counters are yours | `System.Diagnostics.Metrics` meter `StormSocket` |
| Raw TCP, custom framing, Unix domain sockets | Kestrel can host non-HTTP protocols via connection handlers, but you are then writing the protocol | `StormTcpServer` with `LengthPrefixFramer`, `DelimiterFramer` or your own `IMessageFramer`; pass a `UnixDomainSocketEndPoint` |

Every row is something you can build on Kestrel in an afternoon, and several of them you should — a
dictionary of sessions is not hard. The trade is whether you want to own that code, its edge cases
and its tests. If your answer is "there is one endpoint and twenty connections," own it. If it is
"forty thousand devices, and I need to know why each one dropped," the arithmetic changes.

The honest summary: **if your peers are browsers and you are already running ASP.NET Core, use
Kestrel.** StormSocket earns its place when the socket is the product rather than a feature of a web
app — device gateways, game backends, market data fan-out, service-to-service links over TCP or a
Unix socket.

## vs NetCoreServer

[NetCoreServer](https://github.com/chronoxor/NetCoreServer) is the closest library in spirit: a
standalone, asynchronous socket library that is not tied to ASP.NET Core. It is also considerably
more established, at roughly 1.5 million downloads against StormSocket's few thousand.

It covers more protocols. Per its README, it supports TCP, SSL, UDP, UDP multicast and Unix domain
sockets at the transport layer, and HTTP, HTTPS, WebSocket and WebSocket-secure above them.
**StormSocket does not do UDP and is not an HTTP server. If you need either, NetCoreServer is the
better fit and this page has nothing to add.**

The differences that remain:

- **API shape.** NetCoreServer is subclass-based: you derive from `TcpServer` and `TcpSession` (or
  the SSL/HTTP/WS equivalents) and override `OnConnected`, `OnReceived`, `OnDisconnected`.
  StormSocket is event-based — `server.OnDataReceived += handler` — or handler-based through DI with
  `IWebSocketHandler`. Neither is better; subclassing gives you a natural place to hang per-connection
  state, events avoid an inheritance chain and compose with a container. Pick the one your codebase
  reads better in.
- **Sockets underneath.** StormSocket is built on `System.IO.Pipelines`, so receive-side backpressure
  propagates to the OS TCP window without you managing buffers. See
  [architecture](architecture.md).
- **Targets.** StormSocket multi-targets net6.0 through net10.0 and depends only on
  `System.IO.Pipelines` and `Microsoft.Extensions.Logging.Abstractions`. NetCoreServer's current
  package, 8.0.7 (December 2023), [targets net8.0](https://www.nuget.org/packages/NetCoreServer/).
- **Higher-level plumbing.** Groups, slow-consumer policy, per-IP connection limits, rate limiting
  middleware, disconnect reasons and a metrics meter are in StormSocket's box; on NetCoreServer they
  are yours to write.

### Protocol conformance

The [Autobahn Testsuite](https://github.com/crossbario/autobahn-testsuite) is the reference test for
RFC 6455. It throws malformed frames, truncated UTF-8, reserved opcodes and bad close codes at a
server and judges the reply and the close — and it does the same in reverse, playing a hostile server
against a client. StormSocket 5.2.0 passes **247/247** on the server and **463/463** on the client
across the correctness sections. Both runs are in CI on every push
(`.github/workflows/autobahn.yml`) and the full reports are published as build artifacts, so these
are not numbers you have to take on faith.

We also ran the same suite, on the same machine on the same day, against NetCoreServer 8.0.7 behind a
faithful echo harness, and recorded **130/247**. The failures clustered in UTF-8 validation (75),
fragmentation (18), opcode handling (10), ping/pong (8) and reserved bits (6).

Read that with the caveats it deserves:

- The harness we used is not committed in this repository, so by the standard this project sets for
  itself in the [README](../README.md#benchmarks) — competing harness committed, versions pinned,
  hardware disclosed — the number is reproducible in principle and not yet published evidence.
  Run it yourself before relying on it. The spec file is `benchmark/autobahn/fuzzingclient.json`;
  point it at a NetCoreServer `WsServer` on port 9001 that echoes text and binary messages back.
- NetCoreServer does not advertise Autobahn conformance, so this is a measurement, not a broken
  promise.
- It matters exactly as much as your threat model says it does. Two of your own services on a
  private network exchanging well-formed frames will never notice. A public endpoint, a browser
  fleet, or a peer implemented by someone else is a different situation: invalid UTF-8 that is
  silently replaced rather than rejected is data corruption, and unvalidated close codes and
  reserved bits are how a peer drives your server outside the protocol.

StormSocket's own 5.0 release notes describe the same class of bugs being found and fixed in *this*
library by running the suite — see the [changelog](../CHANGELOG.md). The point is the suite, not the
scoreboard.

## vs Fleck, websocket-sharp and WatsonWebsocket

**[Fleck](https://github.com/statianzo/Fleck)** is a small, callback-based WebSocket *server* — no
client — with no dependencies, and it is pleasant to use for exactly that. Its
[current package](https://www.nuget.org/packages/Fleck) is 1.2.0 from April 2021, covering
.NET Standard 2.0, .NET Core 2.0 and .NET Framework 4.0. If you want a WebSocket server in fifteen
lines and nothing else, Fleck is still a reasonable choice.

**[websocket-sharp](https://github.com/sta/websocket-sharp)** provides a client and a server and is
widely embedded, but the published package targets the .NET Framework era: `WebSocketSharp` on NuGet
is [1.0.3-rc11 from July 2016](https://www.nuget.org/packages/WebSocketSharp), still a prerelease.
For new work on modern .NET it is not the place to start.

**[WatsonWebsocket](https://github.com/jchristn/WatsonWebsocket)** wrapped the operating system's
WebSocket support in a simple event-based client and server — its README notes the dependency on OS
support and the resulting host-header and certificate-store constraints. The repository was archived
in May 2026, with the functionality folded into Watson Webserver.

## What StormSocket does not do

- **No UDP.** TCP and WebSocket only. NetCoreServer or a raw `UdpClient` for that.
- **No HTTP server.** It parses exactly enough HTTP to handle the WebSocket upgrade, and answers
  nothing else.
- **No scale-out backplane.** `Groups` and `Sessions` are per-process. Two instances behind a load
  balancer do not share a room; you need your own bus between them. SignalR with Redis or Azure
  SignalR solves this out of the box, and this library does not.
- **No browser client library.** The browser's own `WebSocket` works against it, but there is no
  StormSocket JavaScript package, no reconnect helper, no protocol client. The .NET client
  (`StormWebSocketClient`) does reconnect automatically.
- **It does not share Kestrel's port.** The server binds its own. HTTP and WebSocket on one origin
  means a reverse proxy in front, or Kestrel.
- **No HTTP/2 or RFC 8441 WebSockets**, and no transport fallback for networks that block upgrades.
- **Very little production exposure.** A few thousand downloads, one maintainer, and a 5.0 release
  whose notes are largely a list of remotely reachable defects found by finally running a conformance
  suite. The tests, the suite and the CI are there because of that history, not instead of it. Weigh
  it accordingly.

## The measured numbers

For completeness, since a comparison page invites the question. All from an Apple M-series laptop,
.NET 9, server GC, loopback, client and server on the same host — a shape, not a spec sheet. The
commands are in the [README](../README.md#benchmarks).

| What | Result |
|---|---|
| Echo round-trip, pipeline depth 1, 1 connection | p50 46 us, p99 81 us |
| Echo round-trip, pipeline depth 1, 50 connections | p50 276 us, p99 505 us |
| Saturation throughput, 100 connections, deep pipeline | TCP 1.03 GiB/s, WebSocket ~76 MiB/s at 32-byte messages |
| Frame decode, best of five | 58 ns at 32 B, 60 ns at 128 B, 109 ns at 1 KB, 486 ns at 8 KB |
| Server-side allocation | 109 bytes per message over a 25M-message run |

There is no published head-to-head against SignalR, Kestrel or NetCoreServer on any of these, and
until a comparative harness is committed here alongside ours there will not be. Numbers you cannot
reproduce are not worth printing.
