using PowerLease.Domain;

namespace PowerLease.Application.Inhibitors;

/// <summary>Settings the SSH detection needs.</summary>
public sealed record SshDetectionOptions
{
    public IReadOnlyList<int> Ports { get; init; } = [22];

    /// <summary>
    /// How long a connection must have been up before it earns a long hold. It has no bearing on whether the
    /// connection counts as a reason to stay awake: it does so from the moment it appears.
    /// </summary>
    public TimeSpan MinimumConnectionAge { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a hold created by a login lasts, so a disconnect does not end protection at once.</summary>
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromHours(3);

    /// <summary>
    /// Whether the user has accepted that connection state alone may be used to conclude nobody is
    /// connected. Off by default: without the log, "nobody is connected" and "cannot tell" look identical.
    /// </summary>
    public bool TcpOnlyConfirmed { get; init; }
}

/// <summary>A hold that should be created because someone logged in.</summary>
/// <param name="Key">
/// Stable identity of the cause, so the same login cannot produce two holds. Derived from the log entry
/// where there is one, and from the connection otherwise.
/// </param>
public sealed record SshHoldRequest(
    string Key,
    TimeSpan Duration,
    string Reason,
    string? UserName = null,
    string? RemoteAddress = null,
    string? LogRecordId = null);

/// <summary>
/// What the SSH detection concluded this cycle, plus the durable work it implies.
/// </summary>
/// <param name="Bookmark">
/// How far the log was read. The host must store this in the same transaction as the holds below; storing
/// it alone would silently drop those logins.
/// </param>
public sealed record SshCorrelationResult(
    InhibitorSourceReport Report,
    IReadOnlyList<SshHoldRequest> HoldRequests,
    string? Bookmark,
    SshLogChannelState LogState);

/// <summary>
/// Works out whether anyone is connected over SSH.
/// <para>
/// Two sources of truth, used for different things. Connection state says who is connected <em>now</em>, and
/// that alone decides whether the machine is held awake. The authentication log says a login
/// <em>happened</em>, and that is what creates a hold long enough to survive the connection dropping -- so
/// that closing a laptop lid does not put a build machine to sleep mid-compile.
/// </para>
/// <para>
/// The consequence worth stating: a log entry can only ever add protection. A replayed disconnect cannot
/// take any away, because protection was never derived from the log in the first place.
/// </para>
/// </summary>
public sealed class SshSessionCorrelator
{
    public const string SourceId = "ssh";

    private readonly SshDetectionOptions _options;
    private readonly Dictionary<string, MonotonicStamp> _firstSeen = new(StringComparer.Ordinal);
    private Guid _epochId;

    public SshSessionCorrelator(SshDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Judge the current state.
    /// </summary>
    /// <param name="tcp">The established connections, or the fact that they could not be read.</param>
    /// <param name="log">What the authentication log said, read from the stored position.</param>
    public SshCorrelationResult Evaluate(
        TcpSnapshot tcp,
        SshLogRead log,
        MonotonicStamp now,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(log);

        ResetIfClockRestarted(now);

        var sessions = tcp.Succeeded
            ? tcp.Connections.Where(connection => _options.Ports.Contains(connection.LocalPort)).ToArray()
            : [];

        TrackFirstSeen(sessions, now, tcp.Succeeded);

        var holds = BuildHoldRequests(sessions, log, now);
        var report = BuildReport(tcp, log, sessions, nowUtc);

        // The position is only worth passing on when the log was actually readable. Offering one from a
        // failed or discontinuous read would invite the host to store it and lose whatever was skipped.
        var bookmark = log.State == SshLogChannelState.Available ? log.Bookmark : null;

        return new SshCorrelationResult(report, holds, bookmark, log.State);
    }

    private InhibitorSourceReport BuildReport(
        TcpSnapshot tcp,
        SshLogRead log,
        TcpConnection[] sessions,
        DateTimeOffset nowUtc)
    {
        if (sessions.Length > 0)
        {
            // Something is connected. That is the strongest and most useful thing to say, whatever the log is
            // doing, and it is why a broken log can never cause protection to be dropped while someone is on
            // the machine. A connection counts from the moment it appears: one that has not yet reached the
            // minimum age is one that is still authenticating, which is exactly the moment after a machine
            // has been woken up to be used.
            var inhibitors = sessions
                .Select(connection => new Inhibitor(
                    InhibitorKind.SshSession,
                    $"SSH connection from {connection.RemoteAddress}",
                    nowUtc,
                    connection.Key))
                .ToArray();

            return InhibitorSourceReport.Observed(SourceId, inhibitors);
        }

        if (!tcp.Succeeded)
        {
            return InhibitorSourceReport.Indeterminate(
                SourceId, $"The connection table could not be read: {tcp.Detail}");
        }

        return log.State switch
        {
            SshLogChannelState.Available => InhibitorSourceReport.ConfirmedAbsent(SourceId),

            // The user has accepted that connection state alone is enough to conclude nobody is connected.
            SshLogChannelState.NotInstalled when _options.TcpOnlyConfirmed =>
                InhibitorSourceReport.ConfirmedAbsent(SourceId),

            SshLogChannelState.NotInstalled => InhibitorSourceReport.Indeterminate(
                SourceId,
                "There is no OpenSSH log to read, so a session that is connecting cannot be told from no " +
                "session at all. Enable the log, or set ssh.tcpOnlyConfirmed to accept connection state alone."),

            SshLogChannelState.Discontinuous => InhibitorSourceReport.Indeterminate(
                SourceId,
                $"The OpenSSH log is no longer continuous, so logins may have been missed: {log.Detail}"),

            _ => InhibitorSourceReport.Indeterminate(
                SourceId, $"The OpenSSH log could not be read: {log.Detail}")
        };
    }

    private List<SshHoldRequest> BuildHoldRequests(
        TcpConnection[] sessions,
        SshLogRead log,
        MonotonicStamp now)
    {
        var holds = new List<SshHoldRequest>();

        // A login in the log is the authoritative cause of a hold, keyed on the log entry so that reading the
        // log again cannot produce a second one.
        foreach (var entry in log.Events.Where(entry => entry.Kind == SshAuthEventKind.Authenticated))
        {
            holds.Add(new SshHoldRequest(
                $"ssh-log:{entry.RecordId}",
                _options.HoldDuration,
                $"SSH login by {entry.UserName ?? "an unknown account"} from {entry.RemoteAddress ?? "an unknown address"}",
                entry.UserName,
                entry.RemoteAddress,
                entry.RecordId));
        }

        // Without a log there is nothing to key a hold to, so a connection that has stayed up long enough to
        // have finished authenticating earns one instead.
        if (log.State == SshLogChannelState.Available)
        {
            return holds;
        }

        foreach (var connection in sessions)
        {
            if (_firstSeen.TryGetValue(connection.Key, out var seen)
                && seen.EpochId == now.EpochId
                && now.Elapsed - seen.Elapsed >= _options.MinimumConnectionAge)
            {
                holds.Add(new SshHoldRequest(
                    $"ssh-tcp:{connection.Key}",
                    _options.HoldDuration,
                    $"SSH connection from {connection.RemoteAddress} has been established long enough to be a session",
                    RemoteAddress: connection.RemoteAddress));
            }
        }

        return holds;
    }

    private void TrackFirstSeen(TcpConnection[] sessions, MonotonicStamp now, bool succeeded)
    {
        if (!succeeded)
        {
            // The snapshot says nothing, so nothing is forgotten. Dropping the ages here would restart the
            // clock on every connection and delay the holds they are owed.
            return;
        }

        foreach (var connection in sessions)
        {
            if (!_firstSeen.ContainsKey(connection.Key))
            {
                // On the first cycle after the service starts, connections that have been up for hours are
                // recorded as new. That only delays the hold they earn; they already count as a reason to stay
                // awake, so nothing is left unprotected in the meantime.
                _firstSeen[connection.Key] = now;
            }
        }

        var live = sessions.Select(connection => connection.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _firstSeen.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            _firstSeen.Remove(key);
        }
    }

    private void ResetIfClockRestarted(MonotonicStamp now)
    {
        if (_epochId == now.EpochId)
        {
            return;
        }

        // Ages measured against a different monotonic origin cannot be compared with this one.
        _epochId = now.EpochId;
        _firstSeen.Clear();
    }
}
