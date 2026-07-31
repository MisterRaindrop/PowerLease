using PowerLease.Domain;

namespace PowerLease.Application.Inhibitors;

/// <summary>
/// A running process.
/// </summary>
/// <param name="CommandLine">
/// Null when it could not be read. That happens routinely: a service reading another account's process needs
/// privileges it may not have, and the process may exit between being listed and being examined.
/// </param>
public sealed record ProcessInfo(int ProcessId, string Name, string? CommandLine);

public sealed record ProcessSnapshot(bool Succeeded, IReadOnlyList<ProcessInfo> Processes, string? Detail = null)
{
    public static ProcessSnapshot Of(params ProcessInfo[] processes) => new(true, processes);

    public static ProcessSnapshot Unavailable(string detail) => new(false, [], detail);
}

public interface IProcessSnapshotProvider
{
    ProcessSnapshot GetProcesses();
}

public sealed record ProtectedProcessOptions
{
    /// <summary>Process names to hold for, matched without case.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>Substrings to look for in a command line, matched without case.</summary>
    public IReadOnlyList<string> CommandLinePatterns { get; init; } = [];
}

/// <summary>
/// Holds the machine awake while a process the user cares about is running.
/// <para>
/// The subtle case is a command-line rule against a process whose command line cannot be read. The process
/// might be the long build the user is protecting, and there is no way to tell. It is therefore treated as a
/// match: a machine kept awake unnecessarily costs some electricity, and a machine that sleeps during a
/// four-hour build costs the build.
/// </para>
/// </summary>
public sealed class ProtectedProcessEvaluator
{
    public const string SourceId = "processes";

    private readonly ProtectedProcessOptions _options;

    public ProtectedProcessEvaluator(ProtectedProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public InhibitorSourceReport Evaluate(ProcessSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_options.Names.Count == 0 && _options.CommandLinePatterns.Count == 0)
        {
            // Nothing configured, so there is genuinely nothing here to hold for.
            return InhibitorSourceReport.ConfirmedAbsent(SourceId);
        }

        if (!snapshot.Succeeded)
        {
            return InhibitorSourceReport.Indeterminate(
                SourceId, $"The running processes could not be listed: {snapshot.Detail}");
        }

        var inhibitors = new List<Inhibitor>();
        var unreadable = new List<string>();

        foreach (var process in snapshot.Processes)
        {
            if (_options.Names.Any(name => string.Equals(name, process.Name, StringComparison.OrdinalIgnoreCase)))
            {
                inhibitors.Add(new Inhibitor(
                    InhibitorKind.ProtectedProcess,
                    $"'{process.Name}' is running",
                    nowUtc,
                    $"{process.Name}:{process.ProcessId}"));
                continue;
            }

            if (_options.CommandLinePatterns.Count == 0)
            {
                continue;
            }

            if (process.CommandLine is not { } commandLine)
            {
                // Collected rather than turned into an inhibitor each. A service reading other accounts'
                // processes cannot read most command lines, so this is the ordinary case: one inhibitor per
                // process would mean hundreds of allocations and a full sort every cycle, and a status output
                // in which the reason someone is actually looking for is buried.
                unreadable.Add($"{process.Name}:{process.ProcessId}");
                continue;
            }

            var matched = _options.CommandLinePatterns.FirstOrDefault(
                pattern => commandLine.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
            {
                inhibitors.Add(new Inhibitor(
                    InhibitorKind.ProtectedProcess,
                    $"'{process.Name}' matches '{matched}'",
                    nowUtc,
                    $"{process.Name}:{process.ProcessId}"));
            }
        }

        if (unreadable.Count > 0)
        {
            var listed = string.Join(", ", unreadable.Take(3));
            var more = unreadable.Count > 3 ? $" and {unreadable.Count - 3} more" : string.Empty;

            inhibitors.Add(new Inhibitor(
                InhibitorKind.ProtectedProcess,
                $"The command line of {unreadable.Count} process(es) could not be read, so they are treated " +
                "as matches",
                nowUtc,
                listed + more));
        }

        return inhibitors.Count == 0
            ? InhibitorSourceReport.ConfirmedAbsent(SourceId)
            : InhibitorSourceReport.Observed(SourceId, [.. inhibitors]);
    }
}
