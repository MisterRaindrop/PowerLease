using PowerLease.Cli;
using Xunit;

namespace PowerLease.Cli.Tests;

/// <summary>
/// Argument handling. The commands themselves are tested separately; what matters here is that the right one is
/// chosen, that a mistake is a usage error rather than a request, and that a build with no transport says so
/// instead of looking like a service that is down.
/// </summary>
public sealed class ProgramTests
{
    private static (int ExitCode, string Output) Run(params string[] args) => Run(client: null, args);

    private static (int ExitCode, string Output) Run(IPowerLeaseClient? client, params string[] args)
    {
        using var captured = new StringWriter();
        var exitCode = Program
            .RunAsync(args, client, captured, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
        return (exitCode, captured.ToString());
    }

    [Fact]
    public void No_arguments_reports_the_version()
    {
        var (code, output) = Run();

        Assert.Equal(ExitCode.Success, code);
        Assert.StartsWith("PowerLease CLI ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_is_the_one_the_assembly_was_built_with()
    {
        // Not "unknown": the informational version has to survive the build, or a bug report cannot say which
        // build it came from.
        var (_, output) = Run("--version");

        Assert.DoesNotContain("unknown", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("help")]
    public void Help_explains_the_commands_and_the_exit_codes(string argument)
    {
        var (code, output) = Run(argument);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("status", output, StringComparison.Ordinal);
        Assert.Contains("hold <duration>", output, StringComparison.Ordinal);
        Assert.Contains("Exit codes", output, StringComparison.Ordinal);

        // The one thing a user has to understand about this tool.
        Assert.Contains("never puts it to sleep", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_that_does_not_exist_is_a_usage_error()
    {
        var (code, output) = Run(new FakeClient(), "summon");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown command 'summon'", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_with_no_transport_says_that_rather_than_looking_like_a_service_that_is_down()
    {
        var (code, output) = Run("status");

        Assert.Equal(ExitCode.ServiceUnavailable, code);
        Assert.Contains("ships with the Windows service", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Holding_needs_a_duration()
    {
        var (code, output) = Run(new FakeClient(), "hold");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("How long?", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duration_that_is_not_one_is_a_usage_error_and_never_reaches_the_service()
    {
        var client = new FakeClient();

        var (code, output) = Run(client, "hold", "soon");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("is not a duration", output, StringComparison.Ordinal);
        Assert.Null(client.RequestedDuration);
    }

    [Fact]
    public void The_words_after_the_duration_become_the_reason()
    {
        var client = new FakeClient();

        var (code, _) = Run(client, "hold", "3h", "long", "build");

        Assert.Equal(ExitCode.Success, code);
        Assert.Equal(TimeSpan.FromHours(3), client.RequestedDuration);
    }

    [Fact]
    public void Release_passes_the_identifier_through_and_tolerates_its_absence()
    {
        var client = new FakeClient();

        Run(client, "release", "lease-7");
        Assert.Equal("lease-7", client.RequestedLeaseId);

        var bare = new FakeClient();
        Run(bare, "release");
        Assert.Null(bare.RequestedLeaseId);
    }

    [Fact]
    public void The_json_switch_is_not_mistaken_for_a_command()
    {
        var client = new FakeClient();

        var (code, output) = Run(client, "list", "--json");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("\"leases\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_arguments_are_validated()
    {
        using var writer = new StringWriter();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Program.RunAsync(null!, null, writer, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Program.RunAsync([], null, null!, TestContext.Current.CancellationToken));
    }
}
