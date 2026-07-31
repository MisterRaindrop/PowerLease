namespace PowerLease.Domain;

/// <summary>
/// A lease re-established in the current epoch, together with how its remaining time was decided.
/// </summary>
public sealed record LeaseResumeResult(LeaseDeadline Deadline, LeaseResumeDecision Decision);
