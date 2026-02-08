using System.Net;
using StormSocket.Core;
using StormSocket.Transport;

namespace StormSocket.Session;

public sealed class TcpSession : ISession
{
    private readonly PipeConnection _connection;
    private readonly ITransport _transport;
    private readonly SlowConsumerPolicy _policy;
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private volatile ConnectionState _state;
    private int _closeGuard;

    public long Id { get; }
    public ConnectionState State => _state;
    public ConnectionMetrics Metrics { get; } = new();
    public EndPoint? RemoteEndPoint { get; }
    public bool IsBackpressured => _connection.IsBackpressured;

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
        SlowConsumerPolicy policy = SlowConsumerPolicy.Wait)
    {
        Id = id;
        _transport = transport;
        _connection = connection;
        RemoteEndPoint = remoteEndPoint;
        _policy = policy;
        _state = ConnectionState.Connected;

        if (policy == SlowConsumerPolicy.Disconnect)
        {
            _connection.OnBackpressureDetected = () => _ = CloseAsync();
        }
    }

    internal void SetState(ConnectionState state) => _state = state;

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
                _ = CloseAsync(cancellationToken);
            }

            return ValueTask.CompletedTask;
        }

        ValueTask sendTask = _connection.SendAsync(data, cancellationToken);
        if (sendTask.IsCompletedSuccessfully)
        {
            Metrics.AddBytesSent(data.Length);
            return ValueTask.CompletedTask;
        }

        return SendAsyncSlow(sendTask, data.Length);
    }

    private async ValueTask SendAsyncSlow(ValueTask sendTask, int byteCount)
    {
        await sendTask.ConfigureAwait(false);
        Metrics.AddBytesSent(byteCount);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _closeGuard, 1, 0) != 0)
        {
            return;
        }

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
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}