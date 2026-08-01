using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>
/// Decides whether the machine is held awake, and is the only thing that decides it.
/// <para>
/// Everything else in the product is a source: producers deliver observations, the pipe server delivers
/// commands, and none of them touch the safety state. All of it funnels through this one object, driven
/// by one loop, which is what makes "protection is only released when every source positively confirmed
/// there is nothing to hold for" a property that can be tested rather than hoped for.
/// </para>
/// <para>
/// Not thread-safe, deliberately. Every method except <see cref="RaiseEmergencyInhibit" /> and
/// <see cref="Snapshot" /> must be called from the single loop. The two exceptions are the two things
/// that cannot wait for a turn: throwing the one-way emergency switch, and reading the published state.
/// </para>
/// <para>
/// It performs no input or output. Durable work is emitted as <see cref="KernelEffect" /> values for the
/// host to carry out, because a loop that waits on a disk is a loop that cannot decide whether to keep a
/// machine awake while the disk is wedged. This is structural rather than a convention: the project this
/// lives in cannot reference the persistence project at all.
/// </para>
/// </summary>
public sealed class InhibitKernel
{
    /// <summary>
    /// How many evaluations an effect may go unreported before the kernel stops waiting for it. Generous on
    /// purpose: a slow disk must not look like a lost effect.
    /// </summary>
    private const int AbandonedEffectRevisions = 1000;

    private readonly KernelOptions _options;
    private readonly PowerInhibitCoordinator _power;
    private readonly IClock _clock;
    private readonly FaultRegistry _faults;
    private ExpectedProducerSet _producers;
    private IReadOnlyList<string> _expectedSources;
    private readonly GracePeriodGuard _grace = new();
    private readonly EmergencyInhibitLatch _emergency = new();

    private readonly Dictionary<string, SourceEntry> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackedLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<long, PendingEffect> _inFlight = [];
    private readonly List<KernelEffect> _pendingEffects = [];
    private readonly List<LeaseCommandResult> _pendingResults = [];

    private KernelSnapshot _snapshot = KernelSnapshot.Initial;
    private long _revision;
    private long _configGeneration;
    private long _nextEffectId;
    private MonotonicStamp? _resyncDemandedAt;
    private long _historyWriteFailures;
    private string? _lastHistoryWriteError;

    public InhibitKernel(KernelOptions options, PowerInhibitCoordinator power, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options;
        _power = power;
        _clock = clock;
        _faults = new FaultRegistry(options.ConsecutiveHealthyToClearTransient);
        _expectedSources = options.ExpectedSources;
        _producers = new ExpectedProducerSet(_expectedSources, options.HeartbeatFreshness);
    }

    /// <summary>
    /// The last published decision. Safe to read from any thread, and never blocks the loop.
    /// </summary>
    public KernelSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Force the machine awake immediately, from any thread.
    /// <para>
    /// The only state a source may change without going through the loop, and it only moves in the
    /// direction that adds protection. The kernel lowers it again once every expected source has reported
    /// afresh, so a source that raises it cannot also decide the alarm is over.
    /// </para>
    /// </summary>
    public bool RaiseEmergencyInhibit(string reason) => _emergency.Raise(reason);

    /// <summary>Whether the emergency switch is currently thrown.</summary>
    public bool EmergencyInhibitRaised => _emergency.IsRaised;

    /// <summary>
    /// Take in what a source saw.
    /// </summary>
    public ObservationDisposition Apply(SourceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var stamp = observation.Stamp;
        var sourceId = stamp.SourceId;
        var epoch = _clock.Now.EpochId;

        if (stamp.ClockEpochId != epoch)
        {
            // Taken against a different monotonic clock: it predates a resume or a restart of the source,
            // and its age cannot be computed against ours. Note the baseline is not adopted, because a
            // stamp from another epoch is not a point on our number line.
            return Distrust(
                sourceId,
                stamp,
                adoptBaseline: false,
                ObservationDisposition.DistrustedClockEpoch,
                $"Source '{sourceId}' reported against clock epoch {stamp.ClockEpochId}, not {epoch}.");
        }

        if (stamp.ConfigGeneration != _configGeneration)
        {
            return Distrust(
                sourceId,
                stamp,
                adoptBaseline: true,
                ObservationDisposition.DistrustedConfigGeneration,
                $"Source '{sourceId}' reported against configuration generation " +
                $"{stamp.ConfigGeneration}, not {_configGeneration}.");
        }

        if (_sources.TryGetValue(sourceId, out var existing) && existing.HasBaseline)
        {
            if (!stamp.IsComparableTo(existing.Stamp))
            {
                // Same epoch and configuration, so the difference is the source's own generation: it
                // restarted and its sequence begins again. Adopt the new baseline but do not act on this
                // one, because nothing connects it to what was believed a moment ago.
                return Distrust(
                    sourceId,
                    stamp,
                    adoptBaseline: true,
                    ObservationDisposition.DistrustedSequenceGap,
                    $"Source '{sourceId}' restarted.");
            }

            if (stamp.SourceSequence <= existing.Stamp.SourceSequence)
            {
                // What is held is at least as new, so nothing is lost by ignoring this. This is the only
                // case where dropping an observation is safe, and it is safe precisely because it cannot
                // leave a staler report in place.
                existing.LastDisposition = stamp.SourceSequence == existing.Stamp.SourceSequence
                    ? ObservationDisposition.DiscardedDuplicate
                    : ObservationDisposition.DiscardedOutOfOrder;
                return existing.LastDisposition;
            }

            if (stamp.SourceSequence > existing.Stamp.SourceSequence + 1)
            {
                var missed = stamp.SourceSequence - existing.Stamp.SourceSequence - 1;
                return Distrust(
                    sourceId,
                    stamp,
                    adoptBaseline: true,
                    ObservationDisposition.DistrustedSequenceGap,
                    $"Missed {missed} observation(s) from '{sourceId}'.");
            }
        }

        _sources[sourceId] = new SourceEntry
        {
            Stamp = stamp,
            Report = observation.Report,
            LastDisposition = ObservationDisposition.Accepted,
            HasBaseline = true
        };

        _producers.Heartbeat(sourceId, stamp.ObservedAt);
        return ObservationDisposition.Accepted;
    }

    /// <summary>Take in something that changes what the kernel believes.</summary>
    public void Apply(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var now = _clock.Now;

        switch (message)
        {
            case ResumedFromSleep resumed:
                // Every observation describes a machine that has since been asleep, and the operating
                // system dropped the power request when it suspended.
                DiscardEverySourceObservation($"the machine resumed: {resumed.Reason}");
                ReestablishLeases(now);
                _power.Invalidate();
                _grace.Begin(now, _options.ResumeGracePeriod, $"resumed: {resumed.Reason}");
                break;

            case ServiceStarted started:
                DiscardEverySourceObservation($"the service started: {started.Reason}");
                ReestablishLeases(now);
                _power.Invalidate();
                _grace.Begin(now, _options.StartupGracePeriod, $"service started: {started.Reason}");
                break;

            case ConfigurationReplaced replaced:
                _configGeneration = replaced.ConfigGeneration;
                Reconfigure(replaced.ExpectedSources);
                DiscardEverySourceObservation("configuration was replaced");
                break;

            case TimeAdjusted adjusted:
                // The kernel does not recompute anything itself. A schedule is written in local time, so
                // a source that evaluated one against the old zone may now be wrong, and the kernel has no
                // way to know which sources those are. Everyone reports again.
                DiscardEverySourceObservation($"the clock or time zone changed: {adjusted.Reason}");
                break;

            case SourceReportsDropped dropped:
                Distrust(
                    dropped.SourceId,
                    default,
                    adoptBaseline: false,
                    ObservationDisposition.DistrustedSequenceGap,
                    $"{dropped.Count} report(s) from '{dropped.SourceId}' were dropped before they arrived.");
                break;

            case FaultObserved fault:
                _faults.Report(fault.Key, fault.Severity, fault.Message, _clock.UtcNow);
                break;

            case FaultHealthy healthy:
                _faults.ReportHealthy(healthy.Key);
                break;

            case FaultRepaired repaired:
                _faults.Clear(repaired.Key);
                break;

            case EffectFinished finished:
                OnEffectFinished(finished.Completion);
                break;

            default:
                throw new ArgumentException($"Unhandled control message {message.GetType().Name}.", nameof(message));
        }
    }

    /// <summary>
    /// Carry out a lease command, or refuse it. The answer is delivered by the next
    /// <see cref="Step" /> once anything durable it needs has been done.
    /// </summary>
    public void Execute(LeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = _clock.Now;

        // The deadline is measured by the server on the monotonic clock. A deadline from another epoch
        // cannot be compared, and refusing costs nothing: not acting never reduces protection, and the
        // caller may safely retry with the same identifier.
        if (command.Deadline.EpochId != now.EpochId || now.Elapsed >= command.Deadline.Elapsed)
        {
            _pendingResults.Add(new LeaseCommandResult(
                command.RequestId,
                LeaseCommandStatus.DeadlineExpired,
                Error: "The request was still queued when its deadline passed. Nothing was changed."));
            return;
        }

        switch (command.Kind)
        {
            case LeaseCommandKind.Create:
                Create(command, now);
                break;
            case LeaseCommandKind.Renew:
                Renew(command, now);
                break;
            case LeaseCommandKind.Release:
                Release(command, now);
                break;
            default:
                Reject(command, $"Unsupported command {command.Kind}.");
                break;
        }
    }

    /// <summary>
    /// Work out whether to hold the machine awake, act on it, and publish the result.
    /// </summary>
    public KernelStepResult Step()
    {
        var now = _clock.Now;
        var nowUtc = _clock.UtcNow;

        ExpireLeases(now, nowUtc);
        RefreshLeaseCheckpoints(now, nowUtc);
        DropAbandonedEffects();

        // Before the reports are gathered, not after. Lowering the alarm afterwards would publish a
        // snapshot saying the alarm is over while still listing it among the reasons to stay awake, and a
        // status output that contradicts itself is worse than a stale one.
        TryLowerEmergencyLatch(now);

        var reports = new List<InhibitorSourceReport>();
        var sourceStates = new List<SourceState>();

        foreach (var (sourceId, entry) in _sources)
        {
            var evaluated = Evaluate(sourceId, entry, now);
            reports.Add(evaluated.Report);
            sourceStates.Add(evaluated.State);
        }

        reports.Add(Compose("leases", LeaseInhibitors(nowUtc)));
        reports.Add(Compose("faults", _faults.ToInhibitors()));
        reports.Add(Compose("producers", _producers.ToInhibitors(now, nowUtc)));
        reports.Add(Compose("grace", GraceInhibitors(now, nowUtc)));
        reports.Add(Compose("emergency", EmergencyInhibitors(nowUtc)));

        var decision = InhibitAggregator.Aggregate(reports, _options.CoveredKinds, nowUtc);

        var previous = _power.State;
        var state = _power.Ensure(decision.ShouldHold);

        if (state == ProtectionState.Unprotected)
        {
            // Wanting to hold and being refused is a failure of the product's whole purpose, so it is
            // latched rather than merely reported. A latched fault is itself a reason to stay awake, which
            // keeps the kernel trying instead of settling into a state where it has given up quietly.
            _faults.Report(
                "power-request",
                FaultSeverity.Transient,
                "The system is not honouring the keep-awake request. Check that the active power plan " +
                "allows a program to keep the computer awake.",
                nowUtc);
        }
        else if (state == ProtectionState.Protected)
        {
            _faults.ReportHealthy("power-request");
        }

        _revision++;

        if (state != previous)
        {
            Emit(new KernelEffect
            {
                EffectId = ++_nextEffectId,
                Kind = EffectKind.RecordInhibitChange,
                ProtectionState = state,
                InhibitorKinds = [.. decision.Inhibitors.Select(inhibitor => inhibitor.Kind).Distinct()],
                Reason = decision.Inhibitors.Count == 0
                    ? "no reason to stay awake remained"
                    : decision.Inhibitors[0].Reason,
                Revision = _revision
            });
        }

        var snapshot = new KernelSnapshot(
            _revision,
            state,
            decision,
            _faults.Active,
            _producers.UnhealthySources(now),
            sourceStates,
            _emergency.IsRaised,
            _emergency.Reason,
            _grace.IsActive(now),
            _configGeneration,
            _historyWriteFailures,
            _lastHistoryWriteError);

        Volatile.Write(ref _snapshot, snapshot);

        var effects = _pendingEffects.ToArray();
        _pendingEffects.Clear();
        var results = _pendingResults.ToArray();
        _pendingResults.Clear();

        return new KernelStepResult(snapshot, effects, results, state != previous);
    }

    private (InhibitorSourceReport Report, SourceState State) Evaluate(
        string sourceId,
        SourceEntry entry,
        MonotonicStamp now)
    {
        if (entry.Distrusted is { } reason)
        {
            return (
                InhibitorSourceReport.Indeterminate(sourceId, reason),
                new SourceState(sourceId, entry.LastDisposition, Trusted: false, reason));
        }

        // Freshness is judged now, not when the observation arrived. A source that stopped reporting must
        // stop being believed, and the report it left behind may well say nothing is happening.
        var age = entry.Stamp.ObservedAt.EpochId == now.EpochId
            ? now.Elapsed - entry.Stamp.ObservedAt.Elapsed
            : TimeSpan.MaxValue;

        if (age < TimeSpan.Zero || age > _options.ObservationFreshness)
        {
            var stale = $"The last report from '{sourceId}' is too old to act on.";
            return (
                InhibitorSourceReport.Indeterminate(sourceId, stale),
                new SourceState(sourceId, entry.LastDisposition, Trusted: false, stale));
        }

        return (entry.Report, new SourceState(sourceId, entry.LastDisposition, Trusted: true, null));
    }

    private static InhibitorSourceReport Compose(string sourceId, IReadOnlyList<Inhibitor> inhibitors) =>
        inhibitors.Count == 0
            ? InhibitorSourceReport.ConfirmedAbsent(sourceId)
            : InhibitorSourceReport.Observed(sourceId, [.. inhibitors]);

    private List<Inhibitor> LeaseInhibitors(DateTimeOffset nowUtc)
    {
        var inhibitors = new List<Inhibitor>();
        foreach (var (id, tracked) in _leases)
        {
            // A lease counts while it is still only provisional and while its ending is still being made
            // durable. Protection is added before it is recorded and given up only after.
            inhibitors.Add(new Inhibitor(
                tracked.Lease.Source == LeaseSource.SshSession ? InhibitorKind.SshSession : InhibitorKind.CliLease,
                tracked.Lease.Reason ?? $"lease {id}",
                tracked.Lease.StartedAtUtc,
                id));
        }

        _ = nowUtc;
        return inhibitors;
    }

    private IReadOnlyList<Inhibitor> GraceInhibitors(MonotonicStamp now, DateTimeOffset nowUtc) =>
        _grace.ToInhibitor(now, nowUtc) is { } inhibitor ? [inhibitor] : [];

    private IReadOnlyList<Inhibitor> EmergencyInhibitors(DateTimeOffset nowUtc) =>
        _emergency.IsRaised
            ? [new Inhibitor(InhibitorKind.Fault, _emergency.Reason ?? "emergency inhibit raised", nowUtc)]
            : [];

    private void ExpireLeases(MonotonicStamp now, DateTimeOffset nowUtc)
    {
        foreach (var (id, tracked) in _leases.ToArray())
        {
            if (tracked.Commit == LeaseCommitState.ReleasePending)
            {
                // Its ending is already being made durable; expiry must not race that.
                continue;
            }

            if (!tracked.Deadline.IsInEpoch(now.EpochId))
            {
                // The lease was measured against a different clock. Re-establishing it belongs to the
                // startup path, so until that happens it keeps holding rather than being read wrongly.
                continue;
            }

            if (!tracked.Deadline.HasExpiredAt(now))
            {
                continue;
            }

            _leases.Remove(id);

            // A failed write for this lease no longer means anything: the lease is over.
            _faults.Clear(PersistFaultKey(id));

            // Record the ending. Unlike a release there is no ordering to respect -- protection is already
            // gone -- but it does have to be written, because a row left saying "active" with time still on
            // it would be re-granted that time on the next restart. A three-hour hold that ran out weeks ago
            // would come back for three more hours, every time the machine started.
            Emit(new KernelEffect
            {
                EffectId = ++_nextEffectId,
                Kind = EffectKind.PersistLease,
                Lease = tracked.Lease with
                {
                    Status = LeaseStatus.Expired,
                    EndedAtUtc = nowUtc,
                    EndReason = "the lease ran out",
                    RemainingAtCheckpoint = TimeSpan.Zero,
                    CheckpointUtc = nowUtc
                },
                Revision = _revision
            });
        }
    }

    /// <summary>
    /// Put every lease back on the current monotonic clock.
    /// <para>
    /// The clock starts again after a resume and after the service restarts, and a lease measured against the
    /// old one can never be found to have expired. Without this a lease that lived through a single resume
    /// would hold the machine awake for as long as the service ran -- the failure that makes a power
    /// management tool worse than not having one.
    /// </para>
    /// <para>
    /// A lease whose ending is already being written is left alone: it is on its way out, and re-granting it
    /// would resurrect it for a fresh duration.
    /// </para>
    /// <para>
    /// Deliberately not persisted here. If the service dies before the next checkpoint is written, the stored
    /// row still holds the older, larger remaining time, so the lease comes back holding for longer rather
    /// than for less.
    /// </para>
    /// </summary>
    private void ReestablishLeases(MonotonicStamp now)
    {
        var nowUtc = _clock.UtcNow;

        foreach (var tracked in _leases.Values)
        {
            if (tracked.Commit == LeaseCommitState.ReleasePending || tracked.Deadline.IsInEpoch(now.EpochId))
            {
                continue;
            }

            var resumed = LeaseDeadline.Resume(
                tracked.Lease.TryGetCheckpoint(),
                tracked.Lease.OriginalDuration,
                now);

            tracked.Deadline = resumed.Deadline;
            tracked.Lease = tracked.Lease with
            {
                EpochId = now.EpochId,
                RemainingAtCheckpoint = resumed.Deadline.RemainingAt(now),
                CheckpointUtc = nowUtc
            };
        }
    }

    /// <summary>
    /// Keep each lease's recorded remaining time current.
    /// <para>
    /// This is what makes re-establishing a lease honest. The checkpoint is the only thing a resume has to go
    /// on, so if it were only written when a lease was created or renewed, every resume would hand back the
    /// duration the lease started with -- and a lease on a machine that sleeps and wakes repeatedly would
    /// never end.
    /// </para>
    /// </summary>
    private void RefreshLeaseCheckpoints(MonotonicStamp now, DateTimeOffset nowUtc)
    {
        foreach (var tracked in _leases.Values)
        {
            if (tracked.Commit == LeaseCommitState.ReleasePending || !tracked.Deadline.IsInEpoch(now.EpochId))
            {
                continue;
            }

            tracked.Lease = tracked.Lease with
            {
                RemainingAtCheckpoint = tracked.Deadline.RemainingAt(now),
                CheckpointUtc = nowUtc
            };
        }
    }

    private void Create(LeaseCommand command, MonotonicStamp now)
    {
        if (string.IsNullOrEmpty(command.LeaseId))
        {
            Reject(command, "A lease identifier is required, so that a retry refers to the same lease.");
            return;
        }

        if (command.Duration <= TimeSpan.Zero)
        {
            Reject(command, "A lease needs a positive duration.");
            return;
        }

        if (_leases.ContainsKey(command.LeaseId))
        {
            Reject(command, $"Lease '{command.LeaseId}' already exists.");
            return;
        }

        var nowUtc = _clock.UtcNow;
        var lease = new KeepAwakeLease
        {
            Id = command.LeaseId,
            Source = command.Source,
            Reason = command.Reason,
            OwnerUser = command.Caller.AccountName,
            StartedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + command.Duration,
            AutoRenew = false,
            Status = LeaseStatus.Active,
            EpochId = now.EpochId,
            OriginalDuration = command.Duration,
            RemainingAtCheckpoint = command.Duration,
            CheckpointUtc = nowUtc
        };

        // In place before it is durable. Adding protection early is safe; the reverse is not.
        _leases[command.LeaseId] = new TrackedLease
        {
            Lease = lease,
            Deadline = LeaseDeadline.Grant(now, command.Duration),
            Commit = LeaseCommitState.Provisional,
            OwnerSid = command.Caller.Sid
        };

        EmitLeaseEffect(EffectKind.PersistLease, command, lease, LeaseCommandStatus.Created);
    }

    private void Renew(LeaseCommand command, MonotonicStamp now)
    {
        if (command.LeaseId is null || !_leases.TryGetValue(command.LeaseId, out var tracked))
        {
            Reject(command, $"No lease '{command.LeaseId}'.");
            return;
        }

        if (tracked.Commit == LeaseCommitState.ReleasePending)
        {
            Reject(command, $"Lease '{command.LeaseId}' is being released.");
            return;
        }

        if (!MayChange(command.Caller, tracked))
        {
            Reject(command, "A lease may only be changed by the account that created it, or an administrator.");
            return;
        }

        if (command.Duration <= TimeSpan.Zero)
        {
            Reject(command, "A renewal needs a positive duration.");
            return;
        }

        if (!tracked.Deadline.IsInEpoch(now.EpochId))
        {
            Reject(command, "The lease has not been re-established on the current clock yet.");
            return;
        }

        // Renew never shortens, so a renewal can only add protection and is applied at once.
        tracked.Deadline = tracked.Deadline.Renew(now, command.Duration);

        var nowUtc = _clock.UtcNow;
        var remaining = tracked.Deadline.RemainingAt(now);
        tracked.Lease = tracked.Lease with
        {
            LastRenewedAtUtc = nowUtc,
            LastRenewDuration = command.Duration,
            ExpiresAtUtc = nowUtc + remaining,
            RemainingAtCheckpoint = remaining,
            CheckpointUtc = nowUtc
        };

        EmitLeaseEffect(EffectKind.PersistLease, command, tracked.Lease, LeaseCommandStatus.Renewed);
    }

    private void Release(LeaseCommand command, MonotonicStamp now)
    {
        if (command.LeaseId is null || !_leases.TryGetValue(command.LeaseId, out var tracked))
        {
            Reject(command, $"No lease '{command.LeaseId}'.");
            return;
        }

        if (!MayChange(command.Caller, tracked))
        {
            Reject(command, "A lease may only be released by the account that created it, or an administrator.");
            return;
        }

        var nowUtc = _clock.UtcNow;

        // The inhibitor stays in place until the ending is durable. This is the one direction that reduces
        // protection, so it is the one that has to wait for the disk.
        tracked.Commit = LeaseCommitState.ReleasePending;
        tracked.Lease = tracked.Lease with
        {
            Status = LeaseStatus.Released,
            EndedAtUtc = nowUtc,
            EndReason = $"released by {command.Caller.AccountName ?? command.Caller.Sid}",
            RemainingAtCheckpoint = tracked.Deadline.IsInEpoch(now.EpochId)
                ? tracked.Deadline.RemainingAt(now)
                : tracked.Lease.RemainingAtCheckpoint,
            CheckpointUtc = nowUtc
        };

        EmitLeaseEffect(EffectKind.PersistLeaseRelease, command, tracked.Lease, LeaseCommandStatus.Released);
    }

    private static bool MayChange(CallerSnapshot caller, TrackedLease tracked) =>
        caller.IsAdministrator || string.Equals(caller.Sid, tracked.OwnerSid, StringComparison.Ordinal);

    private void EmitLeaseEffect(
        EffectKind kind,
        LeaseCommand command,
        KeepAwakeLease lease,
        LeaseCommandStatus success)
    {
        var effectId = ++_nextEffectId;

        _inFlight[effectId] = new PendingEffect
        {
            Kind = kind,
            LeaseId = lease.Id,
            RequestId = command.RequestId,
            Success = success,
            IssuedAtRevision = _revision
        };

        _pendingEffects.Add(new KernelEffect
        {
            EffectId = effectId,
            Kind = kind,
            RequestId = command.RequestId,
            Caller = command.Caller,
            PayloadHash = command.PayloadHash,
            Lease = lease,
            Revision = _revision
        });
    }

    private void OnEffectFinished(EffectCompletion completion)
    {
        if (!_inFlight.Remove(completion.EffectId, out var pending))
        {
            // Either already handled or never issued. Nothing to undo either way.
            return;
        }

        if (pending.Kind == EffectKind.RecordInhibitChange)
        {
            // Deliberately not a fault. A fault is a reason to stay awake, and the rule for that is whether the
            // evidence behind a release decision can be trusted -- but this is the record of decisions already
            // made, not evidence for the current one. Treating it as a fault would also deadlock: the fault
            // holds, so the protection state stops changing, so no further history is written, so nothing ever
            // reports it healthy and it could never clear. It is counted and published instead, so status can
            // show that history is being lost without the machine being pinned awake over a disk problem that
            // has no bearing on safety.
            if (completion.Outcome != EffectOutcome.Succeeded)
            {
                _historyWriteFailures++;
                _lastHistoryWriteError = completion.Error;
            }

            return;
        }

        var leaseId = pending.LeaseId!;
        _leases.TryGetValue(leaseId, out var tracked);

        if (pending.RequestId is not { } requestId)
        {
            // Nobody is waiting on this one -- it records a lease the kernel ended by itself. Only its failure
            // matters, and that is handled below.
            if (completion.Outcome != EffectOutcome.Succeeded)
            {
                _faults.Report(
                    PersistFaultKey(leaseId),
                    FaultSeverity.Transient,
                    $"The end of lease '{leaseId}' could not be recorded: {completion.Error}",
                    _clock.UtcNow);
            }

            return;
        }

        switch (completion.Outcome)
        {
            case EffectOutcome.Succeeded when pending.Kind == EffectKind.PersistLeaseRelease:
                _leases.Remove(leaseId);
                _faults.Clear(PersistFaultKey(leaseId));
                _pendingResults.Add(new LeaseCommandResult(requestId, LeaseCommandStatus.Released, leaseId));
                break;

            case EffectOutcome.Succeeded:
                if (tracked is not null)
                {
                    tracked.Commit = LeaseCommitState.Committed;
                }

                // Lets an earlier write failure for this lease clear, instead of latching for the process's
                // lifetime over one full disk.
                _faults.ReportHealthy(PersistFaultKey(leaseId));

                _pendingResults.Add(new LeaseCommandResult(
                    requestId, pending.Success, leaseId, completion.ResultJson));
                break;

            case EffectOutcome.AlreadyDone:
                // A client retrying after a lost reply, not a second request. Drop the copy created for
                // the retry and hand back the answer the first attempt produced.
                if (tracked?.Commit == LeaseCommitState.Provisional)
                {
                    _leases.Remove(leaseId);
                }

                _pendingResults.Add(new LeaseCommandResult(
                    requestId, LeaseCommandStatus.AlreadyDone, leaseId, completion.ResultJson));
                break;

            case EffectOutcome.Conflict:
                if (tracked?.Commit == LeaseCommitState.Provisional)
                {
                    _leases.Remove(leaseId);
                }

                _pendingResults.Add(new LeaseCommandResult(
                    requestId,
                    LeaseCommandStatus.Rejected,
                    leaseId,
                    Error: "That request identifier has already been used for a different request."));
                break;

            default:
                Fail(pending, tracked, completion);
                break;
        }
    }

    private void Fail(PendingEffect pending, TrackedLease? tracked, EffectCompletion completion)
    {
        var leaseId = pending.LeaseId!;

        if (pending.Kind == EffectKind.PersistLeaseRelease)
        {
            // Protection must not be lifted on the strength of a write that failed. The lease goes back to
            // holding, and the failure is latched, which is itself another reason to stay awake.
            if (tracked is not null)
            {
                tracked.Commit = LeaseCommitState.Committed;
                tracked.Lease = tracked.Lease with { Status = LeaseStatus.Active, EndedAtUtc = null, EndReason = null };
            }
        }
        else if (tracked?.Commit == LeaseCommitState.Provisional)
        {
            // The lease never existed. Removing it does reduce protection, but the latched fault below
            // holds the machine awake regardless, so the net direction is still safe.
            _leases.Remove(leaseId);
        }

        _faults.Report(
            PersistFaultKey(leaseId),
            FaultSeverity.Transient,
            $"A lease change could not be made durable: {completion.Error}",
            _clock.UtcNow);

        _pendingResults.Add(new LeaseCommandResult(
            pending.RequestId!, LeaseCommandStatus.Failed, leaseId, Error: completion.Error));
    }

    /// <summary>
    /// One key per lease, so a write failure is reported against the lease it belongs to and can be cleared
    /// when that lease succeeds or ends. Without the clearing, the fault list -- and therefore the inhibitor
    /// list -- would grow by one entry for every lease that ever failed to persist.
    /// </summary>
    private static string PersistFaultKey(string leaseId) => $"lease-persist:{leaseId}";

    private void Reject(LeaseCommand command, string reason) =>
        _pendingResults.Add(new LeaseCommandResult(
            command.RequestId, LeaseCommandStatus.Rejected, command.LeaseId, Error: reason));

    /// <summary>
    /// Queue an effect and remember it, so its outcome can be routed back.
    /// <para>
    /// Everything is registered, including effects nobody is waiting on. An unregistered effect's completion
    /// cannot be matched to anything, so its failure would be discarded without a trace -- which for the record
    /// that a lease ended means a row left saying "active" and that lease coming back on the next restart.
    /// </para>
    /// </summary>
    private void Emit(KernelEffect effect)
    {
        _inFlight[effect.EffectId] = new PendingEffect
        {
            Kind = effect.Kind,
            LeaseId = effect.Lease?.Id,
            IssuedAtRevision = _revision
        };

        _pendingEffects.Add(effect);
    }

    private ObservationDisposition Distrust(
        string sourceId,
        SourceStamp stamp,
        bool adoptBaseline,
        ObservationDisposition disposition,
        string reason)
    {
        if (!_sources.TryGetValue(sourceId, out var entry))
        {
            entry = new SourceEntry();
            _sources[sourceId] = entry;
        }

        if (adoptBaseline)
        {
            // Take the new position so the next contiguous report is believed again. Without this the
            // source could never recover from one gap.
            entry.Stamp = stamp;
            entry.HasBaseline = true;
            _producers.Heartbeat(sourceId, stamp.ObservedAt);
        }

        entry.Distrusted = reason;
        entry.LastDisposition = disposition;
        return disposition;
    }

    /// <summary>
    /// Follow a change of configuration: forget the sources that are gone and expect the ones that are new.
    /// <para>
    /// Switching a rule off is an ordinary thing for a user to do. Without this, the source behind it would
    /// stay in the table, go stale, and be turned into a reason to stay awake on every cycle from then on --
    /// so the machine would never sleep again until the service restarted, and status would blame a producer
    /// that is no longer configured. The direction is safe, but the release path would be permanently dead.
    /// </para>
    /// </summary>
    /// <summary>
    /// Forget effects nobody ever reported back.
    /// <para>
    /// The host is supposed to report every effect exactly once. If it dies between issuing a write and
    /// reporting it, or is replaced, the entry would otherwise sit here for the lifetime of a process designed
    /// to run for months. Dropping one is safe: an unconfirmed lease stays provisional and expires on its own
    /// deadline, so protection is not affected either way.
    /// </para>
    /// </summary>
    private void DropAbandonedEffects()
    {
        if (_revision <= AbandonedEffectRevisions)
        {
            return;
        }

        var cutoff = _revision - AbandonedEffectRevisions;
        foreach (var effectId in _inFlight
            .Where(entry => entry.Value.IssuedAtRevision < cutoff)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _inFlight.Remove(effectId);
        }
    }

    private void Reconfigure(IReadOnlyList<string> expectedSources)
    {
        ArgumentNullException.ThrowIfNull(expectedSources);

        _expectedSources = expectedSources;
        _producers = new ExpectedProducerSet(expectedSources, _options.HeartbeatFreshness);

        var expected = new HashSet<string>(expectedSources, StringComparer.Ordinal);
        foreach (var sourceId in _sources.Keys.Where(id => !expected.Contains(id)).ToArray())
        {
            _sources.Remove(sourceId);
        }
    }

    private void DiscardEverySourceObservation(string reason)
    {
        foreach (var entry in _sources.Values)
        {
            entry.Distrusted = $"Report discarded because {reason}.";
            entry.HasBaseline = false;
        }
    }

    private void TryLowerEmergencyLatch(MonotonicStamp now)
    {
        if (!_emergency.IsRaised)
        {
            _resyncDemandedAt = null;
            return;
        }

        if (_resyncDemandedAt is not { } since || since.EpochId != now.EpochId)
        {
            // From this point on, every expected source has to report afresh. Anything observed before the
            // alarm was raised describes the situation that caused it.
            _resyncDemandedAt = now;
            return;
        }

        foreach (var expected in _expectedSources)
        {
            if (!_sources.TryGetValue(expected, out var entry)
                || entry.Distrusted is not null
                || entry.LastDisposition != ObservationDisposition.Accepted
                || entry.Stamp.ObservedAt.EpochId != now.EpochId

                // Strictly after, not at or after. An observation taken at the same instant the alarm was
                // raised cannot show that the source has looked at the world again since.
                || entry.Stamp.ObservedAt.Elapsed <= since.Elapsed)
            {
                return;
            }
        }

        if (!_producers.AllHealthy(now))
        {
            return;
        }

        _emergency.Clear();
        _resyncDemandedAt = null;
    }

    private sealed class SourceEntry
    {
        public SourceStamp Stamp { get; set; }

        public InhibitorSourceReport Report { get; set; } = InhibitorSourceReport.Indeterminate("unset", "no report yet");

        public ObservationDisposition LastDisposition { get; set; }

        public bool HasBaseline { get; set; }

        /// <summary>Why this source is not being believed, or null when it is.</summary>
        public string? Distrusted { get; set; }
    }

    private enum LeaseCommitState
    {
        Provisional,
        Committed,
        ReleasePending
    }

    private sealed class TrackedLease
    {
        public required KeepAwakeLease Lease { get; set; }

        public required LeaseDeadline Deadline { get; set; }

        public required LeaseCommitState Commit { get; set; }

        public required string OwnerSid { get; init; }
    }

    private sealed class PendingEffect
    {
        public required EffectKind Kind { get; init; }

        public string? LeaseId { get; init; }

        public string? RequestId { get; init; }

        public LeaseCommandStatus Success { get; init; }

        /// <summary>The revision that issued this, so an effect nobody ever reports back can be dropped.</summary>
        public long IssuedAtRevision { get; init; }
    }
}
