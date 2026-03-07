using StormSocket.Session;

namespace StormSocket.Samples.WsServer.Models;

public sealed class ConnectedUser
{
    public required ISession Session { get; init; }
    
    public string Name { get; set; } = "anonymous";
    
    
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public WebSocketSession Ws => (WebSocketSession)Session;
    
    public long Id => Session.Id;
}