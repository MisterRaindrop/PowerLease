using PowerLease.Application.Inhibitors;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// SSH detection. The scenario behind all of it: someone is logged in to a remote development machine and it
/// must not go to sleep underneath them, including while they are still typing their password and after their
/// laptop lid closes.
/// </summary>
public sealed class SshSessionCorrelatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Epoch = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

    private static MonotonicStamp At(double seconds, Guid? epoch = null) =>
        new(epoch ?? Epoch, TimeSpan.FromSeconds(seconds));

    private static TcpConnection Connection(string address = "10.0.0.5", int localPort = 22, int remotePort = 51000) =>
        new(address, remotePort, localPort, OwningProcessId: 4242);

    private static SshSessionCorrelator Correlator(bool tcpOnlyConfirmed = false) =>
        new(new SshDetectionOptions
        {
            Ports = [22],
            MinimumConnectionAge = TimeSpan.FromSeconds(15),
            HoldDuration = TimeSpan.FromHours(3),
            TcpOnlyConfirmed = tcpOnlyConfirmed
        });

    private static SshLogRead Quiet() => SshLogRead.Available([], "offset-1");

    [Fact]
    public void A_connection_holds_the_machine_awake_from_the_moment_it_appears()
    {
        // A connection younger than the minimum age is one that is still authenticating. Ignoring it would
        // drop protection at exactly the wrong moment: the machine has just been woken up so somebody can log
        // in to it.
        var correlator = Correlator();

        var result = correlator.Evaluate(TcpSnapshot.Of(Connection()), Quiet(), At(0), Noon);

        Assert.True(result.Report.IsDeterminate);
        var inhibitor = Assert.Single(result.Report.Inhibitors);
        Assert.Equal(InhibitorKind.SshSession, inhibitor.Kind);
        Assert.Contains("10.0.0.5", inhibitor.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_connected_and_a_readable_log_means_nothing_to_hold_for()
    {
        // The control: release is reachable, so the tests below showing a hold mean something.
        var result = Correlator().Evaluate(TcpSnapshot.Of(), Quiet(), At(0), Noon);

        Assert.True(result.Report.IsDeterminate);
        Assert.Empty(result.Report.Inhibitors);
    }

    [Fact]
    public void A_login_in_the_log_asks_for_a_hold_that_outlives_the_connection()
    {
        // Why the log matters at all. Without it, closing a laptop lid would end protection immediately and
        // put the build machine to sleep mid-compile.
        var correlator = Correlator();
        var log = SshLogRead.Available(
            [new SshAuthEvent("4242", SshAuthEventKind.Authenticated, Noon, "liu", "10.0.0.5", 51000)],
            "offset-2");

        var result = correlator.Evaluate(TcpSnapshot.Of(Connection()), log, At(0), Noon);

        var hold = Assert.Single(result.HoldRequests);
        Assert.Equal(TimeSpan.FromHours(3), hold.Duration);
        Assert.Equal("liu", hold.UserName);
        Assert.Equal("10.0.0.5", hold.RemoteAddress);
        Assert.Equal("4242", hold.LogRecordId);
        Assert.Equal("offset-2", result.Bookmark);

        // The reason is what `powerlease list` shows, so it has to name who logged in from where.
        Assert.Contains("liu", hold.Reason, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5", hold.Reason, StringComparison.Ordinal);

        // The log entry's own timestamp is carried through for the audit trail; it is not the monotonic
        // measurement the hold's remaining time is computed from.
        Assert.Equal(Noon, Assert.Single(log.Events).OccurredAtUtc);
    }

    [Fact]
    public void Reading_the_log_again_cannot_ask_for_a_second_hold_for_one_login()
    {
        // The key is derived from the log entry, so a replay produces the same request rather than a new one.
        // Together with the record of what has been processed, that is what stops one login creating two holds.
        var correlator = Correlator();
        var login = new SshAuthEvent("4242", SshAuthEventKind.Authenticated, Noon, "liu", "10.0.0.5");

        var first = correlator.Evaluate(TcpSnapshot.Of(Connection()), SshLogRead.Available([login], "a"), At(0), Noon);
        var replay = correlator.Evaluate(TcpSnapshot.Of(Connection()), SshLogRead.Available([login], "a"), At(1), Noon);

        Assert.Equal(Assert.Single(first.HoldRequests).Key, Assert.Single(replay.HoldRequests).Key);
        Assert.Equal("ssh-log:4242", Assert.Single(replay.HoldRequests).Key);
    }

    [Fact]
    public void A_replayed_disconnect_cannot_take_protection_away()
    {
        // Protection comes from what is connected now, never from the log, so an old disconnect being read
        // again has nothing to undo. This is the whole reason the two sources are used for different things.
        var correlator = Correlator();
        var log = SshLogRead.Available(
            [new SshAuthEvent("old-1", SshAuthEventKind.Disconnected, Noon.AddHours(-2), "liu", "10.0.0.5")],
            "offset-3");

        var result = correlator.Evaluate(TcpSnapshot.Of(Connection()), log, At(0), Noon);

        Assert.NotEmpty(result.Report.Inhibitors);
        Assert.Empty(result.HoldRequests);
    }

    [Fact]
    public void No_position_is_offered_when_the_log_could_not_be_read()
    {
        // The host stores the position together with the holds it implies. Offering one from a failed read
        // would invite it to be stored, and the logins that were skipped would never be seen again -- each one
        // losing the three-hour hold it should have created.
        var correlator = Correlator();

        var failed = correlator.Evaluate(
            TcpSnapshot.Of(),
            SshLogRead.Unavailable(SshLogChannelState.TransientFailure, "the log service is not responding"),
            At(0),
            Noon);

        Assert.Null(failed.Bookmark);
        Assert.False(failed.Report.IsDeterminate);
    }

    [Fact]
    public void A_log_that_is_no_longer_continuous_holds_the_machine_awake()
    {
        // It was cleared or rolled over, so logins between the recorded position and now were never seen.
        var result = Correlator().Evaluate(
            TcpSnapshot.Of(),
            SshLogRead.Unavailable(SshLogChannelState.Discontinuous, "the recorded position no longer exists"),
            At(0),
            Noon);

        Assert.False(result.Report.IsDeterminate);
        Assert.Contains("missed", result.Report.IndeterminateReason!, StringComparison.Ordinal);
        Assert.Null(result.Bookmark);
    }

    [Fact]
    public void No_log_at_all_holds_the_machine_awake_until_the_user_says_otherwise()
    {
        // Without the log, "nobody is connected" and "cannot see who is connected" look identical, and only
        // the user can decide to accept connection state alone.
        var guarded = Correlator().Evaluate(
            TcpSnapshot.Of(),
            SshLogRead.Unavailable(SshLogChannelState.NotInstalled, "no OpenSSH log"),
            At(0),
            Noon);

        Assert.False(guarded.Report.IsDeterminate);
        Assert.Contains("tcpOnlyConfirmed", guarded.Report.IndeterminateReason!, StringComparison.Ordinal);

        var accepted = Correlator(tcpOnlyConfirmed: true).Evaluate(
            TcpSnapshot.Of(),
            SshLogRead.Unavailable(SshLogChannelState.NotInstalled, "no OpenSSH log"),
            At(0),
            Noon);

        Assert.True(accepted.Report.IsDeterminate);
        Assert.Empty(accepted.Report.Inhibitors);
    }

    [Fact]
    public void A_connection_table_that_could_not_be_read_is_never_taken_as_nobody_connected()
    {
        var result = Correlator().Evaluate(
            TcpSnapshot.Unavailable("the enumeration failed"),
            Quiet(),
            At(0),
            Noon);

        Assert.False(result.Report.IsDeterminate);
        Assert.Contains("connection table", result.Report.IndeterminateReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_to_some_other_port_is_not_an_SSH_session()
    {
        var result = Correlator().Evaluate(
            TcpSnapshot.Of(Connection(localPort: 3389)),
            Quiet(),
            At(0),
            Noon);

        Assert.True(result.Report.IsDeterminate);
        Assert.Empty(result.Report.Inhibitors);
    }

    [Fact]
    public void Without_a_log_a_connection_earns_a_hold_once_it_has_been_up_long_enough()
    {
        // With no log entry to key a hold to, the connection itself becomes the cause -- but only once it has
        // lasted long enough to be a session rather than a port scan.
        var correlator = Correlator(tcpOnlyConfirmed: true);
        var noLog = SshLogRead.Unavailable(SshLogChannelState.NotInstalled, "no OpenSSH log");

        Assert.Empty(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(0), Noon).HoldRequests);
        Assert.Empty(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(14), Noon).HoldRequests);

        var earned = correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(15), Noon);
        var hold = Assert.Single(earned.HoldRequests);
        Assert.Equal("ssh-tcp:22<-10.0.0.5:51000", hold.Key);
        Assert.Equal(TimeSpan.FromHours(3), hold.Duration);
    }

    [Fact]
    public void With_a_readable_log_the_connection_never_becomes_the_cause_of_a_hold()
    {
        // The log is authoritative when it is there. Letting the connection also create one would produce two
        // holds for a single login.
        var correlator = Correlator();

        for (var second = 0; second <= 60; second += 5)
        {
            Assert.Empty(correlator.Evaluate(TcpSnapshot.Of(Connection()), Quiet(), At(second), Noon).HoldRequests);
        }
    }

    [Fact]
    public void A_failed_connection_read_does_not_restart_how_long_a_connection_has_been_up()
    {
        // Forgetting the ages on every failed read would keep pushing the hold out of reach.
        var correlator = Correlator(tcpOnlyConfirmed: true);
        var noLog = SshLogRead.Unavailable(SshLogChannelState.NotInstalled, "no OpenSSH log");
        correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(0), Noon);

        correlator.Evaluate(TcpSnapshot.Unavailable("the enumeration failed"), noLog, At(5), Noon);

        Assert.Single(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(15), Noon).HoldRequests);
    }

    [Fact]
    public void A_change_of_clock_forgets_how_long_connections_have_been_up()
    {
        // Ages measured against a different monotonic origin cannot be compared with the current one, and a
        // comparison across them would produce a plausible number that is wrong.
        var correlator = Correlator(tcpOnlyConfirmed: true);
        var noLog = SshLogRead.Unavailable(SshLogChannelState.NotInstalled, "no OpenSSH log");
        correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(0), Noon);

        var other = Guid.Parse("ffffffff-6666-6666-6666-666666666666");
        Assert.Empty(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(600, other), Noon).HoldRequests);

        // And it starts again from there.
        Assert.Single(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(615, other), Noon).HoldRequests);
    }

    [Fact]
    public void A_connection_that_has_gone_is_forgotten()
    {
        // Otherwise a machine that is connected to repeatedly would accumulate ages for four-tuples that will
        // never be seen again.
        var correlator = Correlator(tcpOnlyConfirmed: true);
        var noLog = SshLogRead.Unavailable(SshLogChannelState.NotInstalled, "no OpenSSH log");
        correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(0), Noon);

        correlator.Evaluate(TcpSnapshot.Of(), noLog, At(5), Noon);

        // Reconnecting from the same address and port starts the clock again, because as far as anything can
        // tell this is a new session.
        Assert.Empty(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(10), Noon).HoldRequests);
        Assert.Single(correlator.Evaluate(TcpSnapshot.Of(Connection()), noLog, At(25), Noon).HoldRequests);
    }

    [Fact]
    public void Several_connections_are_each_a_reason_to_stay_awake()
    {
        var result = Correlator().Evaluate(
            TcpSnapshot.Of(Connection("10.0.0.5"), Connection("10.0.0.6")),
            Quiet(),
            At(0),
            Noon);

        Assert.Equal(2, result.Report.Inhibitors.Count);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        var correlator = Correlator();

        Assert.Throws<ArgumentNullException>(() => new SshSessionCorrelator(null!));
        Assert.Throws<ArgumentNullException>(() => correlator.Evaluate(null!, Quiet(), At(0), Noon));
        Assert.Throws<ArgumentNullException>(() => correlator.Evaluate(TcpSnapshot.Of(), null!, At(0), Noon));
    }
}
