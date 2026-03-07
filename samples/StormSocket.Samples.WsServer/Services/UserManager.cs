using System.Collections.Concurrent;
using StormSocket.Samples.WsServer.Models;
using StormSocket.Session;

namespace StormSocket.Samples.WsServer.Services;

public sealed class UserManager
{
    private readonly ConcurrentDictionary<long, ConnectedUser> _users = new();

    public int Count => _users.Count;
    public IEnumerable<ConnectedUser> All => _users.Values;

    public ConnectedUser Add(ISession networkSession)
    {
        ConnectedUser user = new() { NetworkSession = networkSession };
        _users.TryAdd(networkSession.Id, user);
        return user;
    }

    public bool Remove(long id, out ConnectedUser? user)
    {
        return _users.TryRemove(id, out user);
    }

    public ConnectedUser? Get(long id)
    {
        _users.TryGetValue(id, out ConnectedUser? user);
        return user;
    }

    public bool IsNameTaken(string name, long excludeId = -1)
    {
        return _users.Values.Any(u =>
            u.Id != excludeId &&
            string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}