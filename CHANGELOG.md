# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [5.0.0]

A correctness and hardening release. The WebSocket layer framed and routed messages correctly but
never validated them, so peers could drive the server outside the protocol; several of those paths
were remotely reachable. Every change below is covered by a regression test.

### Security

- **permessage-deflate decompression is now bounded.** `Decompress` inflated into an unbounded
  `MemoryStream`, so a single frame that passed `MaxFrameSize` could expand without limit — a 261 KB
  frame produced a 256 MB message and drove the process to 690 MB. Inflation now stops at
  `MaxMessageSize` and fails the connection with 1009.
- **The HTTP upgrade request is now bounded and scanned incrementally.** There was no header size or
  count limit, and every read rescanned the whole accumulated buffer from byte 0 with a byte-by-byte
  matcher. One connection could push 30 MB in the handshake window and cost the server 813 MB of
  allocations. New limits: `WebSocketOptions.MaxRequestHeaderBytes` (16 KB) and
  `MaxRequestHeaderCount` (100), answered with `431`.
- **Header injection through echoed values is rejected.** A bare LF inside a header value survived
  parsing, so `Sec-WebSocket-Protocol: chat<LF>X-Injected: yes` could add an attacker-controlled
  header to the 101 response. Header names must now be RFC 7230 tokens and values may not contain
  CR/LF or other control characters.
- **Connection limits count connections that are still handshaking.** `MaxConnections` compared
  against established sessions only, so sockets parked in TLS or the upgrade were never counted and
  the limit could be walked straight past.
- **New `ServerOptions.MaxConnectionsPerIp`** bounds concurrent connections from a single address.
- **New `ServerOptions.TlsHandshakeTimeout`** (10 s). The TLS handshake previously had no timeout at
  all, so a peer that stalled mid-handshake held a socket, two pipes and a task indefinitely.
- **Rate limiting no longer resets the budget it is supposed to enforce.** Tripping the limit with
  `Scope.IpAddress` removed the whole IP entry, handing every other connection from that address a
  fresh counter. The window is now sliding by default (`RateLimitOptions.SlidingWindow`), and
  control frames and fragments are metered too (`RateLimitOptions.CountControlFrames`), closing a
  ping-flood amplification where each ping was auto-ponged for free.

### RFC 6455 / RFC 7692 compliance

The README previously claimed full RFC 6455 compliance. These were the gaps:

- **Masking is enforced in both directions.** The MASK bit was decoded and then never read by
  anything: servers accepted unmasked client frames and clients accepted masked server frames.
- **Text payloads are validated as UTF-8** by an incremental validator that carries state across
  fragments, failing with 1007. Invalid sequences were previously replaced with U+FFFD and handed to
  the application as if they were valid — the corruption was silent.
- **Close frames are validated**: reserved and unassigned codes (1004, 1005, 1006, 1012-2999, 5000+)
  fail the connection with 1002 instead of being echoed back onto the wire, a one-byte body is a
  protocol error, and the reason must be valid UTF-8.
- **Exactly one Close frame per connection.** Every close path previously sent a second one, so
  peers saw the diagnosed status followed by a plain 1000.
- **The closing handshake waits for the peer** (`WebSocketOptions.CloseTimeout`, 5 s) before dropping
  TCP, so a peer that is closed by the server reports the real status instead of 1006.
- **Fragmented control frames fail the connection.** The check existed but was unreachable: both read
  loops routed control frames around the layer that performed it, so only the unit tests exercised it.
- **Frames are no longer processed after a Close** has been received.
- **A 64-bit payload length with the most significant bit set** is a protocol error. It previously
  became a negative length that slipped past every size guard and surfaced as an unhandled
  `ArgumentOutOfRangeException`, tearing the connection down with no Close frame.
- **Non-minimal length encodings** and **RSV1 on control or continuation frames** are rejected.
- **The handshake is validated**: `GET` with HTTP/1.1 or later, a `Host` header, and a
  `Sec-WebSocket-Key` that base64-decodes to exactly 16 bytes. `POST / HTTP/1.0` with a garbage key
  previously returned `101 Switching Protocols`.
- `Upgrade` and `Connection` are matched as comma-separated token lists, so `Upgrade: websocket, h2c`
  is accepted and substring matches no longer pass.
- Duplicate `Host`, `Sec-WebSocket-Key` and `Sec-WebSocket-Version` headers are rejected; other
  repeated headers are combined per RFC 7230 instead of last-one-wins.
- A version mismatch answers `426 Upgrade Required` (was `400`), keeping `Sec-WebSocket-Version: 13`.
- **permessage-deflate is negotiated by parsing, not substring matching.** `server_max_window_bits`
  was silently ignored, producing a stream the peer could not inflate; offers that require a window
  this library cannot honor are now declined, and `client_max_window_bits` is never sent unsolicited.

### Fixed

- **Concurrent sends on a TCP session corrupted the wire.** `PipeConnection.SendAsync` wrote to the
  `PipeWriter` with no synchronization while `WebSocketSession` had a write lock. Two threads sending
  on one session interleaved `GetSpan`/`Advance`: a repro produced a garbage length prefix and lost
  half the bytes. The TCP path now uses the same fast-path write lock.
- **permessage-deflate compression ran outside the write lock**, so concurrent sends mutated shared
  deflate state (`ObjectDisposedException` in practice) and could emit frames whose deflate order did
  not match wire order.
- **`CloseAsync` dropped queued data.** Both transports completed the send-pipe writer and cancelled
  the token immediately; 4 MB queued before a close delivered 621 KB. The send loop now drains,
  bounded by a timeout.
- **WebSocket client heartbeat timeout deadlocked** the frame loop through a cycle back into the
  heartbeat task, leaking the transport — and `DisposeAsync` still returned successfully, so the leak
  was silent.
- **`ConnectAsync` could hang forever** with reconnect enabled when the token was cancelled or the
  first attempt threw: the promise was never completed and the exception was never observed.
- **The WebSocket client never sent a Close frame** on `DisconnectAsync`: the state was set to
  `Closing` before the write, and the write path skips anything that is not `Connected`.
- **Client transports leaked on every post-handshake connect failure**, one socket and two loop tasks
  per attempt, forever, when reconnect was enabled.
- **`ConnectTimeout` now covers the whole connect sequence** (DNS, TCP, TLS, upgrade), not just the
  TCP connect; the buffered 101 response is capped.
- **Multicast async events dropped every subscriber but the last.** With two handlers attached, the
  first one's `ValueTask` was never awaited: ordering was lost and its exceptions surfaced as
  `TaskScheduler.UnobservedTaskException` instead of reaching `OnError`. All events now await every
  subscriber in registration order, each isolated by its own try/catch.
- **Sessions could stay in a group forever.** `RemoveFromAll` ran before the disconnect handlers, so a
  `JoinGroup` from `OnDisconnected` re-inserted a dead session with nothing left to remove it; 60
  connect/disconnect cycles left 60 phantom members broadcasting into disposed transports.
- **A concurrent group add/remove could detach a member silently** — it believed it was in the group
  while the group no longer contained it.
- **`SlowConsumerPolicy` and the pipe limits were ignored on TLS connections.** `SslTransport` fell
  back to the 64 KB `PipeOptions.Default`, so every configured backpressure limit was a no-op on
  `wss://`.
- **The receive pipe was never completed**, so its pooled segments were never returned.
- `Session.Items` is now a concurrent dictionary; it is reachable from the read loop, the timers and
  application threads at the same time.
- A throwing `OnConnecting` handler now rejects the connection with 500 instead of being reported as
  a transport error; a throwing handler in the TCP client no longer skips transport disposal or kills
  the read loop.
- Client TLS: an empty `TargetHost` falls back to the URI/endpoint host instead of producing an empty
  SNI name; new `ClientSslOptions.CheckCertificateRevocation`.

### Performance

- **Payload unmasking is vectorized.** The 4-byte key is widened to a vector (or machine word) rather
  than XORed byte at a time. Decoding a 1 KB frame went from 586 ns to 109 ns, and an 8 KB frame from
  4.42 us to 486 ns. A 32-byte frame costs about 7 ns more than before, which buys the protocol
  validation above.
- **The per-frame payload allocation is gone.** Masked payloads are unmasked into a buffer the
  connection reuses instead of a fresh array per frame: server-side allocation over a 25M-message run
  dropped from 156 to 109 bytes per message and gen0 collections from 46 to 28.
- The frame header is read in place when the read buffer is a single segment, instead of being copied
  into scratch space for every frame.

### Added

- `StormTcpServer.LocalEndPoint` / `StormWebSocketServer.LocalEndPoint`, so binding to port 0 and
  discovering the assigned port is possible (useful in tests).
- `WebSocketSession.CloseAsync(WsCloseStatus, CancellationToken)` for closing with an explicit status.
- `IConnectionMiddleware.OnFrameReceivedAsync`, called for every decoded frame including control
  frames and fragments, so middleware can meter traffic that never becomes a message.
- Benchmarks gained `--mode latency`, which measures real round-trip times at pipeline depth 1 and
  reports p50/p90/p99/p99.9.

### Breaking changes

1. **`WsMessage.Data` is only valid for the duration of the handler.** It points into a buffer the
   connection reuses for the next frame. Anything that outlives the handler must copy it
   (`msg.Data.ToArray()`). `msg.Text` is unaffected.
2. **Handshakes that used to be accepted are now rejected**: non-GET methods, HTTP/1.0, a missing
   `Host`, a `Sec-WebSocket-Key` that is not 16 base64-decoded bytes, duplicate singleton headers,
   and header lines containing bare CR/LF.
3. **`NetworkSessionGroup.RemoveFromAll` is terminal for that session.** Rejoining afterwards is
   ignored, which is what stops disconnect handlers from resurrecting dead sessions. Use `Remove`
   per group to take a live session out of its rooms.
4. **`WsUpgradeContext.AcceptSubprotocol` throws `ArgumentException`** for a value the client did not
   offer or one that is not a valid token.
5. **Rate limiting is stricter**: a sliding window by default, and control frames and fragments now
   consume budget. Set `SlidingWindow = false` and `CountControlFrames = false` for the old accounting.
6. **`CloseAsync` can take longer.** It drains queued data (up to 5 s) and, when this endpoint starts
   the closing handshake, waits for the peer's Close frame (`CloseTimeout`, 5 s). Set `CloseTimeout`
   to `TimeSpan.Zero` to drop TCP immediately.
7. **`DisconnectAsync` / `DisposeAsync` on the clients block until the receive loop has finished**, so
   a successful return now means the connection really is gone.
8. **`WsPerMessageDeflate.Decompress` requires a maximum output size**, and `ParseServerResponse`
   throws `WsProtocolException` for a server response the client cannot honor.
9. **Compression window-bits options are advisory only.** `DeflateStream` cannot honor them, so
   rather than advertising a value it would ignore, the library declines offers that require a
   smaller server window.
10. **`wss://` connections now honor `MaxPendingSendBytes` / `MaxPendingReceiveBytes`.** Applications
    that unknowingly relied on the 64 KB default will see different backpressure behavior.

[5.0.0]: https://github.com/suleymanbyzt/StormSocket/releases/tag/v5.0.0
