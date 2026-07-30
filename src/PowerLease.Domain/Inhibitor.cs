namespace PowerLease.Domain;

public sealed record Inhibitor(
    InhibitorKind Kind,
    string Reason,
    DateTimeOffset SinceUtc,
    string? Detail = null);
