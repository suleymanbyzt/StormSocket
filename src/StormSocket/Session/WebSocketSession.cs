using System.IO.Pipelines;
using System.Net;
using System.Text;
using StormSocket.Core;
using StormSocket.Transport;
using StormSocket.WebSocket;

namespace StormSocket.Session;

/// <summary>
/// A WebSocket client session. Supports sending text/binary frames,
/// automatic ping/pong heartbeat, and group membership.
/// All write operations are serialized via an internal lock to prevent
/// concurrent PipeWriter access (heartbeat pings, auto-pong, user sends).
/// </summary>
public sealed class WebSocketSession : ISession
{
    private readonly ITransport _transport;
    private readonly SlowConsumerPolicy _policy;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private volatile ConnectionState _state;
    private volatile bool _isBackpressured;
    private int _closeGuard;
    private WsHeartbeat? _heartbeat;

    public long Id { get; }
    public ConnectionState State => _state;
    public ConnectionMetrics Metrics { get; } = new();
    public EndPoint? RemoteEndPoint { get; }
    public bool IsBackpressured => _isBackpressured;

    public IReadOnlySet<string> Groups
    {
        get
        {
            lock (_groupLock)
            {
                return new HashSet<string>(_groups);
            }
        }
    }

    internal WebSocketSession(long id, ITransport transport, EndPoint? remoteEndPoint, SlowConsumerPolicy policy = SlowConsumerPolicy.Wait)
    {
        Id = id;
        _transport = transport;
        RemoteEndPoint = remoteEndPoint;
        _policy = policy;
        _state = ConnectionState.Connected;
    }

    internal void SetHeartbeat(WsHeartbeat heartbeat)
    {
        _heartbeat = heartbeat;
    }

    internal void SetState(ConnectionState state) => _state = state;

    internal void NotifyPongReceived()
    {
        _heartbeat?.OnPongReceived();
    }

    /// <summary>
    /// Acquires the write lock, writes a frame, and flushes.
    /// All PipeWriter access MUST go through this method.
    /// </summary>
    internal ValueTask WriteFrameAsync(Action<PipeWriter> writeAction, int byteCount = 0, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            return ValueTask.CompletedTask;
        }

        // Fast path: try to acquire lock synchronously (no contention)
        if (_writeLock.Wait(0))
        {
            writeAction(_transport.Output);
            ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
            if (flushTask.IsCompletedSuccessfully)
            {
                _writeLock.Release();
                if (byteCount > 0)
                {
                    Metrics.AddBytesSent(byteCount);
                }

                return ValueTask.CompletedTask;
            }

            // Lock acquired but flush is slow — await flush in slow path
            return WriteFrameSlowFlushAsync(flushTask, byteCount);
        }

        // Slow path: lock contention — await lock
        return WriteFrameSlowLockAsync(writeAction, byteCount, cancellationToken);
    }

    private async ValueTask WriteFrameSlowFlushAsync(ValueTask<FlushResult> flushTask, int byteCount)
    {
        _isBackpressured = true;
        if (_policy == SlowConsumerPolicy.Disconnect)
        {
            Abort();
        }

        try
        {
            await flushTask.ConfigureAwait(false);
        }
        finally
        {
            _isBackpressured = false;
            _writeLock.Release();
        }

        if (byteCount > 0)
        {
            Metrics.AddBytesSent(byteCount);
        }
    }

    private async ValueTask WriteFrameSlowLockAsync(Action<PipeWriter> writeAction, int byteCount, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            writeAction(_transport.Output);

            ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
            if (!flushTask.IsCompletedSuccessfully)
            {
                _isBackpressured = true;
                if (_policy == SlowConsumerPolicy.Disconnect)
                {
                    Abort();
                }

                try
                {
                    await flushTask.ConfigureAwait(false);
                }
                finally
                {
                    _isBackpressured = false;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }

        if (byteCount > 0)
        {
            Metrics.AddBytesSent(byteCount);
        }
    }

    /// <summary>Sends a Binary WebSocket frame to the client.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_policy != SlowConsumerPolicy.Wait && _isBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        return WriteFrameAsync(
            writer => WsFrameEncoder.WriteBinary(writer, data.Span),
            data.Length,
            cancellationToken);
    }

    /// <summary>Sends a Text WebSocket frame (UTF-8 encoded) to the client.</summary>
    public ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_policy != SlowConsumerPolicy.Wait && _isBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return WriteFrameAsync(
            writer => WsFrameEncoder.WriteText(writer, bytes),
            bytes.Length,
            cancellationToken);
    }

    /// <summary>Sends a Text WebSocket frame from pre-encoded UTF-8 bytes (zero-copy).</summary>
    public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Data, CancellationToken cancellationToken = default)
    {
        if (_policy != SlowConsumerPolicy.Wait && _isBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        return WriteFrameAsync(
            writer => WsFrameEncoder.WriteText(writer, utf8Data.Span),
            utf8Data.Length,
            cancellationToken);
    }

    /// <summary>Sends a Close frame and shuts down the connection.</summary>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _closeGuard, 1, 0) != 0)
        {
            return;
        }

        _state = ConnectionState.Closing;

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WsFrameEncoder.WriteClose(_transport.Output);
                await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // ignored
        }

        await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        _state = ConnectionState.Closed;
    }

    public void Abort()
    {
        if (Interlocked.CompareExchange(ref _closeGuard, 1, 0) != 0)
        {
            return;
        }

        _state = ConnectionState.Closing;
        _ = _transport.CloseAsync();
    }

    public void JoinGroup(string group)
    {
        lock (_groupLock)
        {
            _groups.Add(group);
        }
    }

    public void LeaveGroup(string group)
    {
        lock (_groupLock)
        {
            _groups.Remove(group);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);

        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
        }

        _writeLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}