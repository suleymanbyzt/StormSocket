using System.Buffers;
using System.Collections.Concurrent;
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
public sealed class WebSocketSession : IWebSocketSession
{
    private readonly ITransport _transport;
    private readonly SlowConsumerPolicy _policy;
    private readonly ServerMetrics? _serverMetrics;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private NetworkSessionGroup? _groupManager;
    private volatile ConnectionState _state;
    private volatile bool _isBackpressured;
    private int _disconnectReason;
    private int _closeGuard;
    private int _closeFrameSent;
    private volatile bool _closeReceived;
    private readonly TaskCompletionSource _closeHandshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Upper bound on how long a second caller waits for an in-flight close to finish.</summary>
    private static readonly TimeSpan CloseObservationTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _closeTimeout = TimeSpan.FromSeconds(5);
    private Task? _abortTask;
    private WsHeartbeat? _heartbeat;
    private WsPerMessageDeflate? _deflate;
    private IdleTimer? _idleTimer;

    public long Id { get; }
    public ConnectionState State => _state;
    public DisconnectReason DisconnectReason => (DisconnectReason)_disconnectReason;
    public ConnectionMetrics Metrics { get; } = new();
    public EndPoint? RemoteEndPoint { get; }
    public bool IsBackpressured => _isBackpressured;

    /// <remarks>
    /// Concurrent by design: the read loop, heartbeat/idle timers and application threads all reach
    /// session state, and a plain Dictionary corrupts its buckets under concurrent writes.
    /// </remarks>
    public IDictionary<string, object?> Items { get; } = new ConcurrentDictionary<string, object?>();

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

    internal void ClearGroups()
    {
        lock (_groupLock)
        {
            _groups.Clear();
        }
    }

    internal void SetGroupManager(NetworkSessionGroup groupManager)
    {
        _groupManager = groupManager;
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

    internal void SetCloseTimeout(TimeSpan closeTimeout) => _closeTimeout = closeTimeout;

    /// <summary>True once the peer's Close frame has been received (RFC 6455 Section 5.5.1).</summary>
    internal bool CloseReceived => _closeReceived;

    /// <summary>Records the peer's Close frame and releases anyone waiting on the close handshake.</summary>
    internal void NotifyCloseReceived()
    {
        _closeReceived = true;
        _closeHandshake.TrySetResult();
    }

    /// <summary>
    /// Writes a Close frame unless one has already been sent on this connection. RFC 6455 Section 6.1:
    /// an endpoint must not send anything after its Close frame, so the first status wins.
    /// </summary>
    internal ValueTask SendCloseFrameAsync(WsCloseStatus status, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closeFrameSent, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return WriteFrameAsync(writer => WsFrameEncoder.WriteClose(writer, status), cancellationToken: cancellationToken);
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

        // Fast path: try to acquire lock synchronously (no contention).
        // A concurrent DisposeAsync can retire the lock between the state check above and here;
        // that is a closed connection, not a caller error, so it is reported as a no-op.
        bool acquired;
        try
        {
            acquired = _writeLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return ValueTask.CompletedTask;
        }

        if (acquired)
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
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

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
        if (_state is not ConnectionState.Connected)
        {
            return ValueTask.CompletedTask;
        }

        if (_policy != SlowConsumerPolicy.Wait && _isBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                SetDisconnectReason(DisconnectReason.SlowConsumer);
                Abort();
            }

            return ValueTask.CompletedTask;
        }

        // Compression runs inside the write action, i.e. under the write lock: the deflate context is
        // per-connection mutable state and its output order has to match the order frames hit the wire.
        if (_deflate is not null && _deflate.ShouldCompress(data.Length))
        {
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteFrame(writer, WsOpCode.Binary, _deflate.Compress(data.Span), rsv1: true),
                data.Length,
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
        if (_state is not ConnectionState.Connected)
        {
            return ValueTask.CompletedTask;
        }

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
            ValueTask compressedTask = WriteFrameAsync(
                writer => WsFrameEncoder.WriteFrame(writer, WsOpCode.Text, _deflate.Compress(rented.AsSpan(0, written)), rsv1: true),
                written,
                cancellationToken);

            if (compressedTask.IsCompletedSuccessfully)
            {
                ArrayPool<byte>.Shared.Return(rented);
                return ValueTask.CompletedTask;
            }

            return ReturnBufferAfterWriteAsync(compressedTask, rented);
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
        if (_state is not ConnectionState.Connected)
        {
            return ValueTask.CompletedTask;
        }

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
            return WriteFrameAsync(
                writer => WsFrameEncoder.WriteFrame(writer, WsOpCode.Text, _deflate.Compress(utf8Data.Span), rsv1: true),
                utf8Data.Length,
                cancellationToken);
        }

        return WriteFrameAsync(
            writer => WsFrameEncoder.WriteText(writer, utf8Data.Span),
            utf8Data.Length,
            cancellationToken);
    }

    /// <summary>Sends a Close frame and shuts down the connection.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        => CloseAsync(WsCloseStatus.NormalClosure, cancellationToken);

    /// <summary>Sends a Close frame with an explicit status code and shuts down the connection.</summary>
    /// <remarks>
    /// When this endpoint starts the closing handshake it waits for the peer's Close frame before
    /// dropping TCP (RFC 6455 Section 7.1.4), so the peer reports the status sent here instead of an
    /// abnormal 1006. The wait is bounded by the configured close timeout.
    /// </remarks>
    public ValueTask CloseAsync(WsCloseStatus status, CancellationToken cancellationToken = default)
        => CloseAsync(status, waitForPeer: true, cancellationToken);

    /// <param name="waitForPeer">
    /// False when the peer is already known to be gone (heartbeat timeout, idle timeout): waiting for
    /// a Close frame that will never arrive would just stall teardown for the full close timeout.
    /// </param>
    internal async ValueTask CloseAsync(WsCloseStatus status, bool waitForPeer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _closeGuard, 1, 0) != 0)
        {
            // Another close is already running. Waiting for it rather than returning immediately is
            // what keeps DisposeAsync from retiring the transport underneath an in-flight close —
            // that close is parked in the closing handshake and will come back to use it.
            try
            {
                await _closeCompleted.Task.WaitAsync(CloseObservationTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The owner is stuck or the caller gave up; teardown proceeds either way.
            }

            return;
        }

        SetDisconnectReason(DisconnectReason.ClosedByServer);
        _state = ConnectionState.Closing;

        try
        {
            // A Close frame may already have gone out (protocol error, echo of the peer's close).
            // RFC 6455 Section 6.1 forbids sending a second one.
            if (Interlocked.Exchange(ref _closeFrameSent, 1) == 0)
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    WsFrameEncoder.WriteClose(_transport.Output, status);
                    await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
        }
        catch
        {
            // ignored
        }

        // RFC 6455 Section 7.1.4: the endpoint that starts the handshake waits for the peer's Close
        // before tearing down TCP, otherwise the peer sees an abnormal closure.
        if (waitForPeer && !_closeReceived && _closeTimeout > TimeSpan.Zero)
        {
            try
            {
                await _closeHandshake.Task.WaitAsync(_closeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Peer never answered — fall through and close anyway.
            }
        }

        try
        {
            await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state = ConnectionState.Closed;
            _closeCompleted.TrySetResult();
        }
    }

    public void Abort()
    {
        if (Interlocked.CompareExchange(ref _closeGuard, 1, 0) != 0)
        {
            return;
        }

        SetDisconnectReason(DisconnectReason.Aborted);
        _state = ConnectionState.Closing;

        // Abort is synchronous by contract, so the close runs detached — but it is still published
        // so DisposeAsync can await it instead of tearing the transport down underneath it.
        _abortTask = AbortCoreAsync();
    }

    private async Task AbortCoreAsync()
    {
        try
        {
            await _transport.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _state = ConnectionState.Closed;
            _closeCompleted.TrySetResult();
        }
    }

    public void JoinGroup(string group)
    {
        bool added;
        lock (_groupLock)
        {
            added = _groups.Add(group);
        }

        if (added)
        {
            _groupManager?.RegisterSession(group, this);
        }
    }

    public void LeaveGroup(string group)
    {
        bool removed;
        lock (_groupLock)
        {
            removed = _groups.Remove(group);
        }

        if (removed)
        {
            _groupManager?.UnregisterSession(group, this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);

        if (_abortTask is not null)
        {
            try
            {
                await _abortTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _state = ConnectionState.Closed;

        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
        }

        if (_idleTimer is not null)
        {
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
        }

        // Take the write lock before retiring it and the deflate context, so an in-flight send
        // finishes first instead of faulting on disposed state.
        try
        {
            await _writeLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _deflate?.Dispose();
        _writeLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}