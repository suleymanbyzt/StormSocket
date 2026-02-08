using System.Collections.Concurrent;
using System.Net;
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
    private readonly long _windowMs;
    private readonly ConcurrentDictionary<long, RateLimitEntry> _sessionEntries = new();
    private readonly ConcurrentDictionary<IPAddress, RateLimitEntry> _ipEntries = new();

    /// <summary>
    /// Fired when a session exceeds the rate limit, before the configured action is taken.
    /// Use this for logging, monitoring, or sending a warning to the client.
    /// </summary>
    public event SessionConnectedHandler? OnExceeded;

    public RateLimitMiddleware(RateLimitOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

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

        if (entry.TryAcquire(_windowMs, _options.MaxMessages))
        {
            return ValueTask.FromResult(data);
        }

        return HandleExceededAsync(session);
    }

    public ValueTask OnDisconnectedAsync(ISession session)
    {
        if (_options.Scope == RateLimitScope.IpAddress)
        {
            IPAddress? ip = GetIpAddress(session);
            if (ip is not null && _ipEntries.TryGetValue(ip, out RateLimitEntry? entry) && entry.DecrementSessions() <= 0)
            {
                _ipEntries.TryRemove(ip, out _);
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
            RateLimitEntry entry = GetEntry(session);
            entry.IncrementSessions();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<ReadOnlyMemory<byte>> HandleExceededAsync(ISession session)
    {
        if (OnExceeded is not null)
        {
            await OnExceeded.Invoke(session).ConfigureAwait(false);
        }

        if (_options.ExceededAction == RateLimitAction.Disconnect)
        {
            session.Abort();

            if (_options.Scope == RateLimitScope.IpAddress)
            {
                IPAddress? ip = GetIpAddress(session);
                if (ip is not null)
                {
                    _ipEntries.TryRemove(ip, out _);
                }
            }
            else
            {
                _sessionEntries.TryRemove(session.Id, out _);
            }
        }

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

    private static IPAddress? GetIpAddress(ISession session)
    {
        return session.RemoteEndPoint is IPEndPoint ep ? ep.Address : null;
    }

    private sealed class RateLimitEntry
    {
        private readonly object _lock = new();
        private int _count;
        private long _windowStartMs;
        private int _sessionCount;

        public bool TryAcquire(long windowMs, int maxMessages)
        {
            long now = Environment.TickCount64;

            lock (_lock)
            {
                if (now - _windowStartMs >= windowMs)
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
        }

        public void IncrementSessions()
        {
            Interlocked.Increment(ref _sessionCount);
        }

        public int DecrementSessions()
        {
            return Interlocked.Decrement(ref _sessionCount);
        }
    }
}