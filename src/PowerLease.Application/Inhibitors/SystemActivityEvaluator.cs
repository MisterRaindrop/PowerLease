using PowerLease.Domain;

namespace PowerLease.Application.Inhibitors;

/// <summary>One reading of the machine's load. A null value means that metric could not be read.</summary>
public sealed record SystemMetricSample(
    double? CpuPercent = null,
    double? MemoryPercent = null,
    double? DiskBytesPerSecond = null,
    double? NetworkBytesPerSecond = null);

public interface ISystemMetricProvider
{
    SystemMetricSample Read();
}

/// <summary>One threshold rule.</summary>
public sealed record ActivityRule(bool Enabled, double Threshold, TimeSpan QuietDuration);

public sealed record SystemActivityOptions
{
    public ActivityRule Cpu { get; init; } = new(true, 10, TimeSpan.FromMinutes(20));

    public ActivityRule Memory { get; init; } = new(false, 70, TimeSpan.FromMinutes(20));

    public ActivityRule Disk { get; init; } = new(true, 1048576, TimeSpan.FromMinutes(15));

    public ActivityRule Network { get; init; } = new(true, 131072, TimeSpan.FromMinutes(15));

    /// <summary>
    /// The longest tolerated gap between readings. A wider one could have hidden a burst of work, so quiet
    /// cannot be claimed across it.
    /// </summary>
    public TimeSpan MaxSampleGap { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Holds the machine awake while it is busy.
/// <para>
/// This covers what Windows' own idle timer cannot see. A four-hour compile, a Docker build, a database
/// rebuilding an index: nobody is touching the keyboard, so as far as the operating system is concerned the
/// machine is idle and may be put to sleep. Here, load above the threshold is a reason to stay awake, and
/// only continuous quiet for the whole configured duration stops being one.
/// </para>
/// </summary>
public sealed class SystemActivityEvaluator
{
    public const string SourceId = "activity";

    private readonly List<(string Name, DurationThresholdTracker Tracker, Func<SystemMetricSample, double?> Read)> _rules = [];

    public SystemActivityEvaluator(SystemActivityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Add("CPU", options.Cpu, options.MaxSampleGap, sample => sample.CpuPercent);
        Add("memory", options.Memory, options.MaxSampleGap, sample => sample.MemoryPercent);
        Add("disk", options.Disk, options.MaxSampleGap, sample => sample.DiskBytesPerSecond);
        Add("network", options.Network, options.MaxSampleGap, sample => sample.NetworkBytesPerSecond);
    }

    /// <summary>Whether any rule is switched on. With none, this source has nothing to say.</summary>
    public bool HasEnabledRules => _rules.Count > 0;

    /// <summary>
    /// Record a reading. A metric that could not be read is not recorded, which leaves its rule short of the
    /// history it needs and therefore unable to establish quiet.
    /// </summary>
    public void Observe(SystemMetricSample sample, MonotonicStamp at)
    {
        ArgumentNullException.ThrowIfNull(sample);

        foreach (var (_, tracker, read) in _rules)
        {
            if (read(sample) is { } value && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                tracker.Observe(at, value);
            }
        }
    }

    public InhibitorSourceReport Evaluate(MonotonicStamp now, DateTimeOffset nowUtc)
    {
        if (_rules.Count == 0)
        {
            return InhibitorSourceReport.ConfirmedAbsent(SourceId);
        }

        var inhibitors = new List<Inhibitor>();

        foreach (var (name, tracker, _) in _rules)
        {
            var assessment = tracker.Assess(now);
            if (!assessment.Inhibits)
            {
                continue;
            }

            var reason = assessment.Verdict == ActivityVerdict.Active
                ? $"{name} is above the idle threshold"
                : $"{name} has not been quiet long enough to be sure ({assessment.QuietFor:hh\\:mm\\:ss} so far)";

            inhibitors.Add(new Inhibitor(InhibitorKind.SystemActivity, reason, nowUtc, name));
        }

        return inhibitors.Count == 0
            ? InhibitorSourceReport.ConfirmedAbsent(SourceId)
            : InhibitorSourceReport.Observed(SourceId, [.. inhibitors]);
    }

    private void Add(string name, ActivityRule rule, TimeSpan maxGap, Func<SystemMetricSample, double?> read)
    {
        if (rule.Enabled)
        {
            _rules.Add((name, new DurationThresholdTracker(rule.Threshold, rule.QuietDuration, maxGap), read));
        }
    }
}
