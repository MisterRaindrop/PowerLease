namespace PowerLease.Application.Inhibitors;

/// <summary>Whether the SSH authentication log can be believed.</summary>
public enum SshLogChannelState
{
    /// <summary>Readable and continuous from the recorded position.</summary>
    Available,

    /// <summary>
    /// There is no OpenSSH log to read. Without it the product cannot tell "nobody is connected" from
    /// "cannot see who is connected", which is why concluding absence from connection state alone has to be
    /// something the user opts into.
    /// </summary>
    NotInstalled,

    /// <summary>A read failed this time. Says nothing about whether anyone is connected.</summary>
    TransientFailure,

    /// <summary>
    /// The log was cleared, rolled over, or the recorded position no longer exists, so events between then
    /// and now are gone. Logins in that gap were never seen.
    /// </summary>
    Discontinuous
}

public enum SshAuthEventKind
{
    Authenticated,
    Disconnected
}

/// <summary>One entry from the SSH authentication log.</summary>
/// <param name="RecordId">
/// Stable identity of the entry, so that reading the log again cannot act on it twice.
/// </param>
public sealed record SshAuthEvent(
    string RecordId,
    SshAuthEventKind Kind,
    DateTimeOffset OccurredAtUtc,
    string? UserName = null,
    string? RemoteAddress = null,
    int? RemotePort = null);

/// <summary>The result of reading the log from a recorded position.</summary>
/// <param name="Bookmark">
/// Where reading stopped. Only ever stored together with the effects of the events up to it: storing it
/// first and then failing would drop those logins for good, and with them the long hold each one should
/// have created.
/// </param>
public sealed record SshLogRead(
    SshLogChannelState State,
    IReadOnlyList<SshAuthEvent> Events,
    string? Bookmark = null,
    string? Detail = null)
{
    public static SshLogRead Available(IReadOnlyList<SshAuthEvent> events, string? bookmark) =>
        new(SshLogChannelState.Available, events, bookmark);

    public static SshLogRead Unavailable(SshLogChannelState state, string detail) => new(state, [], null, detail);
}

/// <summary>Reads the SSH authentication log.</summary>
public interface ISshAuthLogReader
{
    /// <summary>Read from <paramref name="bookmark" />, or from the present if it is null.</summary>
    SshLogRead Read(string? bookmark);
}
