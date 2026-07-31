using System.Text.Json.Serialization;

namespace PowerLease.Persistence.Configuration;

// The whole schema lives in one file on purpose: these records are the shape of a single JSON
// document, and reading them together is how you check that the file format is what you meant.
// Unmapped members are rejected everywhere. A configuration decides whether a machine stays
// awake, so a mistyped key that silently does nothing is worse than a startup error -- the error
// is recoverable, because the loader falls back to the last good file and latches a fault.
//
// Do not compare these with == where a collection is involved. Records compare collection
// properties by reference, so two configurations holding equal lists are not equal. Nothing in the
// product needs configuration equality; where a round trip has to be checked, the serialised form
// is compared instead, which is a stronger check because it also catches a property that failed to
// serialise at all.

/// <summary>
/// The contents of <c>config.json</c>.
/// <para>
/// This version keeps the machine awake and never puts it to sleep, so the sections describing
/// automatic power transitions are absent. They are not merely ignored: see
/// <see cref="RemovedConfigFields" />, which turns them into an explanatory error rather than
/// letting a user believe a setting took effect.
/// </para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerLeaseConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GeneralOptions General { get; init; } = new();

    public SshOptions Ssh { get; init; } = new();

    /// <summary>
    /// Thresholds for the machine's own activity.
    /// <para>
    /// The name is kept from the specification, but the direction is inverted in this version: these
    /// rules decide when the machine is <em>held awake</em>, never when it is allowed to sleep.
    /// Activity above a threshold is a reason to stay awake; falling below it for the required
    /// duration only stops being a reason. Nothing here can cause a sleep.
    /// </para>
    /// </summary>
    public IdleRuleOptions IdleRules { get; init; } = new();

    public ProtectedProcessOptions ProtectedProcesses { get; init; } = new();

    /// <summary>Windows during which the machine is held awake regardless of how idle it looks.</summary>
    public IReadOnlyList<ScheduleWindowOptions> Schedules { get; init; } = [];

    public EnergyOptions Energy { get; init; } = new();

    public RetentionOptions Retention { get; init; } = new();

    public LoggingOptions Logging { get; init; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneralOptions
{
    public string Language { get; init; } = "en-US";

    /// <summary>How long protection is held unconditionally after the machine resumes.</summary>
    public int ResumeGracePeriodMinutes { get; init; } = 10;

    /// <summary>
    /// How long protection is held unconditionally after the service restarts unexpectedly. The
    /// operating system drops the power request when the process dies, so the machine was
    /// unprotected until the service came back and nothing is known about what happened meanwhile.
    /// </summary>
    public int ServiceRecoveryGracePeriodMinutes { get; init; } = 15;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SshOptions
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<int> Ports { get; init; } = [22];

    /// <summary>
    /// How long a connection must have been established before it earns a long lease. A newer
    /// connection still counts as a reason to stay awake, because a connection that has not reached
    /// this age is one that is still authenticating.
    /// </summary>
    public int MinimumConnectionSeconds { get; init; } = 15;

    public int DefaultHoldMinutes { get; init; } = 180;

    public bool AutoRenew { get; init; } = true;

    public bool EventLogEnabled { get; init; } = true;

    public string? FileLogPath { get; init; }

    /// <summary>
    /// Whether the user has explicitly accepted that TCP state alone may be used to conclude no SSH
    /// session exists.
    /// <para>
    /// Default false, and that default is deliberate. Without the OpenSSH log channel the product
    /// cannot distinguish "no session" from "cannot tell", so it treats the situation as a reason to
    /// stay awake. Turning this on trades that safety for the machine being able to sleep on a
    /// system where the log channel is unavailable, and only the user can make that trade.
    /// </para>
    /// </summary>
    public bool TcpOnlyConfirmed { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdleRuleOptions
{
    public PercentRuleOptions Cpu { get; init; } = new() { Enabled = true, ThresholdPercent = 10, DurationMinutes = 20 };

    /// <summary>
    /// Off by default. Memory in use says little about whether work is happening: the Windows file
    /// cache holds memory indefinitely, and PostgreSQL, Docker and Ollama reserve it whether or not
    /// they are busy.
    /// </summary>
    public PercentRuleOptions Memory { get; init; } = new() { Enabled = false, ThresholdPercent = 70, DurationMinutes = 20 };

    public ThroughputRuleOptions Disk { get; init; } =
        new() { Enabled = true, ThresholdBytesPerSecond = 1048576, DurationMinutes = 15 };

    public ThroughputRuleOptions Network { get; init; } =
        new() { Enabled = true, ThresholdBytesPerSecond = 131072, DurationMinutes = 15 };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PercentRuleOptions
{
    public bool Enabled { get; init; }

    public double ThresholdPercent { get; init; }

    public int DurationMinutes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThroughputRuleOptions
{
    public bool Enabled { get; init; }

    public double ThresholdBytesPerSecond { get; init; }

    public int DurationMinutes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectedProcessOptions
{
    public IReadOnlyList<string> Names { get; init; } = [];

    public IReadOnlyList<string> CommandLinePatterns { get; init; } = [];

    /// <summary>
    /// A file whose presence keeps the machine awake. Null uses the default location under the data
    /// directory. This is the documented integration point for a build script.
    /// </summary>
    public string? LockFile { get; init; }
}

/// <summary>
/// One guaranteed-awake window. Times and days are strings so that a malformed value produces a
/// message naming the field and the value, rather than a deserialiser error about a position in a
/// byte stream.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduleWindowOptions
{
    /// <summary>Local start time, <c>HH:mm</c>.</summary>
    public string Start { get; init; } = "09:00";

    /// <summary>Local end time, <c>HH:mm</c>. Earlier than the start means the window runs past midnight.</summary>
    public string End { get; init; } = "18:00";

    /// <summary>Day names the window starts on, for example <c>["Monday", "Tuesday"]</c>.</summary>
    public IReadOnlyList<string> Days { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnergyOptions
{
    /// <summary>Where power readings come from: <c>Auto</c>, <c>Sensor</c> or <c>Estimate</c>.</summary>
    public string Source { get; init; } = "Auto";

    public double? IdleBaselineWatts { get; init; }

    public double? ElectricityPricePerKwh { get; init; }

    public string Currency { get; init; } = "CNY";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetentionOptions
{
    /// <summary>How long per-sample rows are kept.</summary>
    public int RawHours { get; init; } = 24;

    /// <summary>How long minute buckets are kept. Hour buckets are kept indefinitely.</summary>
    public int MinuteDays { get; init; } = 30;

    public int EventDays { get; init; } = 365;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoggingOptions
{
    public string MinimumLevel { get; init; } = "Information";

    public int RetentionDays { get; init; } = 30;
}
