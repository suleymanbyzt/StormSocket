# Security Policy

StormSocket terminates connections from untrusted peers, so an attacker controls every byte that
reaches the framing, handshake and decompression code. Reports about that surface are welcome.

## Supported versions

| Version | Supported |
|---|---|
| 5.0.x | Yes |
| < 5.0 | No — 5.0.0 fixed a remotely reachable memory-exhaustion issue in permessage-deflate and several protocol-validation gaps. Upgrade. |

## Reporting a vulnerability

Please report privately through
[GitHub Security Advisories](https://github.com/suleymanbyzt/StormSocket/security/advisories/new)
rather than opening a public issue.

Useful in a report: the affected version, whether the server or the client is the target, a minimal
sequence of bytes or frames that triggers it, and what the peer gains (memory, CPU, a protocol
violation, data crossing a session boundary). A failing test against `benchmark/autobahn` or
`tests/StormSocket.Tests` is ideal but not required.

Expect an acknowledgement within a few days. Fixes ship with a patch release and a note in the
[changelog](CHANGELOG.md).

## Deployment notes

These are configuration decisions the library cannot make for you:

- **`AllowedOrigins` is empty by default**, which allows any origin. Browser-facing deployments must
  set it, or check `context.Origin` in `OnConnecting`, otherwise any site can open an authenticated
  WebSocket in a visitor's browser (cross-site WebSocket hijacking).
- **`MaxConnectionsPerIp` is unlimited by default.** Set it on internet-facing servers, but leave it
  at 0 behind a reverse proxy or load balancer — every connection appears to come from the proxy
  there, and `RateLimitScope.IpAddress` has the same caveat. The library never trusts
  `X-Forwarded-For` for identity.
- **`MaxFrameSize` / `MaxMessageSize`** bound memory per connection; decompressed output is capped at
  `MaxMessageSize` as well. Lower them if your protocol has smaller messages.
- **TLS uses the OS default protocol set** (`SslProtocols.None`), which is the recommended setting.
  Revocation checking is off by default; enable `ClientSslOptions.CheckCertificateRevocation` if your
  threat model needs it.
