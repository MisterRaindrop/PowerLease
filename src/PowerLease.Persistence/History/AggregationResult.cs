namespace PowerLease.Persistence.History;

/// <summary>What an aggregation run produced.</summary>
/// <param name="BucketsWritten">
/// How many buckets were written. Periods with no measurements produce no bucket: the absence of a row
/// is how "the machine was not running then" is recorded, rather than a row full of zeroes that reads
/// like a perfectly idle machine.
/// </param>
/// <param name="CompletedThroughUtc">
/// How far aggregation has now been completed. Source rows are only ever deleted up to this point.
/// </param>
public sealed record AggregationResult(int BucketsWritten, DateTimeOffset? CompletedThroughUtc);
