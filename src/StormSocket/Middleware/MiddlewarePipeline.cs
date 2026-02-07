using System.Threading.Tasks;
using StormSocket.Session;

namespace StormSocket.Middleware;

public sealed class MiddlewarePipeline
{
    private readonly List<IConnectionMiddleware> _middlewares = [];

    public void Use(IConnectionMiddleware middleware)
    {
        _middlewares.Add(middleware);
    }

    public async ValueTask OnConnectedAsync(ISession session)
    {
        for (int i = 0; i < _middlewares.Count; i++)
        {
            await _middlewares[i].OnConnectedAsync(session).ConfigureAwait(false);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        ReadOnlyMemory<byte> current = data;
        
        for (int i = 0; i < _middlewares.Count; i++)
        {
            current = await _middlewares[i].OnDataReceivedAsync(session, current).ConfigureAwait(false);
            if (current.IsEmpty)
            {
                break;
            }
        }
        
        return current;
    }

    public async ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        ReadOnlyMemory<byte> current = data;
        
        for (int i = 0; i < _middlewares.Count; i++)
        {
            current = await _middlewares[i].OnDataSendingAsync(session, current).ConfigureAwait(false);
            if (current.IsEmpty)
            {
                break;
            }
        }
        
        return current;
    }

    public async ValueTask OnDisconnectedAsync(ISession session)
    {
        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            await _middlewares[i].OnDisconnectedAsync(session).ConfigureAwait(false);
        }
    }

    public async ValueTask OnErrorAsync(ISession session, Exception exception)
    {
        for (int i = 0; i < _middlewares.Count; i++)
        {
            await _middlewares[i].OnErrorAsync(session, exception).ConfigureAwait(false);
        }
    }
}