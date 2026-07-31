namespace PowerLease.Ipc.Contracts;

public sealed record RequestEnvelope(
    int ProtocolVersion,
    Guid RequestId,
    string Method,
    string? PayloadJson = null);
