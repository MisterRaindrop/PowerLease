using System.Globalization;
using System.Text.Json;
using PowerLease.Ipc.Contracts;

namespace PowerLease.Cli;

/// <summary>
/// What each command prints and what it returns to the shell.
/// <para>
/// Separated from argument parsing so that the part with judgement in it -- what counts as a failure, what the
/// user is told when the answer is "protected, but only partly" -- is testable without a service, a pipe or a
/// Windows machine.
/// </para>
/// </summary>
public static class Commands
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> StatusAsync(
        IPowerLeaseClient client,
        TextWriter output,
        bool asJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        return await RunAsync(output, async () =>
        {
            var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);

            if (asJson)
            {
                output.WriteLine(JsonSerializer.Serialize(status, Json));
            }
            else
            {
                WriteStatus(output, status);
            }

            // Not an error in the command. It ran, and the answer is that the machine may sleep while somebody is
            // using it, which a script has to be able to notice.
            return status.IsProtectionFailing ? ExitCode.ProtectionFailing : ExitCode.Success;
        }).ConfigureAwait(false);
    }

    public static async Task<int> ListAsync(
        IPowerLeaseClient client,
        TextWriter output,
        bool asJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        return await RunAsync(output, async () =>
        {
            var leases = await client.ListLeasesAsync(cancellationToken).ConfigureAwait(false);

            if (asJson)
            {
                output.WriteLine(JsonSerializer.Serialize(leases, Json));
                return ExitCode.Success;
            }

            if (leases.Leases.Count == 0)
            {
                output.WriteLine("No holds.");
                return ExitCode.Success;
            }

            foreach (var lease in leases.Leases)
            {
                output.WriteLine(
                    $"{lease.Id}  {lease.Source}  {Format(lease.Remaining)} left  " +
                    $"{lease.OwnerUser ?? "-"}  {lease.Reason ?? string.Empty}".TrimEnd());
            }

            return ExitCode.Success;
        }).ConfigureAwait(false);
    }

    public static async Task<int> HoldAsync(
        IPowerLeaseClient client,
        TextWriter output,
        TimeSpan duration,
        string? reason,
        bool asJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        if (duration <= TimeSpan.Zero)
        {
            output.WriteLine("A hold needs a positive duration, for example 3h or 90m.");
            return ExitCode.UsageError;
        }

        return await RunAsync(output, async () =>
        {
            var result = await client.CreateLeaseAsync(duration, reason, cancellationToken).ConfigureAwait(false);
            return Report(output, result, asJson, $"Holding for {Format(duration)}.");
        }).ConfigureAwait(false);
    }

    public static async Task<int> ReleaseAsync(
        IPowerLeaseClient client,
        TextWriter output,
        string? leaseId,
        bool asJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        return await RunAsync(output, async () =>
        {
            var result = await client.ReleaseLeaseAsync(leaseId, cancellationToken).ConfigureAwait(false);
            return Report(output, result, asJson, "Released.");
        }).ConfigureAwait(false);
    }

    public static async Task<int> WakeStatusAsync(
        IPowerLeaseClient client,
        TextWriter output,
        bool asJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        return await RunAsync(output, async () =>
        {
            var wake = await client.GetWakeStatusAsync(cancellationToken).ConfigureAwait(false);

            if (asJson)
            {
                output.WriteLine(JsonSerializer.Serialize(wake, Json));
                return ExitCode.Success;
            }

            output.WriteLine($"Keep-awake honoured on mains:   {Describe(wake.SystemRequiredHonouredOnMains)}");
            output.WriteLine($"Keep-awake honoured on battery: {Describe(wake.SystemRequiredHonouredOnBattery)}");
            output.WriteLine($"Modern standby:                 {Describe(wake.ModernStandby)}");
            output.WriteLine($"Running on battery:             {Describe(wake.RunningOnBattery)}");
            output.WriteLine($"Wake timers allowed:            {Describe(wake.WakeTimersAllowed)}");

            foreach (var missing in wake.Unavailable)
            {
                output.WriteLine($"Could not be determined: {missing}");
            }

            return ExitCode.Success;
        }).ConfigureAwait(false);
    }

    private static void WriteStatus(TextWriter output, StatusResponse status)
    {
        output.WriteLine($"State: {status.ProtectionState}");

        if (status.IsProtectionFailing)
        {
            // Said first and said plainly. The machine can sleep while it is in use, and the cause is almost
            // always one setting the user can change.
            output.WriteLine(
                "The system is not honouring the keep-awake request, so this machine can still go to sleep " +
                "while it is in use. Check that the active power plan allows a program to keep the computer awake.");
        }

        if (status.Inhibitors.Count == 0)
        {
            output.WriteLine("Nothing is keeping this machine awake.");
        }
        else
        {
            output.WriteLine("Keeping this machine awake:");
            foreach (var inhibitor in status.Inhibitors)
            {
                var detail = inhibitor.Detail is null ? string.Empty : $" ({inhibitor.Detail})";
                output.WriteLine($"  {inhibitor.Kind}: {inhibitor.Reason}{detail}");
            }
        }

        // The boundary, not just the answer.
        if (status.UncoveredKinds.Count > 0)
        {
            output.WriteLine($"Not being watched at all: {string.Join(", ", status.UncoveredKinds)}");
        }

        var distrusted = status.Sources.Where(source => !source.Trusted).ToArray();
        if (distrusted.Length > 0)
        {
            output.WriteLine("Sources that cannot currently be believed:");
            foreach (var source in distrusted)
            {
                output.WriteLine($"  {source.SourceId}: {source.Detail ?? "no reason given"}");
            }
        }

        if (status.UnhealthySources.Count > 0)
        {
            output.WriteLine($"Sources that have stopped reporting: {string.Join(", ", status.UnhealthySources)}");
        }

        foreach (var fault in status.Faults)
        {
            output.WriteLine($"Fault: {fault}");
        }

        if (status.EmergencyInhibitRaised)
        {
            output.WriteLine($"Emergency hold: {status.EmergencyInhibitReason ?? "raised"}");
        }

        if (status.GracePeriodActive)
        {
            output.WriteLine("Holding unconditionally while the picture settles after a start or a resume.");
        }

        if (status.HistoryWriteFailures > 0)
        {
            output.WriteLine(
                $"History could not be written {status.HistoryWriteFailures} time(s). This does not affect " +
                "whether the machine is kept awake.");
        }
    }

    private static int Report(TextWriter output, CommandResponse result, bool asJson, string success)
    {
        if (asJson)
        {
            output.WriteLine(JsonSerializer.Serialize(result, Json));
        }

        var accepted = result.Error is null;

        if (!asJson)
        {
            output.WriteLine(accepted
                ? result.LeaseId is null ? success : $"{success} ({result.LeaseId})"
                : $"Refused: {result.Error}");
        }

        return accepted ? ExitCode.Success : ExitCode.RequestRefused;
    }

    /// <summary>
    /// Turns not being able to reach the service into its own exit code, so a script can tell "the machine is
    /// unprotected" from "nobody answered".
    /// </summary>
    private static async Task<int> RunAsync(TextWriter output, Func<Task<int>> command)
    {
        try
        {
            return await command().ConfigureAwait(false);
        }
        catch (ServiceUnavailableException error)
        {
            output.WriteLine($"The PowerLease service could not be reached: {error.Message}");
            output.WriteLine("Check that it is running: Get-Service PowerLease");
            return ExitCode.ServiceUnavailable;
        }
    }

    private static string Describe(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        _ => "unknown"
    };

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}h{value.Minutes:00}m"
        : $"{(int)value.TotalMinutes}m";

    /// <summary>
    /// Read a duration written the way a person writes one: <c>3h</c>, <c>90m</c>, <c>45s</c>, or <c>1:30</c>.
    /// </summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var suffix = text[^1];
        var number = text[..^1];

        if (char.IsAsciiLetter(suffix)
            && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            && value > 0)
        {
            try
            {
                duration = char.ToLowerInvariant(suffix) switch
                {
                    'h' => TimeSpan.FromHours(value),
                    'm' => TimeSpan.FromMinutes(value),
                    's' => TimeSpan.FromSeconds(value),
                    _ => TimeSpan.Zero
                };
            }
            catch (OverflowException)
            {
                // A syntactically valid number can still be too large for TimeSpan. It is a usage error, not a
                // reason for the CLI to terminate with a stack trace.
                return false;
            }

            return duration > TimeSpan.Zero;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration) && duration > TimeSpan.Zero;
    }
}
