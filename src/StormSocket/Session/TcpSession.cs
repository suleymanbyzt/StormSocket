using System.Net;
using StormSocket.Core;
using StormSocket.Transport;

namespace StormSocket.Session;

public sealed class TcpSession : ISession
{
    private readonly PipeConnection _connection;
    private readonly ITransport _transport;
    private readonly SlowConsumerPolicy _policy;
    private readonly ServerMetrics? _serverMetrics;
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private volatile ConnectionState _state;
    private int _disconnectReason;
    private int _closeGuard;
    private IdleTimer? _idleTimer;

    public long Id { get; }
    public ConnectionState State => _state;
    public DisconnectReason DisconnectReason => (DisconnectReason)_disconnectReason;
    public ConnectionMetrics Metrics { get; } = new();
    public EndPoint? RemoteEndPoint { get; }
    public bool IsBackpressured => _connection.IsBackpressured;
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

    internal TcpSession(long id, ITransport transport, PipeConnection connection, EndPoint? remoteEndPoint,
        SlowConsumerPolicy policy = SlowConsumerPolicy.Wait, ServerMetrics? serverMetrics = null)
    {
        Id = id;
        _transport = transport;
        _connection = connection;
        RemoteEndPoint = remoteEndPoint;
        _policy = policy;
        _serverMetrics = serverMetrics;
        _state = ConnectionState.Connected;

        if (policy == SlowConsumerPolicy.Disconnect)
        {
            _connection.OnBackpressureDetected = () =>
            {
                SetDisconnectReason(DisconnectReason.SlowConsumer);
                _ = CloseAsync();
            };
        }
    }

    internal void SetIdleTimer(IdleTimer idleTimer)
    {
        _idleTimer = idleTimer;
    }

    internal void NotifyDataReceived()
    {
        _idleTimer?.OnDataReceived();
    }

    internal void SetState(ConnectionState state) => _state = state;

    /// <summary>Sets the disconnect reason. Only the first call wins (no overwrite).</summary>
    internal void SetDisconnectReason(DisconnectReason reason)
    {
        Interlocked.CompareExchange(ref _disconnectReason, (int)reason, (int)DisconnectReason.None);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            return ValueTask.CompletedTask;
        }

        if (_policy != SlowConsumerPolicy.Wait && _connection.IsBackpressured)
        {
            if (_policy == SlowConsumerPolicy.Disconnect)
            {
                SetDisconnectReason(DisconnectReason.SlowConsumer);
                _ = CloseAsync(cancellationToken);
            }

            return ValueTask.CompletedTask;
        }

        ValueTask sendTask = _connection.SendAsync(data, cancellationToken);
        if (sendTask.IsCompletedSuccessfully)
        {
            Metrics.AddBytesSent(data.Length);
            _serverMetrics?.RecordMessageSent(data.Length);
            return ValueTask.CompletedTask;
        }

        return SendAsyncSlow(sendTask, data.Length);
    }

    private async ValueTask SendAsyncSlow(ValueTask sendTask, int byteCount)
    {
        await sendTask.ConfigureAwait(false);
        Metrics.AddBytesSent(byteCount);
        _serverMetrics?.RecordMessageSent(byteCount);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _closeGuard, 1, 0) != 0)
        {
            return;
        }

        SetDisconnectReason(DisconnectReason.ClosedByServer);
        _state = ConnectionState.Closing;
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

        if (_idleTimer is not null)
        {
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}