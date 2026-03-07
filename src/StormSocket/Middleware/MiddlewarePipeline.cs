using StormSocket.Core;
using StormSocket.Session;

namespace StormSocket.Middleware;

public sealed class MiddlewarePipeline
{
    private readonly List<IConnectionMiddleware> _middlewares = [];

    public void Use(IConnectionMiddleware middleware)
    {
        _middlewares.Add(middleware);
    }

    public async ValueTask OnConnectedAsync(ISession networkSession)
    {
        for (int i = 0; i < _middlewares.Count; i++)
        {
            await _middlewares[i].OnConnectedAsync(networkSession).ConfigureAwait(false);
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession networkSession, ReadOnlyMemory<byte> data)
    {
        if (_middlewares.Count == 0)
        {
            return new ValueTask<ReadOnlyMemory<byte>>(data);
        }

        return OnDataReceivedSlowAsync(networkSession, data);
    }

    private async ValueTask<ReadOnlyMemory<byte>> OnDataReceivedSlowAsync(ISession networkSession, ReadOnlyMemory<byte> data)
    {
        ReadOnlyMemory<byte> current = data;

        for (int i = 0; i < _middlewares.Count; i++)
        {
            current = await _middlewares[i].OnDataReceivedAsync(networkSession, current).ConfigureAwait(false);
            if (current.IsEmpty)
            {
                break;
            }
        }

        return current;
    }

    public async ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(ISession networkSession, ReadOnlyMemory<byte> data)
    {
        ReadOnlyMemory<byte> current = data;
        
        for (int i = 0; i < _middlewares.Count; i++)
        {
            current = await _middlewares[i].OnDataSendingAsync(networkSession, current).ConfigureAwait(false);
            if (current.IsEmpty)
            {
                break;
            }
        }
        
        return current;
    }

    public async ValueTask OnDisconnectedAsync(ISession networkSession, DisconnectReason reason)
    {
        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            await _middlewares[i].OnDisconnectedAsync(networkSession, reason).ConfigureAwait(false);
        }
    }

    public async ValueTask OnErrorAsync(ISession networkSession, Exception exception)
    {
        for (int i = 0; i < _middlewares.Count; i++)
        {
            await _middlewares[i].OnErrorAsync(networkSession, exception).ConfigureAwait(false);
        }
    }
}