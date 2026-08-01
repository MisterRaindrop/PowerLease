using System.Threading.Channels;
using PowerLease.Application.Kernel;
using PowerLease.Domain;

namespace PowerLease.Application.Hosting;

/// <summary>
/// Drives the kernel: takes in what producers and callers have posted, evaluates once, then carries out the
/// durable work and reports what happened.
/// <para>
/// Everything the kernel is not allowed to do lives here. It is also where the guarantees the kernel depends on
/// are actually kept, rather than being written down and hoped for:
/// </para>
/// <list type="bullet">
/// <item>every effect is reported back exactly once, including one whose executor threw or was cancelled --
/// an effect that is issued and never answered leaves a lease provisional and a caller waiting;</item>
/// <item>measurements are queued in a bounded channel, and a drop is reported to the kernel rather than
/// swallowed, because a lost report may have been the one saying something was happening;</item>
/// <item>anything that changes what the kernel believes -- a resume, a configuration change, a fault -- goes on
/// an unbounded channel and is never dropped.</item>
/// </list>
/// <para>
/// Pumping is a single call rather than an internal loop, so a test can drive it one turn at a time and a host
/// can decide its own cadence.
/// </para>
/// </summary>
public sealed class KernelLoop
{
    private readonly InhibitKernel _kernel;
    private readonly IEffectExecutor _effects;
    private readonly ITimeZoneProvider _timeZones;

    private readonly Channel<SourceObservation> _observations;
    private readonly Channel<ControlMessage> _control = Channel.CreateUnbounded<ControlMessage>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<LeaseCommand> _commands = Channel.CreateUnbounded<LeaseCommand>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<string, long> _dropped = new(StringComparer.Ordinal);
    private readonly Lock _dropLock = new();

    /// <param name="observationCapacity">
    /// How many measurements may be waiting before the oldest is dropped. Bounded on purpose: an unbounded queue
    /// in front of a loop that has fallen behind trades a visible drop for invisible memory growth and
    /// ever-staler data.
    /// </param>
    public KernelLoop(
        InhibitKernel kernel,
        IEffectExecutor effects,
        ITimeZoneProvider timeZones,
        int observationCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(timeZones);
        ArgumentOutOfRangeException.ThrowIfLessThan(observationCapacity, 1);

        _kernel = kernel;
        _effects = effects;
        _timeZones = timeZones;

        _observations = Channel.CreateBounded<SourceObservation>(
            new BoundedChannelOptions(observationCapacity)
            {
                SingleReader = true,

                // Oldest, never newest. Dropping the newest would leave the kernel holding a stale report while
                // the current one was thrown away, which is the one thing that can turn a busy machine into a
                // positive claim that nothing is happening. The drop is reported either way -- a full queue means
                // the loop has fallen behind, and a source it cannot keep up with is not one to believe until it
                // has been heard cleanly.
                FullMode = BoundedChannelFullMode.DropOldest
            },
            dropped => RecordDrop(dropped.Stamp.SourceId));
    }

    /// <summary>The kernel's published state. Safe to read from any thread.</summary>
    public KernelSnapshot Snapshot => _kernel.Snapshot;

    /// <summary>
    /// Post a measurement. Never blocks, and never throws: if the queue is full the oldest is dropped and the
    /// kernel is told, which is what stops a full queue from quietly becoming a claim that nothing is happening.
    /// </summary>
    public void Post(SourceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _observations.Writer.TryWrite(observation);
    }

    /// <summary>Post something that changes what the kernel believes. Never dropped.</summary>
    public void Post(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The time zone is cached, so the event that says it changed is also the moment to discard the cache.
        // Doing it here rather than on every read keeps a process-wide side effect off the evaluation path.
        if (message is TimeAdjusted)
        {
            _timeZones.Refresh();
        }

        _control.Writer.TryWrite(message);
    }

    /// <summary>Post a command. Never dropped: a caller is waiting for the answer.</summary>
    public void Post(LeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Writer.TryWrite(command);
    }

    /// <summary>Force the machine awake from any thread, without waiting for a turn.</summary>
    public bool RaiseEmergencyInhibit(string reason) => _kernel.RaiseEmergencyInhibit(reason);

    /// <summary>
    /// One turn: drain what has arrived, evaluate, then carry out the durable work and feed the outcomes back.
    /// </summary>
    /// <returns>The answers owed to callers, from this turn and any earlier effects that finished during it.</returns>
    public async Task<KernelStepResult> PumpAsync(CancellationToken cancellationToken = default)
    {
        // What changes belief first: a resume invalidates every measurement, so applying the measurements
        // before it would let a report taken before the machine slept be believed for a turn.
        while (_control.Reader.TryRead(out var message))
        {
            _kernel.Apply(message);
        }

        while (_observations.Reader.TryRead(out var observation))
        {
            _kernel.Apply(observation);
        }

        // Drops are applied last, after the reports that survived, so distrusting the source is not immediately
        // undone by the newest of them.
        DrainDrops();

        while (_commands.Reader.TryRead(out var command))
        {
            _kernel.Execute(command);
        }

        var result = _kernel.Step();

        foreach (var effect in result.Effects)
        {
            _kernel.Apply(new EffectFinished(await RunAsync(effect, cancellationToken).ConfigureAwait(false)));
        }

        return result;
    }

    /// <summary>
    /// Run one effect, turning anything at all into a completion.
    /// <para>
    /// Cancellation is reported as a failure rather than propagated. An effect that is issued and never answered
    /// leaves the lease it belongs to provisional and the caller waiting for ever, so shutting down must still
    /// close the loop on it -- and a failure is the safe answer, since the kernel responds to one by keeping
    /// protection rather than giving it up.
    /// </para>
    /// </summary>
    private async Task<EffectCompletion> RunAsync(KernelEffect effect, CancellationToken cancellationToken)
    {
        try
        {
            return await _effects.ExecuteAsync(effect, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EffectCompletion(
                effect.EffectId, EffectOutcome.Failed, Error: "the service was shutting down");
        }
#pragma warning disable CA1031 // The whole point is that no executor failure may escape and strand an effect.
        catch (Exception error)
#pragma warning restore CA1031
        {
            return new EffectCompletion(effect.EffectId, EffectOutcome.Failed, Error: error.Message);
        }
    }

    private void RecordDrop(string sourceId)
    {
        // Called by the channel on the posting thread, which is any producer's thread.
        lock (_dropLock)
        {
            _dropped[sourceId] = _dropped.GetValueOrDefault(sourceId) + 1;
        }
    }

    private void DrainDrops()
    {
        KeyValuePair<string, long>[] drops;
        lock (_dropLock)
        {
            if (_dropped.Count == 0)
            {
                return;
            }

            drops = [.. _dropped];
            _dropped.Clear();
        }

        foreach (var (sourceId, count) in drops)
        {
            _kernel.Apply(new SourceReportsDropped(sourceId, count));
        }
    }
}
