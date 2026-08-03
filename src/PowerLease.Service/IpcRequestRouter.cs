using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Ipc.Contracts;
using PowerLease.Persistence.History;

namespace PowerLease.Service;

internal sealed class IpcRequestRouter
{
    // Two requests a second, sustained for a whole minute, is far beyond interactive use and generous for a
    // monitoring script, while still putting a firm ceiling on one authenticated account flooding the service.
    internal const int MaximumRequestsPerWindow = 120;

    private static readonly TimeSpan CommandQueueTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    // A week covers plausible interactive work and matches the longest configured SSH hold, without allowing a
    // malformed request to overflow deadline arithmetic deeper in the kernel.
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<KernelSnapshot> _snapshot;
    private readonly Func<LeaseLoadResult> _leases;
    private readonly Func<PowerCapabilitySnapshot> _wakeStatus;
    private readonly Func<LeaseCommand, CancellationToken, Task<LeaseCommandResult>> _dispatch;
    private readonly IClock _clock;
    private readonly object _rateLimitSync = new();
    private readonly Dictionary<string, CallerRateWindow> _rateWindows = new(StringComparer.Ordinal);

    public IpcRequestRouter(
        PowerLease.Application.Hosting.KernelLoop loop,
        LeaseCommandDispatcher commands,
        SqliteHistoryStore store,
        StoreGate storeGate,
        IPowerCapabilityProbe powerCapabilities,
        IClock clock)
        : this(
            () => loop.Snapshot,
            () => LoadActiveLeases(store, storeGate),
            powerCapabilities.Read,
            commands.PostAndWaitAsync,
            clock)
    {
    }

    internal IpcRequestRouter(
        Func<KernelSnapshot> snapshot,
        Func<LeaseLoadResult> leases,
        Func<PowerCapabilitySnapshot> wakeStatus,
        Func<LeaseCommand, CancellationToken, Task<LeaseCommandResult>> dispatch,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(wakeStatus);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(clock);
        _snapshot = snapshot;
        _leases = leases;
        _wakeStatus = wakeStatus;
        _dispatch = dispatch;
        _clock = clock;
    }

    public async Task<ResponseEnvelope> HandleAsync(
        RequestEnvelope request,
        CallerSnapshot caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        // Releasing gets its own allowance rather than an exemption. A status-polling script must never use up
        // the quota that lets its caller end a hold -- but an exempt method is an unbounded one, and every
        // release reaching the kernel becomes a queued command on the single loop that decides whether this
        // machine stays awake. Two separate windows keep both properties: neither can starve the other, and
        // neither is unbounded. The choice of window depends on the protocol version being the one we
        // understand, so a bogus envelope cannot pick its bucket by writing ReleaseLease in the method field.
        var releasing = request.ProtocolVersion == IpcProtocol.Version
            && request.Method == IpcMethods.ReleaseLease;

        if (IsRateLimited(caller, releasing))
        {
            return ResponseEnvelope.Refused(
                request.RequestId,
                "The caller is asking too often; wait a minute and try again.");
        }

        if (request.ProtocolVersion != IpcProtocol.Version)
        {
            return ResponseEnvelope.Refused(
                request.RequestId,
                $"Protocol version {request.ProtocolVersion} is not supported; use {IpcProtocol.Version}.");
        }

        // Authorization intentionally precedes the handler lookup. An unauthorized caller must not learn which
        // declared methods this service version happens to implement.
        if (!IpcMethods.IsAllowed(request.Method, caller.IsAdministrator))
        {
            return ResponseEnvelope.Refused(request.RequestId, "The caller is not allowed to use this method.");
        }

        return request.Method switch
        {
            IpcMethods.GetStatus => Accepted(request.RequestId, MapStatus(_snapshot())),
            IpcMethods.ListLeases => Accepted(request.RequestId, MapLeases(_leases(), _clock.UtcNow)),
            IpcMethods.GetWakeStatus => Accepted(request.RequestId, MapWakeStatus(_wakeStatus())),
            IpcMethods.CreateLease => await HandleCommandAsync(
                request,
                caller,
                LeaseCommandKind.Create,
                cancellationToken).ConfigureAwait(false),
            IpcMethods.RenewLease => await HandleCommandAsync(
                request,
                caller,
                LeaseCommandKind.Renew,
                cancellationToken).ConfigureAwait(false),
            IpcMethods.ReleaseLease => await HandleCommandAsync(
                request,
                caller,
                LeaseCommandKind.Release,
                cancellationToken).ConfigureAwait(false),
            _ => ResponseEnvelope.Refused(
                request.RequestId,
                "This method is not available in this version of PowerLease.")
        };
    }

    /// <param name="releasing">
    /// Whether this request ends a hold. Releases are counted in their own window, so however hard a caller
    /// polls for status it still has its full allowance left for the one request that gives a machine back.
    /// </param>
    private bool IsRateLimited(CallerSnapshot caller, bool releasing)
    {
        var key = releasing ? "release:" + caller.Sid : "request:" + caller.Sid;
        var now = _clock.Now;

        lock (_rateLimitSync)
        {
            if (!_rateWindows.TryGetValue(key, out var window)
                || window.Start.EpochId != now.EpochId
                || now.Elapsed < window.Start.Elapsed
                || now.Elapsed - window.Start.Elapsed >= RateLimitWindow)
            {
                _rateWindows[key] = new CallerRateWindow(now, requestCount: 1);
                RemoveExpiredRateWindows(now, key);
                return false;
            }

            if (window.RequestCount >= MaximumRequestsPerWindow)
            {
                return true;
            }

            window.RequestCount++;
            return false;
        }
    }

    private void RemoveExpiredRateWindows(MonotonicStamp now, string currentKey)
    {
        // A Windows machine normally has very few authenticated callers. Pruning only after the map grows past
        // that ordinary range keeps departed domain identities from accumulating for the life of the service.
        if (_rateWindows.Count < 128)
        {
            return;
        }

        foreach (var (sid, window) in _rateWindows.ToArray())
        {
            if (!string.Equals(sid, currentKey, StringComparison.Ordinal)
                && (window.Start.EpochId != now.EpochId
                    || now.Elapsed < window.Start.Elapsed
                    || now.Elapsed - window.Start.Elapsed >= RateLimitWindow))
            {
                _rateWindows.Remove(sid);
            }
        }
    }

    private async Task<ResponseEnvelope> HandleCommandAsync(
        RequestEnvelope request,
        CallerSnapshot caller,
        LeaseCommandKind kind,
        CancellationToken cancellationToken)
    {
        if (!TryReadCommand(request, kind, out var leaseId, out var duration, out var reason, out var error))
        {
            return ResponseEnvelope.Refused(request.RequestId, error!);
        }

        if (kind == LeaseCommandKind.Release
            && leaseId is null
            && !TryResolveOwnLease(caller, out leaseId, out error))
        {
            return ResponseEnvelope.Refused(request.RequestId, error!);
        }

        var now = _clock.Now;
        var command = new LeaseCommand
        {
            RequestId = $"ipc:{caller.Sid}:{request.RequestId:N}",
            Caller = caller,
            Kind = kind,
            LeaseId = leaseId,
            Duration = duration,
            Source = LeaseSource.Cli,
            Reason = reason,
            Deadline = new MonotonicStamp(now.EpochId, now.Elapsed + CommandQueueTimeout),
            PayloadHash = HashPayload(request.Method, request.PayloadJson)
        };

        var result = await _dispatch(command, cancellationToken).ConfigureAwait(false);

        // Every outcome the kernel produces, including a refusal, travels as an accepted envelope carrying a
        // CommandResponse. The client turns a non-null Error into the same "refused" exit code either way, so
        // singling out one rejection by matching words in its message bought nothing and would have changed
        // behaviour silently the day somebody reworded it.
        return Accepted(
            request.RequestId,
            new CommandResponse(result.Status.ToString(), result.LeaseId, result.Error));
    }

    private static bool TryReadCommand(
        RequestEnvelope request,
        LeaseCommandKind kind,
        out string? leaseId,
        out TimeSpan duration,
        out string? reason,
        out string? error)
    {
        leaseId = null;
        duration = TimeSpan.Zero;
        reason = null;
        error = null;

        try
        {
            switch (kind)
            {
                case LeaseCommandKind.Create:
                    {
                        var payload = JsonSerializer.Deserialize<CreateLeasePayload>(request.PayloadJson ?? "{}", Json);
                        if (payload is null || payload.Duration <= TimeSpan.Zero)
                        {
                            error = "CreateLease requires a positive duration.";
                            return false;
                        }

                        if (payload.Duration > MaximumLeaseDuration)
                        {
                            error = "CreateLease duration cannot exceed 7 days.";
                            return false;
                        }

                        leaseId = $"cli:{request.RequestId:N}";
                        duration = payload.Duration;
                        reason = payload.Reason;
                        return true;
                    }

                case LeaseCommandKind.Renew:
                    {
                        var payload = JsonSerializer.Deserialize<RenewLeasePayload>(request.PayloadJson ?? "{}", Json);
                        if (payload is null || string.IsNullOrWhiteSpace(payload.LeaseId) || payload.Duration <= TimeSpan.Zero)
                        {
                            error = "RenewLease requires a leaseId and a positive duration.";
                            return false;
                        }

                        if (payload.Duration > MaximumLeaseDuration)
                        {
                            error = "RenewLease duration cannot exceed 7 days.";
                            return false;
                        }

                        leaseId = payload.LeaseId;
                        duration = payload.Duration;
                        return true;
                    }

                case LeaseCommandKind.Release:
                    {
                        var payload = JsonSerializer.Deserialize<ReleaseLeasePayload>(request.PayloadJson ?? "{}", Json);
                        if (payload is null)
                        {
                            error = "ReleaseLease requires a request payload.";
                            return false;
                        }

                        leaseId = string.IsNullOrWhiteSpace(payload.LeaseId) ? null : payload.LeaseId;
                        return true;
                    }

                default:
                    error = "The command kind is not supported.";
                    return false;
            }
        }
        catch (JsonException exception)
        {
            error = $"The request payload is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private bool TryResolveOwnLease(CallerSnapshot caller, out string? leaseId, out string? error)
    {
        leaseId = null;
        error = null;
        var loaded = _leases();
        if (loaded.HasUnreadableRows)
        {
            error = "Stored holds could not all be read; specify the hold identifier to release.";
            return false;
        }

        var owned = loaded.Leases
            .Where(lease => lease.Status == LeaseStatus.Active
                && lease.OwnerSid is { Length: > 0 } ownerSid
                && string.Equals(ownerSid, caller.Sid, StringComparison.Ordinal))
            .Select(lease => lease.Id)
            .ToArray();

        if (owned.Length == 0)
        {
            error = "The caller has no active holds to release.";
            return false;
        }

        if (owned.Length > 1)
        {
            error = "The caller has more than one active hold; specify the hold identifier to release.";
            return false;
        }

        leaseId = owned[0];
        return true;
    }

    private static StatusResponse MapStatus(KernelSnapshot snapshot) => new()
    {
        Revision = snapshot.Revision,
        ProtectionState = snapshot.ProtectionState switch
        {
            ProtectionState.Released => ReportedProtectionState.Released,
            ProtectionState.Protected => ReportedProtectionState.Protected,
            ProtectionState.Unprotected => ReportedProtectionState.Unprotected,
            _ => throw new InvalidOperationException($"Unknown protection state '{snapshot.ProtectionState}'.")
        },
        ShouldHold = snapshot.ShouldHold,
        Inhibitors = snapshot.Decision.Inhibitors
            .Select(inhibitor => new ReportedInhibitor(
                inhibitor.Kind.ToString(),
                inhibitor.Reason,
                inhibitor.SinceUtc,
                inhibitor.Detail))
            .ToArray(),
        UncoveredKinds = snapshot.Decision.UncoveredKinds.Select(kind => kind.ToString()).ToArray(),
        Sources = snapshot.Sources
            .Where(source => !source.Trusted)
            .Select(source => new ReportedSource(source.SourceId, source.Trusted, source.Detail))
            .ToArray(),
        UnhealthySources = snapshot.UnhealthySources,
        Faults = snapshot.Faults.Select(fault => $"{fault.Key}: {fault.Message}").ToArray(),
        EmergencyInhibitRaised = snapshot.EmergencyInhibitRaised,
        EmergencyInhibitReason = snapshot.EmergencyInhibitReason,
        GracePeriodActive = snapshot.GracePeriodActive,
        HistoryWriteFailures = snapshot.HistoryWriteFailures
    };

    private static ListLeasesResponse MapLeases(LeaseLoadResult loaded, DateTimeOffset nowUtc)
    {
        return new ListLeasesResponse
        {
            Leases = loaded.Leases.Select(lease => new ReportedLease(
                lease.Id,
                lease.Source.ToString(),
                lease.Reason,
                lease.OwnerUser,
                lease.StartedAtUtc,
                Remaining(lease.ExpiresAtUtc, nowUtc),
                lease.Status.ToString())).ToArray()
        };
    }

    private static WakeStatusResponse MapWakeStatus(PowerCapabilitySnapshot snapshot) => new()
    {
        SystemRequiredHonouredOnMains = snapshot.SystemRequiredHonouredOnMains,
        SystemRequiredHonouredOnBattery = snapshot.SystemRequiredHonouredOnBattery,
        ModernStandby = snapshot.ModernStandby,
        RunningOnBattery = snapshot.RunningOnBattery,
        WakeTimersAllowed = null,
        Unavailable =
        [
            .. snapshot.Unavailable,
            "Wake timer policy is not available from this service version."
        ]
    };

    private static ResponseEnvelope Accepted<T>(Guid requestId, T payload) =>
        new(IpcProtocol.Version, requestId, Accepted: true, JsonSerializer.Serialize(payload, Json));

    private static TimeSpan Remaining(DateTimeOffset? expiresAtUtc, DateTimeOffset nowUtc) =>
        expiresAtUtc is { } expiry && expiry > nowUtc ? expiry - nowUtc : TimeSpan.Zero;

    private static string HashPayload(string method, string? payloadJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + (payloadJson ?? string.Empty))));

    private static LeaseLoadResult LoadActiveLeases(SqliteHistoryStore store, StoreGate gate)
    {
        lock (gate.SyncRoot)
        {
            return store.LoadLeases(LeaseStatus.Active);
        }
    }

    private sealed class CallerRateWindow(MonotonicStamp start, int requestCount)
    {
        public MonotonicStamp Start { get; } = start;

        public int RequestCount { get; set; } = requestCount;
    }

}
