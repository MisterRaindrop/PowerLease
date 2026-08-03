using PowerLease.Cli;
using PowerLease.Ipc.Contracts;
using Xunit;

namespace PowerLease.Cli.Tests;

/// <summary>A client the test scripts, including one that cannot reach the service.</summary>
internal sealed class FakeClient : IPowerLeaseClient
{
    public StatusResponse Status { get; set; } = new()
    {
        Revision = 1,
        ProtectionState = ReportedProtectionState.Released,
        ShouldHold = false
    };

    public ListLeasesResponse Leases { get; set; } = new();

    public CommandResponse Command { get; set; } = new("Created", "lease-1");

    public WakeStatusResponse Wake { get; set; } = new();

    public bool Unavailable { get; set; }

    public TimeSpan? RequestedDuration { get; private set; }

    public string? RequestedLeaseId { get; private set; }

    public Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken) => Answer(Status);

    public Task<ListLeasesResponse> ListLeasesAsync(CancellationToken cancellationToken) => Answer(Leases);

    public Task<CommandResponse> CreateLeaseAsync(TimeSpan duration, string? reason, CancellationToken cancellationToken)
    {
        RequestedDuration = duration;
        return Answer(Command);
    }

    public Task<CommandResponse> ReleaseLeaseAsync(string? leaseId, CancellationToken cancellationToken)
    {
        RequestedLeaseId = leaseId;
        return Answer(Command);
    }

    public Task<WakeStatusResponse> GetWakeStatusAsync(CancellationToken cancellationToken) => Answer(Wake);

    private Task<T> Answer<T>(T value) => Unavailable
        ? throw new ServiceUnavailableException("the pipe is not there")
        : Task.FromResult(value);
}

/// <summary>
/// What the commands print and what they return to the shell. The exit code is the part with judgement in it: a
/// script has to be able to tell a machine that is unprotected from one that could not be asked.
/// </summary>
public sealed class CommandsTests
{
    private static (int Code, string Output) Run(Func<TextWriter, Task<int>> command)
    {
        using var writer = new StringWriter();
        var code = command(writer).GetAwaiter().GetResult();
        return (code, writer.ToString());
    }

    [Fact]
    public void Status_on_a_machine_being_held_succeeds()
    {
        var client = new FakeClient
        {
            Status = new StatusResponse
            {
                Revision = 7,
                ProtectionState = ReportedProtectionState.Protected,
                ShouldHold = true,
                Inhibitors = [new ReportedInhibitor("SshSession", "SSH session", DateTimeOffset.UnixEpoch, "10.0.0.5")]
            }
        };

        var (code, output) = Run(w => Commands.StatusAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("SSH session", output, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_exits_non_zero_when_the_system_is_refusing_to_honour_the_request()
    {
        // The one condition the product exists to prevent. Reporting it as success would hide it behind a green
        // tick, and a script watching a fleet would never see it.
        var client = new FakeClient
        {
            Status = new StatusResponse
            {
                Revision = 7,
                ProtectionState = ReportedProtectionState.Unprotected,
                ShouldHold = true
            }
        };

        var (code, output) = Run(w => Commands.StatusAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.ProtectionFailing, code);
        Assert.Contains("can still go to sleep", output, StringComparison.Ordinal);
        Assert.Contains("power plan", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_shows_the_boundary_of_what_protection_covers()
    {
        // Not just the answer. A rule the user believes is on but nobody is evaluating, and a source that cannot
        // be believed, both change what the answer is worth.
        var client = new FakeClient
        {
            Status = new StatusResponse
            {
                Revision = 7,
                ProtectionState = ReportedProtectionState.Released,
                ShouldHold = false,
                UncoveredKinds = ["LockFile", "ScheduleWindow"],
                Sources = [new ReportedSource("ssh", Trusted: false, "the last report is too old to act on")],
                UnhealthySources = ["activity"],
                Faults = ["config.json failed validation"],
                HistoryWriteFailures = 3
            }
        };

        var (_, output) = Run(w => Commands.StatusAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Contains("Not being watched at all: LockFile, ScheduleWindow", output, StringComparison.Ordinal);
        Assert.Contains("too old to act on", output, StringComparison.Ordinal);
        Assert.Contains("stopped reporting: activity", output, StringComparison.Ordinal);
        Assert.Contains("config.json failed validation", output, StringComparison.Ordinal);
        Assert.Contains("does not affect", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_as_json_still_carries_the_exit_code()
    {
        var client = new FakeClient
        {
            Status = new StatusResponse
            {
                Revision = 7,
                ProtectionState = ReportedProtectionState.Unprotected,
                ShouldHold = true
            }
        };

        var (code, output) = Run(w => Commands.StatusAsync(client, w, asJson: true, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.ProtectionFailing, code);
        Assert.Contains("\"protectionState\"", output, StringComparison.Ordinal);
        Assert.Contains("\"isProtectionFailing\": true", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_that_cannot_be_reached_is_its_own_exit_code()
    {
        // Distinct from the machine being unprotected: a script has to tell "nobody answered" from "the answer is
        // bad news".
        var client = new FakeClient { Unavailable = true };

        var (code, output) = Run(w => Commands.StatusAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.ServiceUnavailable, code);
        Assert.Contains("could not be reached", output, StringComparison.Ordinal);
        Assert.Contains("Get-Service PowerLease", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Holding_passes_the_duration_through_and_reports_the_new_hold()
    {
        var client = new FakeClient();

        var (code, output) = Run(w => Commands.HoldAsync(
            client, w, TimeSpan.FromHours(3), "long build", asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.Success, code);
        Assert.Equal(TimeSpan.FromHours(3), client.RequestedDuration);
        Assert.Contains("lease-1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Holding_for_no_time_is_a_usage_error_and_never_reaches_the_service()
    {
        var client = new FakeClient();

        var (code, output) = Run(w => Commands.HoldAsync(
            client, w, TimeSpan.Zero, null, asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Null(client.RequestedDuration);
        Assert.Contains("positive duration", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_request_is_its_own_exit_code()
    {
        var client = new FakeClient { Command = new CommandResponse("Rejected", null, "that hold belongs to someone else") };

        var (code, output) = Run(w => Commands.ReleaseAsync(
            client, w, "lease-9", asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.RequestRefused, code);
        Assert.Contains("belongs to someone else", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_says_so_plainly_when_there_is_nothing_to_list()
    {
        var (code, output) = Run(w => Commands.ListAsync(
            new FakeClient(), w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("No holds.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_shows_each_hold_with_the_time_it_has_left()
    {
        var client = new FakeClient
        {
            Leases = new ListLeasesResponse
            {
                Leases =
                [
                    new ReportedLease("lease-1", "SshSession", "SSH from 10.0.0.5", "liu",
                        DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(179), "Active")
                ]
            }
        };

        var (_, output) = Run(w => Commands.ListAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Contains("lease-1", output, StringComparison.Ordinal);
        Assert.Contains("2h59m left", output, StringComparison.Ordinal);
        Assert.Contains("liu", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Wake_status_distinguishes_no_from_unknown()
    {
        // A blank must never read as a no: "the power plan forbids this" and "nobody could find out" call for
        // different actions.
        var client = new FakeClient
        {
            Wake = new WakeStatusResponse
            {
                SystemRequiredHonouredOnMains = true,
                SystemRequiredHonouredOnBattery = false,
                ModernStandby = null,
                Unavailable = ["the power plan could not be read on battery"]
            }
        };

        var (code, output) = Run(w => Commands.WakeStatusAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("on mains:   yes", output, StringComparison.Ordinal);
        Assert.Contains("on battery: no", output, StringComparison.Ordinal);
        Assert.Contains("Modern standby:                 unknown", output, StringComparison.Ordinal);
        Assert.Contains("Could not be determined:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_reports_an_emergency_hold_and_a_settling_period()
    {
        // Both explain a hold the user would otherwise have no account of: nothing they configured is asking for
        // it, so without a line saying why, the machine simply refuses to sleep for no visible reason.
        var client = new FakeClient
        {
            Status = new StatusResponse
            {
                Revision = 7,
                ProtectionState = ReportedProtectionState.Protected,
                ShouldHold = true,
                EmergencyInhibitRaised = true,
                EmergencyInhibitReason = "a source saw something alarming",
                GracePeriodActive = true
            }
        };

        var (_, output) = Run(w => Commands.StatusAsync(client, w, asJson: false, TestContext.Current.CancellationToken));

        Assert.Contains("Emergency hold: a source saw something alarming", output, StringComparison.Ordinal);
        Assert.Contains("after a start or a resume", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_as_json_is_machine_readable_and_still_exits_non_zero()
    {
        var client = new FakeClient { Command = new CommandResponse("Rejected", null, "no such hold") };

        var (code, output) = Run(w => Commands.ReleaseAsync(
            client, w, "lease-9", asJson: true, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.RequestRefused, code);
        Assert.Contains("\"error\": \"no such hold\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Wake_status_as_json_keeps_unknown_distinguishable_from_no()
    {
        var client = new FakeClient
        {
            Wake = new WakeStatusResponse
            {
                SystemRequiredHonouredOnMains = false,
                ModernStandby = null
            }
        };

        var (code, output) = Run(w => Commands.WakeStatusAsync(client, w, asJson: true, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("\"systemRequiredHonouredOnMains\": false", output, StringComparison.Ordinal);
        Assert.Contains("\"modernStandby\": null", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3h", 3, 0, 0)]
    [InlineData("90m", 1, 30, 0)]
    [InlineData("45s", 0, 0, 45)]
    [InlineData("1:30", 1, 30, 0)]
    [InlineData("0:00:30", 0, 0, 30)]
    [InlineData("1.5h", 1, 30, 0)]
    public void A_duration_can_be_written_the_way_a_person_writes_one(
        string text, int hours, int minutes, int seconds)
    {
        Assert.True(Commands.TryParseDuration(text, out var duration));
        Assert.Equal(new TimeSpan(hours, minutes, seconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("3x")]
    [InlineData("0h")]
    [InlineData("-2h")]
    [InlineData("1e308h")]
    [InlineData("1e308m")]
    [InlineData("1e308s")]
    [InlineData("Infinityh")]
    public void A_duration_that_is_not_one_is_refused(string? text)
    {
        Assert.False(Commands.TryParseDuration(text, out _));
    }

    [Fact]
    public void The_largest_whole_hour_that_fits_is_accepted_and_the_next_is_refused()
    {
        Assert.True(Commands.TryParseDuration("256204778h", out var duration));
        Assert.Equal(TimeSpan.FromHours(256204778), duration);

        Assert.False(Commands.TryParseDuration("256204779h", out _));
    }

    [Fact]
    public async Task The_arguments_are_validated()
    {
        using var writer = new StringWriter();
        var client = new FakeClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Commands.StatusAsync(null!, writer, false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Commands.StatusAsync(client, null!, false, TestContext.Current.CancellationToken));
    }
}
