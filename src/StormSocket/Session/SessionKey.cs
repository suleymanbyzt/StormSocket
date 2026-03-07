namespace StormSocket.Session;

/// <summary>
/// A strongly-typed key for storing and retrieving session data via
/// <see cref="INetworkSession.Get{T}"/> and <see cref="INetworkSession.Set{T}"/>.
/// <example>
/// <code>
/// static readonly SessionKey&lt;string&gt; UserId = new("userId");
/// session.Set(UserId, "abc123");
/// string id = session.Get(UserId); // compile-time safe, no cast
/// </code>
/// </example>
/// </summary>
public sealed class SessionKey<T>
{
    public string Name { get; }

    public SessionKey(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}
