using PowerLease.Cli;
using Xunit;

namespace PowerLease.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void No_arguments_prints_the_version_banner_and_succeeds()
    {
        var (exitCode, output) = RunMain([]);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("PowerLease CLI ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_flag_prints_the_version_banner_and_succeeds()
    {
        var (exitCode, output) = RunMain(["--version"]);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("PowerLease CLI ", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("--help")]
    [InlineData("-v")]
    public void Unsupported_arguments_exit_with_code_two(string argument)
    {
        var (exitCode, output) = RunMain([argument]);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unsupported arguments", output, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_one_argument_exits_with_code_two()
    {
        var (exitCode, _) = RunMain(["--version", "extra"]);

        Assert.Equal(2, exitCode);
    }

    /// <summary>
    /// Redirects <see cref="Console.Out" /> for the duration of the call so the banner can be
    /// asserted, then restores the original writer even if Main throws.
    /// </summary>
    private static (int ExitCode, string Output) RunMain(string[] args)
    {
        var original = Console.Out;
        using var captured = new StringWriter();

        try
        {
            Console.SetOut(captured);
            var exitCode = Program.Main(args);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
