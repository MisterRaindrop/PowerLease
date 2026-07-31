namespace PowerLease.Application.Inhibitors;

/// <summary>An established inbound TCP connection.</summary>
/// <param name="LocalPort">The port on this machine, which is what identifies it as SSH.</param>
/// <param name="RemoteAddress">Textual form of the peer address, IPv4 or IPv6.</param>
/// <param name="OwningProcessId">The process holding the socket, when the platform reports one.</param>
public sealed record TcpConnection(string RemoteAddress, int RemotePort, int LocalPort, int? OwningProcessId)
{
    /// <summary>
    /// Identity of the connection across snapshots. Four-tuple rather than a handle, because the platform
    /// enumeration hands back a fresh table each time with nothing stable to hold on to.
    /// </summary>
    public string Key => $"{LocalPort}<-{RemoteAddress}:{RemotePort}";
}

/// <summary>
/// The established inbound connections, or the fact that they could not be read.
/// </summary>
/// <param name="Succeeded">
/// False when the enumeration failed. The connection list is then meaningless and must not be read as
/// "nothing is connected".
/// </param>
public sealed record TcpSnapshot(bool Succeeded, IReadOnlyList<TcpConnection> Connections, string? Detail = null)
{
    public static TcpSnapshot Of(params TcpConnection[] connections) => new(true, connections);

    public static TcpSnapshot Unavailable(string detail) => new(false, [], detail);
}

/// <summary>Reads the machine's established inbound connections.</summary>
public interface ITcpConnectionProvider
{
    /// <summary>
    /// Every established inbound connection. Implementations must enumerate IPv4 and IPv6 separately and
    /// report a failure rather than an empty list, because an empty list is a statement that nobody is
    /// connected.
    /// </summary>
    TcpSnapshot GetEstablishedConnections();
}
