using System.Reflection;

namespace PowerLease.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args, client: null, Console.Out, CancellationToken.None);

    /// <summary>
    /// Parse the arguments and run the command.
    /// <para>
    /// The writer and the client are arguments rather than things this reaches for, so a test can drive the whole
    /// command without a service, a named pipe, or swapping a process-wide static.
    /// </para>
    /// </summary>
    /// <param name="client">
    /// How to reach the service, or null when this build has no transport. The transport is a named pipe and
    /// ships with the Windows service; until then every command that needs it says so, rather than failing in a
    /// way that looks like the service being down.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args,
        IPowerLeaseClient? client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 0 || args.Contains("--version", StringComparer.Ordinal))
        {
            output.WriteLine($"PowerLease CLI {Version()}");
            return ExitCode.Success;
        }

        var asJson = args.Contains("--json", StringComparer.Ordinal);
        var positional = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var command = positional.Length > 0 ? positional[0] : string.Empty;

        if (args.Contains("--help", StringComparer.Ordinal) || command is "help")
        {
            WriteUsage(output);
            return ExitCode.Success;
        }

        if (client is null)
        {
            output.WriteLine(
                "This build cannot reach the service: the named-pipe transport ships with the Windows service.");
            return ExitCode.ServiceUnavailable;
        }

        switch (command)
        {
            case "status":
                return await Commands.StatusAsync(client, output, asJson, cancellationToken).ConfigureAwait(false);

            case "list":
                return await Commands.ListAsync(client, output, asJson, cancellationToken).ConfigureAwait(false);

            case "wake-status":
                return await Commands.WakeStatusAsync(client, output, asJson, cancellationToken).ConfigureAwait(false);

            case "hold":
                return await HoldAsync(client, output, positional, asJson, cancellationToken).ConfigureAwait(false);

            case "release":
                return await Commands.ReleaseAsync(
                    client,
                    output,
                    positional.Length > 1 ? positional[1] : null,
                    asJson,
                    cancellationToken).ConfigureAwait(false);

            default:
                output.WriteLine($"Unknown command '{command}'.");
                WriteUsage(output);
                return ExitCode.UsageError;
        }
    }

    private static async Task<int> HoldAsync(
        IPowerLeaseClient client,
        TextWriter output,
        string[] positional,
        bool asJson,
        CancellationToken cancellationToken)
    {
        if (positional.Length < 2)
        {
            output.WriteLine("How long? For example: powerlease hold 3h");
            return ExitCode.UsageError;
        }

        if (!Commands.TryParseDuration(positional[1], out var duration))
        {
            output.WriteLine($"'{positional[1]}' is not a duration. Try 3h, 90m or 45s.");
            return ExitCode.UsageError;
        }

        var reason = positional.Length > 2 ? string.Join(' ', positional[2..]) : null;
        return await Commands.HoldAsync(client, output, duration, reason, asJson, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void WriteUsage(TextWriter output)
    {
        output.WriteLine("powerlease <command> [--json]");
        output.WriteLine();
        output.WriteLine("  status                 what is keeping this machine awake, and what is not watched");
        output.WriteLine("  list                   the holds currently in place");
        output.WriteLine("  hold <duration> [why]  keep this machine awake, for example: hold 3h building");
        output.WriteLine("  release [id]           end a hold you created");
        output.WriteLine("  wake-status            what this machine's power configuration allows");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 success, 1 the system is not honouring the keep-awake request,");
        output.WriteLine("            2 usage, 3 the service could not be reached, 4 refused.");
        output.WriteLine();
        output.WriteLine("This tool only keeps the machine awake. It never puts it to sleep; Windows does that,");
        output.WriteLine("according to your own power plan.");
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
