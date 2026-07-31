using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

public enum LeaseCommandKind
{
    Create,
    Renew,
    Release
}

/// <summary>A request to change the leases, carrying everything needed to decide it without I/O.</summary>
public sealed record LeaseCommand
{
    public required string RequestId { get; init; }

    public required CallerSnapshot Caller { get; init; }

    public required LeaseCommandKind Kind { get; init; }

    /// <summary>Which lease, for a renewal or a release.</summary>
    public string? LeaseId { get; init; }

    public TimeSpan Duration { get; init; }

    public LeaseSource Source { get; init; } = LeaseSource.Cli;

    public string? Reason { get; init; }

    /// <summary>
    /// When this request stops being worth carrying out, on the monotonic clock.
    /// <para>
    /// Measured by the server, not the caller: a deadline sent as a wall-clock time would be worthless
    /// across a clock adjustment and trivially forged by the caller.
    /// </para>
    /// </summary>
    public required MonotonicStamp Deadline { get; init; }

    /// <summary>
    /// A hash of the request content. Stored with the request identifier so that the same identifier
    /// arriving with different content is refused rather than answered from the earlier result.
    /// </summary>
    public required string PayloadHash { get; init; }
}

public enum LeaseCommandStatus
{
    Created,
    Renewed,
    Released,

    /// <summary>A retry of something already carried out. The stored result is returned unchanged.</summary>
    AlreadyDone,

    /// <summary>Refused: no such lease, not the caller's lease, or the identifier was reused for different content.</summary>
    Rejected,

    /// <summary>
    /// The request sat in the queue past its deadline and was not carried out. Nothing was changed, so
    /// the caller may safely retry with the same identifier.
    /// </summary>
    DeadlineExpired,

    /// <summary>Could not be made durable. Protection was not reduced.</summary>
    Failed
}

public sealed record LeaseCommandResult(
    string RequestId,
    LeaseCommandStatus Status,
    string? LeaseId = null,
    string? ResultJson = null,
    string? Error = null);
