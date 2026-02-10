using System.Net;

namespace StormSocket.WebSocket;

/// <summary>
/// Context for WebSocket upgrade request validation and authentication.
/// Provides access to HTTP request details before accepting the connection.
/// </summary>
public sealed class WsUpgradeContext
{
    private bool _handled;
    private bool _accepted;
    private int _rejectStatusCode;
    private string? _rejectReason;

    /// <summary>
    /// The request path (e.g., "/chat").
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The raw query string without the leading '?' (e.g., "room=general&amp;token=abc").
    /// </summary>
    public string? QueryString { get; }

    /// <summary>
    /// Parsed query parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>
    /// All HTTP headers from the upgrade request.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// The remote endpoint of the connecting client.
    /// </summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// The Sec-WebSocket-Key from the upgrade request.
    /// </summary>
    internal string WsKey { get; }

    internal bool IsHandled => _handled;
    internal bool IsAccepted => _accepted;
    internal int RejectStatusCode => _rejectStatusCode;
    internal string? RejectReason => _rejectReason;

    internal WsUpgradeContext(
        string path,
        string? queryString,
        IReadOnlyDictionary<string, string> headers,
        string wsKey,
        EndPoint? remoteEndPoint)
    {
        Path = path;
        QueryString = queryString;
        Query = ParseQueryString(queryString);
        Headers = headers;
        WsKey = wsKey;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>
    /// Accept the WebSocket connection.
    /// </summary>
    public void Accept()
    {
        if (_handled)
        {
            throw new InvalidOperationException("Upgrade request already handled.");
        }

        _handled = true;
        _accepted = true;
    }

    /// <summary>
    /// Reject the WebSocket connection with a status code and optional reason.
    /// </summary>
    /// <param name="statusCode">HTTP status code (e.g., 401, 403).</param>
    /// <param name="reason">Optional reason phrase for the response body.</param>
    public void Reject(int statusCode = 403, string? reason = null)
    {
        if (_handled)
        {
            throw new InvalidOperationException("Upgrade request already handled.");
        }

        _handled = true;
        _accepted = false;
        _rejectStatusCode = statusCode;
        _rejectReason = reason ?? GetDefaultReason(statusCode);
    }

    private static string GetDefaultReason(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        _ => "Rejected",
    };

    private static IReadOnlyDictionary<string, string> ParseQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return new Dictionary<string, string>();
        }

        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string part in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = Uri.UnescapeDataString(part[..eqIndex]);
                string value = Uri.UnescapeDataString(part[(eqIndex + 1)..]);
                result[key] = value;
            }
            else
            {
                result[Uri.UnescapeDataString(part)] = string.Empty;
            }
        }

        return result;
    }
}