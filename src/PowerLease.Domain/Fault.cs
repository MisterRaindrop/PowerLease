namespace PowerLease.Domain;

/// <summary>A recorded failure that is currently keeping the machine awake.</summary>
public sealed record Fault(
    string Key,
    FaultSeverity Severity,
    string Message,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);
