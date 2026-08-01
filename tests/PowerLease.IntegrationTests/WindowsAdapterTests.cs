using System.Reflection;
using PowerLease.Application.Inhibitors;
using PowerLease.Application.Kernel;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Infrastructure.Windows.Sources;
using Xunit;

namespace PowerLease.IntegrationTests;

/// <summary>
/// What can be asserted about the Windows adapters without special hardware or privileges. None of these puts
/// the machine to sleep, and none of them needs a session to be connected.
/// </summary>
public sealed class WindowsAdapterTests
{
    [Fact]
    public void The_request_type_asked_for_is_the_one_that_keeps_the_machine_awake()
    {
        // POWER_REQUEST_TYPE does not start where one would guess: zero is PowerRequestDisplayRequired, and
        // PowerRequestSystemRequired is one. Getting it wrong is close to invisible -- a display request keeps
        // the system awake as a side effect, so the machine would still stay up while the product held the
        // wrong thing, lit the screen of a headless machine, and appeared under the wrong heading in
        // powercfg /requests. Nothing but this test would notice.
        var requestType = typeof(PowerRequestManager)
            .GetNestedType("PowerRequestType", BindingFlags.NonPublic);

        Assert.NotNull(requestType);
        Assert.Equal(0, (int)Enum.Parse(requestType, "DisplayRequired"));
        Assert.Equal(1, (int)Enum.Parse(requestType, "SystemRequired"));
        Assert.Equal(2, (int)Enum.Parse(requestType, "AwayModeRequired"));
        Assert.Equal(3, (int)Enum.Parse(requestType, "ExecutionRequired"));
    }

    [Fact]
    public void Letting_go_of_a_generation_that_was_never_held_does_nothing()
    {
        // The coordinator closes a generation whose acquisition outcome it could not establish, so this is a
        // path taken in ordinary operation rather than an edge case.
        var manager = new PowerRequestManager();

        manager.Close(generation: 42);
        manager.Close(generation: 42);
    }

    [Fact]
    public void Holding_the_machine_awake_and_letting_go_again_reports_what_happened()
    {
        // Runs for real on the CI runner: it only ever prevents sleep, so it cannot put the runner to sleep.
        var manager = new PowerRequestManager();

        var first = manager.Acquire(generation: 1);
        Assert.True(
            first is PowerInhibitResult.Held or PowerInhibitResult.Rejected or PowerInhibitResult.Uncertain,
            $"unexpected result {first}");

        // Whatever happened, letting go must be safe and must not throw.
        manager.Close(generation: 1);

        // And a fresh generation can be taken out afterwards.
        var second = manager.Acquire(generation: 2);
        manager.Close(generation: 2);
        Assert.True(second is PowerInhibitResult.Held or PowerInhibitResult.Rejected or PowerInhibitResult.Uncertain);
    }

    [Fact]
    public void The_power_configuration_is_read_as_facts_or_admitted_gaps_never_as_a_silent_no()
    {
        // "The power plan forbids this" and "nobody could find out" call for different actions from the user,
        // so a fact that could not be determined must stay null and say which call failed.
        IPowerCapabilityProbe probe = new PowerCapabilityProbe();

        var snapshot = probe.Read();

        var facts = new[]
        {
            snapshot.SystemRequiredHonouredOnMains,
            snapshot.SystemRequiredHonouredOnBattery,
            snapshot.ModernStandby,
            snapshot.RunningOnBattery
        };

        // Every fact is either known or listed as unavailable; a null with nothing said about it would reach the
        // user as a blank that reads like a no.
        Assert.Equal(facts.Count(fact => fact is null) > 0, snapshot.Unavailable.Count > 0);
        Assert.All(snapshot.Unavailable, detail => Assert.False(string.IsNullOrWhiteSpace(detail)));
    }

    [Fact]
    public void A_lock_file_that_exists_is_reported_present_and_one_that_does_not_is_reported_absent()
    {
        var probe = new WindowsLockFileProbe();
        var path = Path.Combine(Path.GetTempPath(), $"powerlease-{Guid.NewGuid():n}.lock");

        var absent = probe.Check(path);
        Assert.True(absent.Succeeded);
        Assert.False(absent.Exists);

        File.WriteAllText(path, string.Empty);
        try
        {
            var present = probe.Check(path);
            Assert.True(present.Succeeded);
            Assert.True(present.Exists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_metric_that_needs_two_readings_is_null_the_first_time_rather_than_zero()
    {
        // Zero would read as the quietest machine imaginable and release protection. A rate cannot be known
        // from one reading, so the honest answer is that it is not known yet.
        using var provider = new SystemMetricProvider();

        var first = provider.Read();

        Assert.Null(first.CpuPercent);
        Assert.Null(first.NetworkBytesPerSecond);
    }

    [Fact]
    public void The_connection_table_is_read_as_a_success_or_as_an_admitted_failure_never_as_emptiness()
    {
        // An empty successful snapshot is a positive claim that nobody is connected, which releases
        // protection. A failed read must not be able to masquerade as one.
        var snapshot = new ExtendedTcpTableProvider().GetEstablishedConnections();

        if (snapshot.Succeeded)
        {
            Assert.All(snapshot.Connections, connection =>
            {
                Assert.InRange(connection.LocalPort, 0, 65535);
                Assert.InRange(connection.RemotePort, 0, 65535);
                Assert.False(string.IsNullOrWhiteSpace(connection.RemoteAddress));

                // An IPv4-mapped address must have been unwrapped, or the same connection would be tracked
                // under two different keys depending on which family enumerated it.
                Assert.DoesNotContain("::ffff:", connection.RemoteAddress, StringComparison.OrdinalIgnoreCase);
            });
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Detail));
        }
    }

    [Fact]
    public void The_running_processes_are_listed_and_an_unreadable_command_line_is_null_not_blank()
    {
        // A service cannot read most other accounts' command lines. That is expected, and the evaluator treats
        // null as a match; an empty string would silently match nothing.
        var snapshot = new ProcessSnapshotProvider().GetProcesses();

        Assert.True(snapshot.Succeeded, snapshot.Detail);
        Assert.NotEmpty(snapshot.Processes);
        Assert.All(snapshot.Processes, process =>
        {
            Assert.False(string.IsNullOrWhiteSpace(process.Name));
            Assert.True(process.CommandLine is null || process.CommandLine.Length > 0);
        });
    }

    [Fact]
    public void Reading_the_ssh_log_reports_a_state_and_offers_a_position_only_when_it_can_be_trusted()
    {
        // The OpenSSH channel may or may not exist on this machine; both are first-class answers. What must
        // hold either way is that a position is only handed back from a read that was actually continuous,
        // because storing one from a failed read loses the logins it skipped.
        var read = new OpenSshEventProvider().Read(bookmark: null);

        if (read.State == SshLogChannelState.Available)
        {
            Assert.All(read.Events, entry => Assert.False(string.IsNullOrWhiteSpace(entry.RecordId)));
        }
        else
        {
            Assert.Empty(read.Events);
            Assert.Null(read.Bookmark);
            Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        }
    }
}
