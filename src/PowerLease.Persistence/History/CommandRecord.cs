namespace PowerLease.Persistence.History;

/// <summary>What happened when a command was recorded for the second time.</summary>
public enum CommandRecordOutcome
{
    /// <summary>First time this caller has sent this request identifier. Go ahead and act.</summary>
    Recorded,

    /// <summary>
    /// Already carried out. Return the stored result and do not act again: this is a client retrying
    /// after a reply was lost, not a second request.
    /// </summary>
    AlreadyCompleted,

    /// <summary>
    /// The identifier has been used before with different content. Refused rather than answered from
    /// the earlier result, because the caller and the service disagree about what the request is.
    /// </summary>
    PayloadConflict
}

/// <param name="Outcome">What to do about this command.</param>
/// <param name="ExistingResultJson">The stored reply, when the command had already been carried out.</param>
public sealed record CommandRecord(CommandRecordOutcome Outcome, string? ExistingResultJson);
