namespace PowerLease.Persistence.Configuration;

/// <summary>
/// The configuration the service will run with, plus an honest account of how it got there.
/// </summary>
public sealed class ConfigLoadResult
{
    public ConfigLoadResult(PowerLeaseConfig config, ConfigSource source, IReadOnlyList<ConfigProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(problems);

        Config = config;
        Source = source;
        Problems = problems;
    }

    public PowerLeaseConfig Config { get; }

    public ConfigSource Source { get; }

    /// <summary>
    /// Everything wrong with what was on disk. Empty when the file loaded cleanly, or when there was
    /// no file at all: a first run is not a fault.
    /// </summary>
    public IReadOnlyList<ConfigProblem> Problems { get; }

    /// <summary>
    /// True when the running configuration is not what the file says.
    /// <para>
    /// The caller must latch a persistent fault for this, which is itself a reason to keep the
    /// machine awake: the configuration in use is not the one the user wrote, so the thresholds
    /// deciding when it is safe to release protection are not the ones they chose. The fault stays
    /// until the file is fixed, because nothing about a later successful read of an unrelated value
    /// makes the broken file correct.
    /// </para>
    /// </summary>
    public bool RequiresPersistentFault => Problems.Count > 0;

    /// <summary>One line per problem, for a log entry or a status message.</summary>
    public string ProblemSummary => string.Join(Environment.NewLine, Problems.Select(problem => problem.ToString()));
}
