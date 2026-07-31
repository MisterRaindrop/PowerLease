namespace PowerLease.Application.Kernel;

/// <summary>
/// Who asked for something, captured as plain values.
/// <para>
/// Copied from the caller's token before the request is queued, and never re-read afterwards. Holding
/// a borrowed token handle across the queue would mean deciding permission against an identity that
/// may have been revoked, and the request body is never trusted for identity at all: it is supplied by
/// the caller.
/// </para>
/// </summary>
public sealed record CallerSnapshot
{
    /// <summary>The caller's security identifier, resolved from its token by the pipe server.</summary>
    public required string Sid { get; init; }

    public string? AccountName { get; init; }

    /// <summary>Whether the caller was running elevated, decided when the request arrived.</summary>
    public bool IsElevated { get; init; }

    /// <summary>Whether the caller is a machine administrator, decided when the request arrived.</summary>
    public bool IsAdministrator { get; init; }
}
