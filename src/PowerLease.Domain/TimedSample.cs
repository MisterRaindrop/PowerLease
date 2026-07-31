namespace PowerLease.Domain;

/// <summary>A measurement and the monotonic instant it was taken at.</summary>
public readonly record struct TimedSample<T>(MonotonicStamp At, T Value);
