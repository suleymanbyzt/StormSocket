using System.Collections.Concurrent;
using System.Net;

namespace StormSocket.Core;

/// <summary>
/// Admission control for accepted sockets: enforces the global and per-IP connection limits.
/// </summary>
/// <remarks>
/// The limits are claimed at accept time rather than after the handshake. A limit counted from
/// established sessions only can be walked straight past by opening sockets and never completing
/// TLS or the WebSocket upgrade — every one of those still holds a socket, two pipes and a task.
/// </remarks>
internal sealed class ConnectionGate
{
    private readonly int _maxConnections;
    private readonly int _maxConnectionsPerIp;
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private int _count;

    public ConnectionGate(int maxConnections, int maxConnectionsPerIp)
    {
        _maxConnections = maxConnections;
        _maxConnectionsPerIp = maxConnectionsPerIp;
    }

    /// <summary>Connections currently admitted, including those still handshaking.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Claims a slot for a newly accepted connection.
    /// </summary>
    /// <param name="remoteEndPoint">Peer address; a non-IP endpoint (Unix socket) skips the per-IP limit.</param>
    /// <param name="lease">Token to hand back to <see cref="Release"/> when the connection ends.</param>
    /// <returns>False when a limit is reached and the socket must be closed immediately.</returns>
    public bool TryAcquire(EndPoint? remoteEndPoint, out IPAddress? lease)
    {
        lease = null;

        if (_maxConnections > 0 && !TryIncrementTotal())
        {
            return false;
        }

        if (_maxConnections <= 0)
        {
            Interlocked.Increment(ref _count);
        }

        if (_maxConnectionsPerIp > 0 && remoteEndPoint is IPEndPoint ip)
        {
            if (!TryIncrementPerIp(ip.Address))
            {
                Interlocked.Decrement(ref _count);
                return false;
            }

            lease = ip.Address;
        }

        return true;
    }

    /// <summary>Returns a slot claimed by <see cref="TryAcquire"/>. Must be called exactly once per admitted connection.</summary>
    public void Release(IPAddress? lease)
    {
        Interlocked.Decrement(ref _count);

        if (lease is null)
        {
            return;
        }

        // Drop the key once it hits zero so the dictionary cannot grow without bound across a long
        // run of short-lived connections from many addresses.
        while (true)
        {
            if (!_perIp.TryGetValue(lease, out int current))
            {
                return;
            }

            if (current <= 1)
            {
                if (((ICollection<KeyValuePair<IPAddress, int>>)_perIp).Remove(new KeyValuePair<IPAddress, int>(lease, current)))
                {
                    return;
                }

                continue;
            }

            if (_perIp.TryUpdate(lease, current - 1, current))
            {
                return;
            }
        }
    }

    private bool TryIncrementTotal()
    {
        while (true)
        {
            int current = Volatile.Read(ref _count);
            if (current >= _maxConnections)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _count, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private bool TryIncrementPerIp(IPAddress address)
    {
        while (true)
        {
            if (_perIp.TryGetValue(address, out int current))
            {
                if (current >= _maxConnectionsPerIp)
                {
                    return false;
                }

                if (_perIp.TryUpdate(address, current + 1, current))
                {
                    return true;
                }

                continue;
            }

            if (_perIp.TryAdd(address, 1))
            {
                return true;
            }
        }
    }
}
