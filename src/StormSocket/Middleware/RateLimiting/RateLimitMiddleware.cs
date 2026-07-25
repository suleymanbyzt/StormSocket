using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Session;

namespace StormSocket.Middleware.RateLimiting;

/// <summary>
/// Opt-in middleware that limits the number of incoming messages per client within a time window.
/// Protects the server from misbehaving or malicious clients.
/// <example>
/// <code>
/// var rateLimiter = new RateLimitMiddleware(new RateLimitOptions
/// {
///     Window = TimeSpan.FromSeconds(10),
///     MaxMessages = 500,
///     ExceededAction = RateLimitAction.Disconnect,
/// });
/// rateLimiter.OnExceeded += async (session) =>
/// {
///     Console.WriteLine($"Rate limited: {session.RemoteEndPoint}");
/// };
/// server.UseMiddleware(rateLimiter);
/// </code>
/// </example>
/// </summary>
public sealed class RateLimitMiddleware : IConnectionMiddleware
{
    private readonly RateLimitOptions _options;
    private readonly ILogger? _logger;
    private readonly long _windowMs;
    private readonly ConcurrentDictionary<long, RateLimitEntry> _sessionEntries = new();
    private readonly ConcurrentDictionary<IPAddress, RateLimitEntry> _ipEntries = new();

    /// <summary>
    /// Fired when a session exceeds the rate limit, before the configured action is taken.
    /// Use this for logging, monitoring, or sending a warning to the client.
    /// </summary>
    public event SessionConnectedHandler? OnExceeded;

    public RateLimitMiddleware(RateLimitOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (options.Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Window must be positive.");
        }

        if (options.MaxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessages must be positive.");
        }

        _windowMs = (long)options.Window.TotalMilliseconds;
    }

    public ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        RateLimitEntry entry = GetEntry(session);

        if (entry.TryAcquire(_windowMs, _options.MaxMessages, _options.SlidingWindow))
        {
            return ValueTask.FromResult(data);
        }

        return HandleExceededAsync(session);
    }

    /// <summary>
    /// Charges a single received protocol frame — a ping/pong/close control frame or one fragment
    /// of a fragmented message — to the same window as assembled application messages.
    /// Servers call this from the read loop once per frame, before answering it, so that traffic
    /// which never surfaces as an application message cannot bypass the limiter.
    /// No-op returning <see langword="true"/> when <see cref="RateLimitOptions.CountControlFrames"/>
    /// is disabled.
    /// </summary>
    /// <param name="session">The session the frame arrived on.</param>
    /// <returns>
    /// <see langword="true"/> when the frame fits in the remaining budget;
    /// <see langword="false"/> when the limit was exceeded, in which case <see cref="OnExceeded"/>
    /// and the configured <see cref="RateLimitOptions.ExceededAction"/> have already been applied
    /// and the caller must not process the frame.
    /// </returns>
    public ValueTask<bool> OnFrameReceivedAsync(ISession session) => TryAcceptFrameAsync(session);

    /// <inheritdoc cref="OnFrameReceivedAsync"/>
    public ValueTask<bool> TryAcceptFrameAsync(ISession session)
    {
        if (!_options.CountControlFrames)
        {
            return ValueTask.FromResult(true);
        }

        RateLimitEntry entry = GetEntry(session);

        if (entry.TryAcquire(_windowMs, _options.MaxMessages, _options.SlidingWindow))
        {
            return ValueTask.FromResult(true);
        }

        return RejectFrameAsync(session);
    }

    public ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason)
    {
        if (_options.Scope == RateLimitScope.IpAddress)
        {
            IPAddress? ip = GetIpAddress(session);
            if (ip is not null && _ipEntries.TryGetValue(ip, out RateLimitEntry? entry) && entry.ReleaseSession())
            {
                // Remove the exact instance that was drained: a session connecting concurrently
                // may already have published a replacement whose budget must survive.
                RemoveIpEntry(ip, entry);
            }
        }
        else
        {
            _sessionEntries.TryRemove(session.Id, out _);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnConnectedAsync(ISession session)
    {
        if (_options.Scope == RateLimitScope.IpAddress)
        {
            IPAddress ip = GetIpAddress(session) ?? IPAddress.None;

            while (true)
            {
                RateLimitEntry entry = _ipEntries.GetOrAdd(ip, static _ => new RateLimitEntry());
                if (entry.TryAddSession())
                {
                    break;
                }

                // The last session on this entry disconnected between the lookup and the increment,
                // so it is retired; evict it and retry rather than counting into a detached bucket.
                RemoveIpEntry(ip, entry);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> RejectFrameAsync(ISession session)
    {
        await HandleExceededAsync(session).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<ReadOnlyMemory<byte>> HandleExceededAsync(ISession session)
    {
        _logger?.LogWarning("Rate limit exceeded for session {SessionId}, action: {Action}", session.Id, _options.ExceededAction);

        if (OnExceeded is not null)
        {
            await OnExceeded.Invoke(session).ConfigureAwait(false);
        }

        if (_options.ExceededAction == RateLimitAction.Disconnect)
        {
            if (session is TcpSession tcp) tcp.SetDisconnectReason(DisconnectReason.RateLimited);
            else if (session is WebSocketSession ws) ws.SetDisconnectReason(DisconnectReason.RateLimited);
            session.Abort();
        }

        // The counter deliberately survives the action. Dropping it here would hand the offender a
        // fresh budget on its next message, and under IpAddress scope every other connection from
        // that IP would get a zeroed bucket too — tripping the limit would raise it, not enforce it.
        return ReadOnlyMemory<byte>.Empty;
    }

    private RateLimitEntry GetEntry(ISession session)
    {
        if (_options.Scope == RateLimitScope.IpAddress)
        {
            IPAddress ip = GetIpAddress(session) ?? IPAddress.None;
            return _ipEntries.GetOrAdd(ip, static _ => new RateLimitEntry());
        }

        return _sessionEntries.GetOrAdd(session.Id, static _ => new RateLimitEntry());
    }

    private void RemoveIpEntry(IPAddress ip, RateLimitEntry entry)
    {
        ICollection<KeyValuePair<IPAddress, RateLimitEntry>> entries = _ipEntries;
        entries.Remove(new KeyValuePair<IPAddress, RateLimitEntry>(ip, entry));
    }

    private static IPAddress? GetIpAddress(ISession session)
    {
        return session.RemoteEndPoint is IPEndPoint ep ? ep.Address : null;
    }

    private sealed class RateLimitEntry
    {
        private readonly object _lock = new();
        private int _count;
        private int _previousCount;
        private long _windowStartMs;
        private int _sessionCount;
        private bool _retired;

        public bool TryAcquire(long windowMs, int maxMessages, bool sliding)
        {
            long now = Environment.TickCount64;

            lock (_lock)
            {
                long elapsed = now - _windowStartMs;

                if (!sliding)
                {
                    if (elapsed >= windowMs)
                    {
                        _windowStartMs = now;
                        _count = 1;
                        return true;
                    }

                    if (_count >= maxMessages)
                    {
                        return false;
                    }

                    _count++;
                    return true;
                }

                if (elapsed >= windowMs * 2)
                {
                    _previousCount = 0;
                    _count = 0;
                    _windowStartMs = now;
                    elapsed = 0;
                }
                else if (elapsed >= windowMs)
                {
                    _previousCount = _count;
                    _count = 0;
                    _windowStartMs += windowMs;
                    elapsed -= windowMs;
                }

                // Weighted estimate over the trailing window, scaled by windowMs to stay in integer
                // arithmetic: the previous bucket contributes the fraction of it that still overlaps.
                long weighted = ((long)_previousCount * (windowMs - elapsed)) + ((long)_count * windowMs);
                if (weighted >= (long)maxMessages * windowMs)
                {
                    return false;
                }

                _count++;
                return true;
            }
        }

        /// <summary>
        /// Registers one more live session on this entry. Returns false when the entry has already
        /// been retired by its last session leaving, in which case the caller must fetch a new one.
        /// </summary>
        public bool TryAddSession()
        {
            lock (_lock)
            {
                if (_retired)
                {
                    return false;
                }

                _sessionCount++;
                return true;
            }
        }

        /// <summary>
        /// Releases one session. Returns true only when this was the last one, retiring the entry so
        /// it can be evicted without stranding a counter that other live sessions still share.
        /// </summary>
        public bool ReleaseSession()
        {
            lock (_lock)
            {
                if (_sessionCount > 0)
                {
                    _sessionCount--;
                }

                if (_sessionCount > 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }
}