using System.Net;
using System.Text;
using System.Threading.Tasks;
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
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _groupLock = new();
    private readonly HashSet<string> _groups = [];
    private volatile ConnectionState _state;
    private WsHeartbeat? _heartbeat;

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

    internal WebSocketSession(long id, ITransport transport, EndPoint? remoteEndPoint)
    {
        Id = id;
        _transport = transport;
        RemoteEndPoint = remoteEndPoint;
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
    internal async ValueTask WriteFrameAsync(Action<System.IO.Pipelines.PipeWriter> writeAction, int byteCount = 0, CancellationToken cancellationToken = default)
    {
        if (_state is not ConnectionState.Connected)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            writeAction(_transport.Output);
            await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
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
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await WriteFrameAsync(
            writer => WsFrameEncoder.WriteBinary(writer, data.Span),
            data.Length,
            cancellationToken);
    }

    /// <summary>Sends a Text WebSocket frame (UTF-8 encoded) to the client.</summary>
    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await WriteFrameAsync(
            writer => WsFrameEncoder.WriteText(writer, bytes),
            bytes.Length,
            cancellationToken);
    }

    /// <summary>Sends a Text WebSocket frame from pre-encoded UTF-8 bytes (zero-copy).</summary>
    public async ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Data, CancellationToken cancellationToken = default)
    {
        await WriteFrameAsync(
            writer => WsFrameEncoder.WriteText(writer, utf8Data.Span),
            utf8Data.Length,
            cancellationToken);
    }

    /// <summary>Sends a Close frame and shuts down the connection.</summary>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_state is ConnectionState.Closing or ConnectionState.Closed)
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

        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
        }

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
        _writeLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}