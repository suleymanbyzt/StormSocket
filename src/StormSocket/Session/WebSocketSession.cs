using System.Buffers;
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
    private readonly ServerMetrics? _serverMetrics;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private volatile ConnectionState _state;
    private volatile bool _isBackpressured;
    private int _disconnectReason;
    private int _closeGuard;
    private WsHeartbeat? _heartbeat;
    private WsPerMessageDeflate? _deflate;
    private IdleTimer? _idleTimer;

    public long Id { get; }
    public ConnectionState State => _state;
    public DisconnectReason DisconnectReason => (DisconnectReason)_disconnectReason;
    public ConnectionMetrics Metrics { get; } = new();
    public EndPoint? RemoteEndPoint { get; }
    public bool IsBackpressured => _isBackpressured;
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public T? Get<T>(SessionKey<T> key)
    {
        return Items.TryGetValue(key.Name, out object? value) ? (T?)value : default;
    }

    public void Set<T>(SessionKey<T> key, T value)
    {
        Items[key.Name] = value;
    }

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

    internal WebSocketSession(long id, ITransport transport, EndPoint? remoteEndPoint, SlowConsumerPolicy policy = SlowConsumerPolicy.Wait, ServerMetrics? serverMetrics = null)
    {
        Id = id;
        _transport = transport;
        RemoteEndPoint = remoteEndPoint;
        _policy = policy;
        _serverMetrics = serverMetrics;
        _state = ConnectionState.Connected;
    }

    internal void SetHeartbeat(WsHeartbeat heartbeat)
    {
        _heartbeat = heartbeat;
    }

    internal void SetCompression(WsPerMessageDeflate deflate)
    {
        _deflate = deflate;
    }

    internal void SetIdleTimer(IdleTimer idleTimer)
    {
        _idleTimer = idleTimer;
    }

    internal void NotifyDataReceived()
    {
        _idleTimer?.OnDataReceived();
    }

    internal WsPerMessageDeflate? Compression => _deflate;

    internal void SetState(ConnectionState state) => _state = state;

    /// <summary>Sets the disconnect reason. Only the first call wins (no overwrite).</summary>
    internal void SetDisconnectReason(DisconnectReason reason)
    {
        Interlocked.CompareExchange(ref _disconnectReason, (int)reason, (int)DisconnectReason.None);
    }

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
            try
            {
                writeAction(_transport.Output);
            }
            catch
            {
                _writeLock.Release();
                throw;
            }

            ValueTask<FlushResult> flushTask = _transport.Output.FlushAsync(cancellationToken);
            if (flushTask.IsCompletedSuccessfully)
            {
                _writeLock.Release();
                if (byteCount > 0)
                {
                    Metrics.AddBytesSent(byteCount);
                    _serverMetrics?.RecordMessageSent(byteCount);
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
            SetDisconnectReason(DisconnectReason.SlowConsumer);
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
            _serverMetrics?.RecordMessageSent(byteCount);
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
                    SetDisconnectReason(DisconnectReason.SlowConsumer);
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
            _serverMetrics?.RecordMessageSent(byteCount);
        }
    }

    /// <summary>Sends a Binary WebSocket frame to the client.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_policy != SlowConsumerPolicy.Wait && _isBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                SetDisconnectReason(DisconnectReason.SlowConsumer);
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        if (_deflate is not null && _deflate.ShouldCompress(data.Length))
        {
            byte[] compressed = _deflate.Compress(data.Span);
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteFrame(writer, WsOpCode.Binary, compressed, rsv1: true),
                compressed.Length,
                cancellationToken);
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
                SetDisconnectReason(DisconnectReason.SlowConsumer);
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        int byteCount = Encoding.UTF8.GetByteCount(text);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        int written = Encoding.UTF8.GetBytes(text, rented);

        if (_deflate is not null && _deflate.ShouldCompress(written))
        {
            byte[] compressed = _deflate.Compress(rented.AsSpan(0, written));
            ArrayPool<byte>.Shared.Return(rented);
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteFrame(writer, WsOpCode.Text, compressed, rsv1: true),
                compressed.Length,
                cancellationToken);
        }

        ValueTask task = WriteFrameAsync(
            writer => WsFrameEncoder.WriteText(writer, rented.AsSpan(0, written)),
            written, cancellationToken);

        if (task.IsCompletedSuccessfully)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return ValueTask.CompletedTask;
        }

        return ReturnBufferAfterWriteAsync(task, rented);
    }

    private static async ValueTask ReturnBufferAfterWriteAsync(ValueTask writeTask, byte[] rented)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Sends a Text WebSocket frame from pre-encoded UTF-8 bytes (zero-copy).</summary>
    public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Data, CancellationToken cancellationToken = default)
    {
        if (_policy != SlowConsumerPolicy.Wait && _isBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                SetDisconnectReason(DisconnectReason.SlowConsumer);
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        if (_deflate is not null && _deflate.ShouldCompress(utf8Data.Length))
        {
            byte[] compressed = _deflate.Compress(utf8Data.Span);
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteFrame(writer, WsOpCode.Text, compressed, rsv1: true),
                compressed.Length,
                cancellationToken);
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

        SetDisconnectReason(DisconnectReason.ClosedByServer);
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

        SetDisconnectReason(DisconnectReason.Aborted);
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

        if (_idleTimer is not null)
        {
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
        }

        _deflate?.Dispose();
        _writeLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}