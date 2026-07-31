using System.Globalization;

namespace PowerLease.Persistence.Configuration;

/// <summary>
/// Checks a configuration completely before it is trusted or written.
/// <para>
/// Every problem is collected rather than stopping at the first, because a user fixing a
/// configuration file one error per restart is a user who gives up and deletes it.
/// </para>
/// </summary>
public static class ConfigValidator
{
    private static readonly string[] LogLevels =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    private static readonly string[] EnergySources = ["Auto", "Sensor", "Estimate"];

    public static IReadOnlyList<ConfigProblem> Validate(PowerLeaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<ConfigProblem>();

        if (config.SchemaVersion != PowerLeaseConfig.CurrentSchemaVersion)
        {
            problems.Add(new ConfigProblem(
                "schemaVersion",
                $"Expected {PowerLeaseConfig.CurrentSchemaVersion} but found {config.SchemaVersion}. " +
                "A file from a newer version cannot be interpreted safely."));
        }

        ValidateGeneral(config.General, problems);
        ValidateSsh(config.Ssh, problems);
        ValidateIdleRules(config.IdleRules, problems);
        ValidateProtectedProcesses(config.ProtectedProcesses, problems);
        ValidateSchedules(config.Schedules, problems);
        ValidateEnergy(config.Energy, problems);
        ValidateRetention(config.Retention, problems);
        ValidateLogging(config.Logging, problems);

        return problems;
    }

    private static void ValidateGeneral(GeneralOptions general, List<ConfigProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(general.Language))
        {
            problems.Add(new ConfigProblem("general.language", "A language tag is required, for example 'en-US'."));
        }

        RequireInRange(general.ResumeGracePeriodMinutes, 0, 1440, "general.resumeGracePeriodMinutes", problems);
        RequireInRange(
            general.ServiceRecoveryGracePeriodMinutes, 0, 1440, "general.serviceRecoveryGracePeriodMinutes", problems);
    }

    private static void ValidateSsh(SshOptions ssh, List<ConfigProblem> problems)
    {
        if (ssh.Enabled && ssh.Ports.Count == 0)
        {
            problems.Add(new ConfigProblem(
                "ssh.ports", "At least one port is required while SSH detection is enabled."));
        }

        var seen = new HashSet<int>();
        for (var i = 0; i < ssh.Ports.Count; i++)
        {
            var port = ssh.Ports[i];
            if (port is < 1 or > 65535)
            {
                problems.Add(new ConfigProblem($"ssh.ports[{i}]", $"{port} is not a TCP port number (1 to 65535)."));
            }
            else if (!seen.Add(port))
            {
                problems.Add(new ConfigProblem($"ssh.ports[{i}]", $"Port {port} is listed more than once."));
            }
        }

        RequireInRange(ssh.MinimumConnectionSeconds, 0, 3600, "ssh.minimumConnectionSeconds", problems);
        RequireInRange(ssh.DefaultHoldMinutes, 1, 7 * 24 * 60, "ssh.defaultHoldMinutes", problems);

        if (ssh.FileLogPath is { } logPath && !Path.IsPathRooted(logPath))
        {
            problems.Add(new ConfigProblem(
                "ssh.fileLogPath",
                $"'{logPath}' must be an absolute path. The service runs as a system account, so a " +
                "relative path would resolve somewhere the user did not intend."));
        }
    }

    private static void ValidateIdleRules(IdleRuleOptions rules, List<ConfigProblem> problems)
    {
        ValidatePercentRule(rules.Cpu, "idleRules.cpu", problems);
        ValidatePercentRule(rules.Memory, "idleRules.memory", problems);
        ValidateThroughputRule(rules.Disk, "idleRules.disk", problems);
        ValidateThroughputRule(rules.Network, "idleRules.network", problems);
    }

    private static void ValidatePercentRule(PercentRuleOptions rule, string path, List<ConfigProblem> problems)
    {
        RequireNumber(rule.ThresholdPercent, $"{path}.thresholdPercent", problems);
        if (!double.IsNaN(rule.ThresholdPercent) && rule.ThresholdPercent is < 0 or > 100)
        {
            problems.Add(new ConfigProblem(
                $"{path}.thresholdPercent",
                $"{rule.ThresholdPercent.ToString(CultureInfo.InvariantCulture)} is not a percentage (0 to 100)."));
        }

        RequireInRange(rule.DurationMinutes, 1, 1440, $"{path}.durationMinutes", problems);
    }

    private static void ValidateThroughputRule(ThroughputRuleOptions rule, string path, List<ConfigProblem> problems)
    {
        RequireNumber(rule.ThresholdBytesPerSecond, $"{path}.thresholdBytesPerSecond", problems);
        if (!double.IsNaN(rule.ThresholdBytesPerSecond) && rule.ThresholdBytesPerSecond < 0)
        {
            problems.Add(new ConfigProblem(
                $"{path}.thresholdBytesPerSecond", "A throughput threshold cannot be negative."));
        }

        RequireInRange(rule.DurationMinutes, 1, 1440, $"{path}.durationMinutes", problems);
    }

    private static void ValidateProtectedProcesses(ProtectedProcessOptions processes, List<ConfigProblem> problems)
    {
        for (var i = 0; i < processes.Names.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(processes.Names[i]))
            {
                problems.Add(new ConfigProblem($"protectedProcesses.names[{i}]", "A process name cannot be blank."));
            }
        }

        for (var i = 0; i < processes.CommandLinePatterns.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(processes.CommandLinePatterns[i]))
            {
                problems.Add(new ConfigProblem(
                    $"protectedProcesses.commandLinePatterns[{i}]", "A pattern cannot be blank."));
            }
        }

        if (processes.LockFile is { } lockFile && !Path.IsPathRooted(lockFile))
        {
            problems.Add(new ConfigProblem(
                "protectedProcesses.lockFile",
                $"'{lockFile}' must be an absolute path, because the service's working directory is " +
                "not where the user is looking."));
        }
    }

    private static void ValidateSchedules(
        IReadOnlyList<ScheduleWindowOptions> schedules,
        List<ConfigProblem> problems)
    {
        for (var i = 0; i < schedules.Count; i++)
        {
            var problem = ConfigSchedules.TryConvert(schedules[i], $"schedules[{i}]", out _);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }
    }

    private static void ValidateEnergy(EnergyOptions energy, List<ConfigProblem> problems)
    {
        if (!EnergySources.Contains(energy.Source, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(new ConfigProblem(
                "energy.source", $"'{energy.Source}' is not one of {string.Join(", ", EnergySources)}."));
        }

        RequireNonNegativeIfPresent(energy.IdleBaselineWatts, "energy.idleBaselineWatts", problems);
        RequireNonNegativeIfPresent(energy.ElectricityPricePerKwh, "energy.electricityPricePerKwh", problems);

        if (string.IsNullOrWhiteSpace(energy.Currency))
        {
            problems.Add(new ConfigProblem("energy.currency", "A currency code is required."));
        }
    }

    private static void ValidateRetention(RetentionOptions retention, List<ConfigProblem> problems)
    {
        RequireInRange(retention.RawHours, 1, 24 * 365, "retention.rawHours", problems);
        RequireInRange(retention.MinuteDays, 1, 3650, "retention.minuteDays", problems);
        RequireInRange(retention.EventDays, 1, 36500, "retention.eventDays", problems);

        // Deleting per-sample rows before the minute buckets built from them would throw away the
        // only copy of that history.
        if (retention.RawHours > retention.MinuteDays * 24)
        {
            problems.Add(new ConfigProblem(
                "retention.rawHours",
                $"Per-sample rows are kept for {retention.RawHours} hours, longer than the " +
                $"{retention.MinuteDays} days of minute buckets aggregated from them."));
        }
    }

    private static void ValidateLogging(LoggingOptions logging, List<ConfigProblem> problems)
    {
        if (!LogLevels.Contains(logging.MinimumLevel, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(new ConfigProblem(
                "logging.minimumLevel", $"'{logging.MinimumLevel}' is not one of {string.Join(", ", LogLevels)}."));
        }

        RequireInRange(logging.RetentionDays, 1, 3650, "logging.retentionDays", problems);
    }

    private static void RequireInRange(int value, int min, int max, string path, List<ConfigProblem> problems)
    {
        if (value < min || value > max)
        {
            problems.Add(new ConfigProblem(path, $"{value} is outside the allowed range {min} to {max}."));
        }
    }

    private static void RequireNumber(double value, string path, List<ConfigProblem> problems)
    {
        // A threshold that is not a number compares false against every measurement, which would
        // classify all activity as quiet and release protection. It must never reach the tracker.
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            problems.Add(new ConfigProblem(path, "Must be a finite number."));
        }
    }

    private static void RequireNonNegativeIfPresent(double? value, string path, List<ConfigProblem> problems)
    {
        if (value is not { } present)
        {
            return;
        }

        if (double.IsNaN(present) || double.IsInfinity(present))
        {
            problems.Add(new ConfigProblem(path, "Must be a finite number."));
        }
        else if (present < 0)
        {
            problems.Add(new ConfigProblem(path, "Cannot be negative."));
        }
    }
}
