using System.Net;
using System.Threading.Tasks;
using StormSocket.Core;
using StormSocket.Transport;

namespace StormSocket.Session;

public sealed class TcpSession : ISession
{
    private readonly PipeConnection _connection;
    private readonly ITransport _transport;
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private volatile ConnectionState _state;

    public long Id { get; }
    public ConnectionState State => _state;
    public ConnectionMetrics Metrics { get; } = new();
    public EndPoint? RemoteEndPoint { get; }

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

    internal ITransport Transport => _transport;
    internal PipeConnection Connection => _connection;

    internal TcpSession(long id, ITransport transport, PipeConnection connection, EndPoint? remoteEndPoint)
    {
        Id = id;
        _transport = transport;
        _connection = connection;
        RemoteEndPoint = remoteEndPoint;
        _state = ConnectionState.Connected;
    }

    internal void SetState(ConnectionState state) => _state = state;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            return;
        }

        await _connection.SendAsync(data, cancellationToken).ConfigureAwait(false);
        Metrics.AddBytesSent(data.Length);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_state is ConnectionState.Closing or ConnectionState.Closed)
        {
            return;
        }

        _state = ConnectionState.Closing;
        await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        _state = ConnectionState.Closed;
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