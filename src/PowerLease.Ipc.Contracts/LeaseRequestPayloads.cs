namespace PowerLease.Ipc.Contracts;

public sealed record CreateLeasePayload(TimeSpan Duration, string? Reason);

public sealed record RenewLeasePayload(string? LeaseId, TimeSpan Duration);

public sealed record ReleaseLeasePayload(string? LeaseId);
