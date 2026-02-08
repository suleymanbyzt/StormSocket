using System.Net;
using StormSocket.Core;
using StormSocket.Session;

namespace StormSocket.Client;

/// <summary>
/// Internal adapter that allows a client to satisfy the ISession interface
/// required by MiddlewarePipeline. Not exposed publicly.
/// </summary>
internal sealed class ClientSessionAdapter : ISession
{
    private readonly StormTcpClient? _tcpClient;
    private readonly StormWebSocketClient? _wsClient;

    internal ClientSessionAdapter(StormTcpClient client)
    {
        _tcpClient = client;
    }

    internal ClientSessionAdapter(StormWebSocketClient client)
    {
        _wsClient = client;
    }

    public long Id => 0;

    public ConnectionState State => _tcpClient?.State ?? _wsClient?.State ?? ConnectionState.Closed;

    public ConnectionMetrics Metrics => _tcpClient?.Metrics ?? _wsClient?.Metrics ?? new ConnectionMetrics();

    public EndPoint? RemoteEndPoint => _tcpClient?.RemoteEndPoint ?? _wsClient?.RemoteEndPoint;

    public bool IsBackpressured => false;

    public IReadOnlySet<string> Groups => new HashSet<string>();

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_tcpClient is not null)
        {
            await _tcpClient.SendAsync(data, cancellationToken).ConfigureAwait(false);
        }
        else if (_wsClient is not null)
        {
            await _wsClient.SendAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpClient is not null)
        {
            await _tcpClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            
        }
        else if (_wsClient is not null)
        {
            await _wsClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Abort() { }

    public void JoinGroup(string group) { }

    public void LeaveGroup(string group) { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}