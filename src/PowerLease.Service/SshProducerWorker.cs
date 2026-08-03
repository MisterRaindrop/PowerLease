using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PowerLease.Application.Hosting;
using PowerLease.Application.Inhibitors;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows.Sources;
using PowerLease.Persistence.Configuration;
using PowerLease.Persistence.History;

namespace PowerLease.Service;

internal sealed class SshProducerWorker : SourceProducerWorker
{
    private const string BookmarkChannel = "OpenSSH/Operational";
    private const string LocalSystemSid = "S-1-5-18";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly CallerSnapshot SystemCaller = new()
    {
        Sid = LocalSystemSid,
        AccountName = @"NT AUTHORITY\SYSTEM",
        IsElevated = true,
        IsAdministrator = true
    };

    private readonly SshSessionCorrelator _correlator;
    private readonly ExtendedTcpTableProvider _tcp = new();
    private readonly OpenSshEventProvider? _events;
    private readonly LeaseCommandDispatcher _commands;
    private readonly SqliteHistoryStore _store;
    private readonly StoreGate _storeGate;
    private readonly HashSet<string> _satisfied;
    private string? _bookmark;

    public SshProducerWorker(
        PowerLeaseConfig config,
        LeaseLoadResult loaded,
        KernelLoop loop,
        LeaseCommandDispatcher commands,
        SqliteHistoryStore store,
        StoreGate storeGate,
        IClock clock,
        ILogger<SshProducerWorker> logger)
        : base(SshSessionCorrelator.SourceId, Interval, loop, clock, logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storeGate);

        _correlator = new SshSessionCorrelator(new SshDetectionOptions
        {
            Ports = config.Ssh.Ports,
            MinimumConnectionAge = TimeSpan.FromSeconds(config.Ssh.MinimumConnectionSeconds),
            HoldDuration = TimeSpan.FromMinutes(config.Ssh.DefaultHoldMinutes),
            TcpOnlyConfirmed = config.Ssh.TcpOnlyConfirmed
        });
        _events = config.Ssh.EventLogEnabled ? new OpenSshEventProvider() : null;
        _commands = commands;
        _store = store;
        _storeGate = storeGate;
        _satisfied = new HashSet<string>(
            loaded.Leases
                .Where(lease => lease.Status == LeaseStatus.Active)
                .Select(lease => lease.Id),
            StringComparer.Ordinal);

        lock (_storeGate.SyncRoot)
        {
            _bookmark = _store.GetBookmark(BookmarkChannel);
        }
    }

    protected override async Task ProduceAsync(CancellationToken stoppingToken)
    {
        var now = Clock.Now;
        var log = _events?.Read(_bookmark) ?? SshLogRead.Unavailable(
            SshLogChannelState.NotInstalled,
            "OpenSSH Event Log reading is disabled by configuration.");
        var result = _correlator.Evaluate(_tcp.GetEstablishedConnections(), log, now, Clock.UtcNow);

        var awaiting = new List<(string Key, Task<LeaseCommandResult> Completion)>();
        foreach (var hold in result.HoldRequests)
        {
            if (_satisfied.Contains(hold.Key))
            {
                continue;
            }

            awaiting.Add((
                hold.Key,
                _commands.PostAndWaitAsync(CreateCommand(hold), stoppingToken)));
        }

        // Commands and the source report are posted in one producer turn. Persistence acknowledgements arrive
        // later from the sole pump and determine when the bookmark may safely advance.
        Post(result.Report, now);

        if (awaiting.Count > 0)
        {
            await Task.WhenAll(awaiting.Select(item => item.Completion)).ConfigureAwait(false);
            foreach (var item in awaiting)
            {
                var commandResult = await item.Completion.ConfigureAwait(false);
                if (commandResult.Status is LeaseCommandStatus.Created or LeaseCommandStatus.AlreadyDone)
                {
                    _satisfied.Add(item.Key);
                }
            }
        }

        if (result.Bookmark is { } bookmark
            && result.HoldRequests.All(hold => _satisfied.Contains(hold.Key)))
        {
            lock (_storeGate.SyncRoot)
            {
                using var transaction = _store.BeginTransaction();
                transaction.SetBookmark(BookmarkChannel, bookmark, Clock.UtcNow);
                transaction.Commit();
                _bookmark = bookmark;
            }
        }

        PruneSatisfied(result.HoldRequests);
    }

    private LeaseCommand CreateCommand(SshHoldRequest hold)
    {
        var now = Clock.Now;
        return new LeaseCommand
        {
            RequestId = $"ssh-hold:{hold.Key}",
            Caller = SystemCaller,
            Kind = LeaseCommandKind.Create,
            LeaseId = hold.Key,
            Duration = hold.Duration,
            Source = LeaseSource.SshSession,
            Reason = hold.Reason,
            Deadline = new MonotonicStamp(now.EpochId, now.Elapsed + CommandTimeout),
            PayloadHash = HashPayload(hold)
        };
    }

    private void PruneSatisfied(IReadOnlyList<SshHoldRequest> currentHolds)
    {
        HashSet<string> active;
        lock (_storeGate.SyncRoot)
        {
            active = new HashSet<string>(
                _store.LoadLeases(LeaseStatus.Active).Leases.Select(lease => lease.Id),
                StringComparer.Ordinal);
        }

        var current = new HashSet<string>(currentHolds.Select(hold => hold.Key), StringComparer.Ordinal);
        _satisfied.RemoveWhere(key => !current.Contains(key) && !active.Contains(key));
    }

    /// <summary>
    /// The identity of the hold, and deliberately nothing else.
    /// <para>
    /// The idempotency record refuses a request identifier that comes back with a different payload, which is
    /// right for a client that reused an identifier by mistake but wrong here, where the identifier is derived
    /// from the log record. Including the duration or the reason would mean that changing
    /// <c>ssh.defaultHoldMinutes</c> between a crash and the restart turned the replay of an already-committed
    /// hold into a conflict, so its key would never become satisfied and the bookmark would never advance --
    /// the log would stop being read and no later login would ever be seen. The key alone cannot disagree
    /// with itself.
    /// </para>
    /// </summary>
    private static string HashPayload(SshHoldRequest hold) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hold.Key)));
}
